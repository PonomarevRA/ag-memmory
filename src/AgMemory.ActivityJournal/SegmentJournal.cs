using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgMemory.ActivityJournal;

/// <summary>Serializes readers and writers across local processes and commits bounded sequential JSONL segments.</summary>
public sealed class SegmentJournal : IDisposable
{
    private const long MetadataReserve = 8192;
    private readonly string _directory;
    private readonly ActivityJournalOptions _options;
    private readonly Mutex _mutex;
    private readonly object _gate = new();
    private bool _initialized;
    private bool _disposed;
    public string? RecoveryCode { get; private set; }
    /// <summary>Cheap cross-process commit hint. No event segment is opened for an unchanged idle subscription.</summary>
    public long Revision => File.GetLastWriteTimeUtc(Path.Combine(_directory, "manifest.json")).Ticks;

    public SegmentJournal(string directory, ActivityJournalOptions options)
    {
        options.Validate();
        _directory = Path.GetFullPath(directory);
        _options = options;
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_directory)));
        _mutex = new Mutex(false, "AgMemory.ActivityJournal." + identity);
        // Activate service settings at startup. An old idle instance must not apply stale options on its first event.
        try { Locked(() => { Open(); return 0; }); }
        catch { _mutex.Dispose(); throw; }
    }

    /// <summary>Commits a prepared batch once. Sequence assignment, tail recovery, eviction and manifest replace share one process lock.</summary>
    public void Append(IReadOnlyList<StoredActivity> pending, long dropped = 0)
    {
        if (pending.Count == 0) return;
        Locked(() =>
        {
            var state = Open();
            var lines = new List<byte[]>();
            var sequence = state.Sequence;
            foreach (var item in pending)
            {
                var recorded = item.Event with { JournalEpoch = state.Epoch, Sequence = ActivityJson.Number(++sequence), RecordedAt = DateTimeOffset.UtcNow };
                var line = JsonSerializer.SerializeToUtf8Bytes(item with { Event = recorded }, ActivityJson.Options);
                if (line.Length > 8192) throw new ArgumentException("Activity event exceeds its byte budget.");
                lines.Add(line);
            }
            // Leave metadata headroom at every intermediate write, including the replacement manifest.
            var length = lines.Sum(line => line.Length + 1L);
            if (length > _options.MaxSizeBytes - MetadataReserve) throw new ArgumentException("Activity batch exceeds quota.");
            var segmentLimit = Math.Min(1_048_576, (_options.MaxSizeBytes - MetadataReserve) / 4);
            var activePath = state.Segment is null ? null : Path.Combine(_directory, state.Segment);
            var rotate = activePath is null || state.CommittedBytes + length > segmentLimit;
            if (rotate)
            {
                state = state with { Segment = $"segment-{state.Sequence + 1:D20}.jsonl", CommittedBytes = 0 };
                activePath = Path.Combine(_directory, state.Segment);
            }
            var metadataBudget = Math.Max(MetadataReserve, JsonSerializer.SerializeToUtf8Bytes(state, ActivityJson.Options).Length * 2L + 2048);
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

    /// <summary>Reads an authorized recent or forward window. Filtering precedes projection and never changes the fixed partition.</summary>
    public ActivityRead Read(string partition, int limit = 100, long? after = null, long? before = null,
        string? session = null, string? operation = null, string? query = null, Func<ActivityEvent, bool>? filter = null, Func<ActivityStatus, bool>? windowFilter = null)
    {
        return Locked(() =>
        {
            var state = Open();
            var status = StatusCore(state, partition);
            // Idle forward polling and invalid windows inspect metadata only, never event contents.
            if (after >= state.Sequence || windowFilter is not null && !windowFilter(status)) return new ActivityRead([], status, false);
            var cutoff = state.Partitions?.GetValueOrDefault(partition);
            var events = new List<StoredActivity>();
            var paths = Segments();
            if (after is null) Array.Reverse(paths);
            foreach (var path in paths)
            {
                var commit = state.Segments?.FirstOrDefault(value => value.Name == Path.GetFileName(path));
                if (commit is not null && (after is not null && commit.Last <= after || before is not null && commit.First >= before)) continue;
                var rows = ReadSegment(path);
                if (after is null) rows.Reverse();
                foreach (var item in rows)
                {
                    if (item.Partition != partition || cutoff is not null && ActivityJson.Sequence(item.Event.Sequence) < cutoff.StartSequence || !Matches(item.Event, after, before, session, operation, query)) continue;
                    if (filter is not null && !filter(item.Event)) continue;
                    events.Add(item with { Event = item.Event with { JournalEpoch = cutoff?.Epoch ?? state.Epoch } });
                    if (events.Count > limit) break;
                }
                if (events.Count > limit) break;
            }
            var more = events.Count > limit;
            if (more) events.RemoveAt(events.Count - 1);
            if (after is null) events.Reverse();
            return new ActivityRead(events, status, more);
        });
    }

    public ActivityStatus Status(string? partition = null) => Locked(() => StatusCore(Open(), partition));

    /// <summary>Invalidates only one authorized partition. Its old metadata is logically hidden and naturally evicted under the app quota.</summary>
    public ActivityStatus ClearPartition(string partition) => Locked(() =>
    {
        var state = Open();
        var partitions = new Dictionary<string, PartitionBoundary>(state.Partitions ?? [], StringComparer.Ordinal);
        if (!partitions.ContainsKey(partition) && partitions.Count >= 16) throw new InvalidOperationException("clear_partition_capacity");
        partitions[partition] = new(Guid.NewGuid().ToString("N"), state.Sequence + 1);
        state = state with { Partitions = partitions };
        Save(state);
        return StatusCore(state, partition);
    });

    /// <summary>Clears only this app journal, preserving the quota and invalidating every previous cursor.</summary>
    public ActivityStatus Clear() => Locked(() =>
    {
        foreach (var path in Segments()) File.Delete(path);
        var state = new Manifest(Guid.NewGuid().ToString("N"), _options.MaxSizeBytes, 0, null, 0);
        Save(state);
        return StatusCore(state);
    });

    /// <summary>Recovers any uncommitted tail before a reader can see it; incompatible running writers fail closed.</summary>
    private Manifest Open()
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var statePath = Path.Combine(_directory, "manifest.json");
        Manifest? state = null;
        try { if (File.Exists(statePath)) state = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(statePath), ActivityJson.Options); }
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, ActivityJson.Options);
        if (UsedBytes() + bytes.Length > _options.MaxSizeBytes) throw new IOException("quota_unavailable");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    private ActivityStatus StatusCore(Manifest state, string? partition = null)
    {
        var paths = Segments();
        var oldest = state.Sequence + 1;
        if (paths.Length > 0)
        {
            var name = Path.GetFileName(paths[0]);
            oldest = long.Parse(name.AsSpan(8, 20), System.Globalization.CultureInfo.InvariantCulture);
        }
        var boundary = partition is null ? null : state.Partitions?.GetValueOrDefault(partition);
        oldest = Math.Max(oldest, boundary?.StartSequence ?? 0);
        var epoch = boundary?.Epoch ?? state.Epoch;
        var latest = ActivityJson.Number(state.Sequence);
        return new("agent-activity/v1", epoch, _options.MaxSizeBytes, UsedBytes(), ActivityJson.Number(oldest), latest,
            new("healthy", state.Dropped, 0, 0, RecoveryCode), new(epoch, latest));
    }

    private long UsedBytes() => Directory.Exists(_directory) ? Directory.GetFiles(_directory).Sum(path => new FileInfo(path).Length) : 0;
    private string[] Segments() => Directory.Exists(_directory) ? Directory.GetFiles(_directory, "segment-*.jsonl").Order(StringComparer.Ordinal).ToArray() : [];
    private static List<StoredActivity> ReadSegment(string path)
    {
        var result = new List<StoredActivity>();
        foreach (var line in File.ReadLines(path))
        {
            var item = JsonSerializer.Deserialize<StoredActivity>(line, ActivityJson.Options) ?? throw new IOException("invalid_event");
            result.Add(item);
        }
        return result;
    }

    private static bool Matches(ActivityEvent value, long? after, long? before, string? session, string? operation, string? query)
    {
        var sequence = ActivityJson.Sequence(value.Sequence);
        if (after is not null && sequence <= after || before is not null && sequence >= before) return false;
        if (session is not null && value.SessionId != session || operation is not null && value.Operation != operation) return false;
        // Search includes only public operation metadata and the permitted sanitized search preview.
        return string.IsNullOrEmpty(query) || value.Operation.Contains(query, StringComparison.OrdinalIgnoreCase) || value.ClientKind.Contains(query, StringComparison.OrdinalIgnoreCase) || (value.Query.Preview?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
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
    private sealed record PartitionBoundary(string Epoch, long StartSequence);
    private sealed record SegmentCommit(string Name, long First, long Last, long Bytes);
    private sealed record Manifest(string Epoch, long MaxSizeBytes, long Sequence, string? Segment, long CommittedBytes, Dictionary<string, PartitionBoundary>? Partitions = null, long Dropped = 0, List<SegmentCommit>? Segments = null);
}
