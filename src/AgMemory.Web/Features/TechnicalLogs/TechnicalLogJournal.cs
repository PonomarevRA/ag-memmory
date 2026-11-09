using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgMemory.Web.Features.TechnicalLogs;

/// <summary>Serializes readers and writers across local processes and commits bounded sequential JSONL segments.</summary>
internal sealed class TechnicalLogJournal : IDisposable
{
    private const long MetadataReserve = 16384;
    private readonly string _directory;
    private readonly TechnicalLogOptions _options;
    private readonly Mutex _mutex;
    private readonly object _gate = new();
    private bool _initialized;
    private bool _disposed;
    public string? RecoveryCode { get; private set; }
    /// <summary>Cheap cross-process commit hint. No event segment is opened for an unchanged idle subscription.</summary>
    public long Revision => File.GetLastWriteTimeUtc(Path.Combine(_directory, "manifest.json")).Ticks;

    /// <summary>Activates the service quota under its named path mutex; incompatible old instances subsequently fail closed.</summary>
    public TechnicalLogJournal(string directory, TechnicalLogOptions options)
    {
        options.Validate();
        _directory = Path.GetFullPath(directory);
        _options = options;
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_directory)));
        _mutex = new Mutex(false, "AgMemory.TechnicalLogs." + identity);
        // Activate service settings at startup. An old idle instance must not apply stale options on its first event.
        try { Locked(() => { Open(); return 0; }); }
        catch { _mutex.Dispose(); throw; }
    }

    /// <summary>Commits a prepared batch once. Sequence assignment, tail recovery, eviction and manifest replace share one process lock.</summary>
    public void Append(IReadOnlyList<TechnicalLogEvent> pending, long dropped = 0)
    {
        if (pending.Count == 0) return;
        Locked(() =>
        {
            var state = Open();
            var lines = new List<byte[]>();
            var sequence = state.Sequence;
            foreach (var item in pending)
            {
                var recorded = item with { JournalEpoch = state.Epoch, Sequence = TechnicalLogJson.Number(sequence = checked(sequence + 1)), RecordedAt = DateTimeOffset.UtcNow };
                var line = JsonSerializer.SerializeToUtf8Bytes(recorded, TechnicalLogJson.Options);
                if (line.Length > 16384) throw new ArgumentException("Technical event exceeds its byte budget.");
                lines.Add(line);
            }
            // Leave metadata headroom at every intermediate write, including the replacement manifest.
            var length = lines.Sum(line => line.Length + 1L);
            if (length > _options.MaxSizeBytes - MetadataReserve) throw new ArgumentException("Technical batch exceeds quota.");
            var segmentLimit = Math.Min(1_048_576, (_options.MaxSizeBytes - MetadataReserve) / 4);
            var activePath = state.Segment is null ? null : Path.Combine(_directory, state.Segment);
            var rotate = activePath is null || state.CommittedBytes + length > segmentLimit;
            if (rotate)
            {
                state = state with { Segment = $"segment-{state.Sequence + 1:D20}.jsonl", CommittedBytes = 0 };
                activePath = Path.Combine(_directory, state.Segment);
            }
            var metadataBudget = Math.Max(MetadataReserve, JsonSerializer.SerializeToUtf8Bytes(state, TechnicalLogJson.Options).Length * 2L + 2048);
            EvictFor(length + metadataBudget, activePath);
            using (var stream = new FileStream(activePath!, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                stream.Position = state.CommittedBytes;
                foreach (var line in lines) { stream.Write(line); stream.WriteByte((byte)'\n'); }
                stream.Flush(flushToDisk: true);
            }
            var commits = new List<SegmentCommit>();
            foreach (var commit in state.Segments ?? [])
                if (commit.Name != state.Segment && File.Exists(Path.Combine(_directory, commit.Name))) commits.Add(commit);
            var firstSequence = state.Sequence + 1;
            var previousActive = state.Segments?.FirstOrDefault(commit => commit.Name == state.Segment);
            if (previousActive is not null) firstSequence = previousActive.First;
            commits.Add(new(state.Segment!, firstSequence, sequence, state.CommittedBytes + length));
            Save(state with { Sequence = sequence, CommittedBytes = state.CommittedBytes + length, Dropped = state.Dropped + dropped, Segments = commits });
            return 0;
        });
    }

    /// <summary>Reads one captured commit window, skipping event files for an idle or invalid cursor.</summary>
    public TechnicalLogRead Read(TechnicalLogQuery query)
    {
        var result = Locked<TechnicalLogRead>(() =>
    {
        var state = Open();
        var status = StatusCore(state);
        if (!query.IsCurrent(status) || query.After >= state.Sequence) return new([], status, false);
        var events = new List<TechnicalLogEvent>();
        var commits = (state.Segments ?? []).ToList();
        if (!query.Forward) commits.Reverse();
        foreach (var commit in commits)
        {
            if (query.After is not null && commit.Last <= query.After) continue;
            if (query.Before is not null && commit.First >= query.Before) continue;
            var rows = ReadSegment(Path.Combine(_directory, commit.Name), commit, state.Epoch);
            if (!query.Forward) rows.Reverse();
            foreach (var item in rows)
            {
                var sequence = TechnicalLogJson.Sequence(item.Sequence);
                if (query.After is not null && sequence <= query.After) continue;
                if (query.Before is not null && sequence >= query.Before) continue;
                events.Add(item);
                if (events.Count > query.Limit) break;
            }
            if (events.Count > query.Limit) break;
        }
        var more = events.Count > query.Limit;
        if (more) events.RemoveAt(events.Count - 1);
        if (!query.Forward) events.Reverse();
        return new(events, status, more);
        });
        return result;
    }

    public TechnicalLogStatus Status() => Locked(() => StatusCore(Open()));

    /// <summary>Recovers any uncommitted tail before a reader can see it; incompatible running writers fail closed.</summary>
    private Manifest Open()
    {
        Directory.CreateDirectory(_directory);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("journal_symlink");
        foreach (var entry in Directory.EnumerateFileSystemEntries(_directory))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("journal_symlink");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Combine(_directory, "manifest.json");
        Manifest? state = null;
        try { if (File.Exists(statePath)) state = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(statePath), TechnicalLogJson.Options); }
        catch (JsonException) { RecoveryCode = "recovery_reset"; }
        if (state is null)
        {
            foreach (var path in Segments()) File.Delete(path);
            state = new(Guid.NewGuid().ToString("N"), _options.MaxSizeBytes, 0, null, 0);
            Save(state);
        }
        foreach (var path in Directory.GetFiles(_directory, "*.tmp")) File.Delete(path);
        RecoverTail(state);
        if (state.MaxSizeBytes != _options.MaxSizeBytes)
        {
            if (_initialized) throw new InvalidOperationException("configuration_mismatch");
            // A new process activates a changed service quota. Old processes subsequently fail their equality check.
            EvictFor(MetadataReserve, null);
            state = state with { MaxSizeBytes = _options.MaxSizeBytes };
            if (state.Segment is not null && !File.Exists(Path.Combine(_directory, state.Segment)))
                state = state with { Segment = null, CommittedBytes = 0 };
            state = state with { Segments = (state.Segments ?? []).Where(commit => File.Exists(Path.Combine(_directory, commit.Name))).ToList() };
            Save(state);
        }
        _initialized = true;
        return state;
    }

    /// <summary>Removes orphan rotation files and truncates bytes not covered by the durable manifest.</summary>
    private void RecoverTail(Manifest state)
    {
        var retained = state.Segments ?? [];
        long? previous = null;
        foreach (var commit in retained)
        {
            if (!File.Exists(Path.Combine(_directory, commit.Name))) throw new IOException("committed_segment_missing");
            if (previous is not null && commit.First != previous + 1 || commit.Last < commit.First) throw new IOException("sequence_gap");
            if (commit.Name != state.Segment && new FileInfo(Path.Combine(_directory, commit.Name)).Length != commit.Bytes) throw new IOException("committed_segment_changed");
            previous = commit.Last;
        }
        if (state.Segment is not null && (retained.Count == 0 || retained[^1].Last != state.Sequence || retained[^1].Name != state.Segment))
            throw new IOException("committed_segment_missing");
        foreach (var path in Segments())
        {
            var name = Path.GetFileName(path);
            if (state.Segment is null || string.CompareOrdinal(name, state.Segment) > 0) { File.Delete(path); continue; }
            if (name != state.Segment) continue;
            var length = new FileInfo(path).Length;
            if (length < state.CommittedBytes) throw new IOException("committed_segment_missing");
            if (length > state.CommittedBytes)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                stream.SetLength(state.CommittedBytes);
                stream.Flush(true);
                RecoveryCode = "tail_recovered";
            }
        }
    }

    /// <summary>Evicts whole oldest segments so disk space stays bounded without rewriting retained history.</summary>
    private void EvictFor(long required, string? activePath)
    {
        var used = UsedBytes();
        foreach (var path in Segments())
        {
            if (used + required <= _options.MaxSizeBytes) break;
            if (path == activePath) continue;
            var length = new FileInfo(path).Length;
            File.Delete(path);
            used -= length;
        }
        if (used + required > _options.MaxSizeBytes) throw new IOException("quota_unavailable");
    }

    /// <summary>Writes a bounded replacement before rename; observers see either the previous or the complete new commit.</summary>
    private void Save(Manifest state)
    {
        var path = Path.Combine(_directory, "manifest.json");
        var temp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, TechnicalLogJson.Options);
        if (UsedBytes() + bytes.Length > _options.MaxSizeBytes) throw new IOException("quota_unavailable");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Builds durable bounds from committed metadata without opening event contents.</summary>
    private TechnicalLogStatus StatusCore(Manifest state)
    {
        var oldest = state.Segments is { Count: > 0 } ? state.Segments[0].First : state.Sequence + 1;
        var latest = TechnicalLogJson.Number(state.Sequence);
        return new("technical-logs-http/v1", "agmemory", "technical-log/v1", state.Epoch,
            TechnicalLogJson.Number(oldest), latest, _options.MaxSizeBytes, UsedBytes(), new(state.Epoch, latest),
            new(), new(RecoveryCode is null ? "ok" : "degraded", state.Dropped, null, null, RecoveryCode));
    }

    private long UsedBytes() => Directory.Exists(_directory) ? Directory.GetFiles(_directory).Sum(path => new FileInfo(path).Length) : 0;
    private string[] Segments() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "segment-*.jsonl").Order(StringComparer.Ordinal).ToArray() : [];

    /// <summary>Checks the selected committed range and epoch before replay; equal-length corruption must not silently change evidence order.</summary>
    private static List<TechnicalLogEvent> ReadSegment(string path, SegmentCommit commit, string epoch)
    {
        var result = new List<TechnicalLogEvent>();
        var expected = commit.First;
        foreach (var line in File.ReadLines(path))
        {
            if (expected == 0) throw new IOException("extra_segment_rows");
            if (Encoding.UTF8.GetByteCount(line) > 16384) throw new IOException("invalid_event_size");
            using var document = JsonDocument.Parse(line);
            foreach (var key in new[] { "schemaVersion", "eventId", "journalEpoch", "sequence", "application", "occurredAt", "recordedAt", "level", "category", "eventName", "messageTemplate", "message", "exception", "traceId", "spanId", "operationId", "sessionId", "taskId", "properties" })
                if (!document.RootElement.TryGetProperty(key, out _)) throw new IOException("incomplete_event");
            var item = document.RootElement.Deserialize<TechnicalLogEvent>(TechnicalLogJson.Options) ?? throw new IOException("invalid_event");
            if (item.SchemaVersion != "technical-log/v1" || item.Application != "agmemory" || item.JournalEpoch != epoch ||
                item.Sequence != TechnicalLogJson.Number(expected) || expected > commit.Last) throw new IOException("invalid_event_order");
            if (item.Level is not ("TRACE" or "DEBUG" or "INFO" or "WARN" or "ERROR" or "FATAL") || string.IsNullOrEmpty(item.EventId)) throw new IOException("invalid_event");
            result.Add(item);
            if (expected == commit.Last) expected = 0;
            else expected = checked(expected + 1);
        }
        if (result.Count == 0 || expected != 0) throw new IOException("incomplete_segment");
        return result;
    }

    /// <summary>Keeps the named mutex thread-affine: no await occurs while the cross-process lock is held.</summary>
    private T Locked<T>(Func<T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var acquired = false;
            try
            {
                try { acquired = _mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { acquired = true; RecoveryCode = "writer_recovered"; }
                if (!acquired) throw new IOException("writer_busy");
                return action();
            }
            finally { if (acquired) _mutex.ReleaseMutex(); }
        }
    }

    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _mutex.Dispose(); } }
    private sealed record SegmentCommit(string Name, long First, long Last, long Bytes);
    private sealed record Manifest(string Epoch, long MaxSizeBytes, long Sequence, string? Segment, long CommittedBytes, long Dropped = 0, List<SegmentCommit>? Segments = null);
}
