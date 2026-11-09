using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

/// <summary>
/// Commits complete knowledge documents with same-directory atomic rename, independently of LanceDB tables.
/// The lease coordinates cooperating clients. File flush protects contents before publication; power-loss
/// durability of the directory entry depends on the filesystem. The configured parent must be trusted.
/// </summary>
public sealed partial class KnowledgeFileStore : IKnowledgeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };
    private readonly string _storagePath;
    private readonly string _directory;
    private readonly string _leasePath;
    private readonly StorageLeaseGate _gate;
    private readonly KnowledgeFileStoreOptions _options;

    /// <summary>Uses only a server-configured storage root; profile keys are opaque server authority identifiers.</summary>
    public KnowledgeFileStore(string storagePath, KnowledgeFileStoreOptions? options = null)
        : this(storagePath, options, false) { }

    /// <summary>Uses a separate schema-v1 namespace so older shared-context writers cannot discard task state.</summary>
    public static KnowledgeFileStore ForTaskWorking(string storagePath, KnowledgeFileStoreOptions? options = null) =>
        new(storagePath, options, true);

    private KnowledgeFileStore(string storagePath, KnowledgeFileStoreOptions? options, bool taskWorking)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        _storagePath = Path.GetFullPath(storagePath);
        _directory = Path.Combine(_storagePath, taskWorking ? ".agmemory-working" : ".agmemory-knowledge");
        var leaseName = taskWorking ? ".working.lock" : ".knowledge.lock";
        _leasePath = Path.Combine(_directory, leaseName);
        _options = options ?? new();
        _options.Validate();
        _gate = new StorageLeaseGate(_directory, leaseName);
    }

    /// <summary>Reads the latest committed document, or an empty version-zero state; corruption is never reset silently.</summary>
    public async Task<KnowledgeState> ReadAsync(string profileKey, CancellationToken cancellationToken)
    {
        var path = DocumentPath(profileKey);
        cancellationToken.ThrowIfCancellationRequested();
        PrepareDirectory();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidatePaths(path);
            ProtectLease();
            CleanupTemps(path);
            return await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically publishes state and receipts if the persisted version still matches; false means retry from a fresh read.</summary>
    public async Task<bool> CompareExchangeAsync(string profileKey, long expectedVersion, KnowledgeState replacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || replacement.Version != expectedVersion + 1)
            throw new ArgumentException("Replacement must advance the expected nonnegative version by one.", nameof(replacement));
        ValidateState(replacement);
        var path = DocumentPath(profileKey);
        cancellationToken.ThrowIfCancellationRequested();
        PrepareDirectory();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidatePaths(path);
            ProtectLease();
            CleanupTemps(path);
            var current = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            if (current.Version != expectedVersion) return false;
            EnsureProfileCapacity(path);
            Publish(path, replacement, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Hashes opaque keys so neither separators nor a caller-selected path can escape the configured directory.</summary>
    private string DocumentPath(string profileKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileKey);
        if (profileKey.Length > 1024 || profileKey.Any(char.IsControl)) throw new ArgumentException("Invalid profile key.", nameof(profileKey));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profileKey)));
        return Path.Combine(_directory, hash + ".json");
    }

    /// <summary>Creates a private sidecar directory, rejecting direct root and sidecar symlinks before opening the lease.</summary>
    private void PrepareDirectory()
    {
        RejectLink(_storagePath);
        RejectLink(_directory);
        RejectLink(_leasePath);
        Directory.CreateDirectory(_storagePath);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Rechecks controlled paths after acquiring the lease; trusted ancestry is an explicit deployment requirement.</summary>
    private void ValidatePaths(string path)
    {
        RejectLink(_storagePath);
        RejectLink(_directory);
        RejectLink(_leasePath);
        RejectLink(path);
    }

    /// <summary>Rejects existing symbolic links, including dangling ones, without following their destination.</summary>
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException("Knowledge storage paths must not be symbolic links.");
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Knowledge storage paths must not be reparse points.");
    }

    /// <summary>Restricts the persistent lock file to the owning user on Unix.</summary>
    private void ProtectLease()
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_leasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Bounds reads before allocating and rejects malformed or future-schema documents instead of overwriting them.</summary>
    private async Task<KnowledgeState> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > _options.MaximumDocumentBytes) throw new InvalidDataException("Knowledge document exceeds its size limit.");
        try
        {
            var state = await JsonSerializer.DeserializeAsync<KnowledgeState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (state is null) throw new InvalidDataException("Knowledge document is null.");
            ValidateState(state);
            return state;
        }
        catch (JsonException error) { throw new InvalidDataException("Knowledge document is malformed.", error); }
    }

    /// <summary>Checks envelope and aggregate bounds; domain-level rules remain in the application service.</summary>
    private void ValidateState(KnowledgeState state)
    {
        if (state.SchemaVersion != 1 || state.Version < 0) throw new InvalidDataException("Unsupported knowledge schema or version.");
        if (state.Metadata is null || state.Presentations is null || state.Feedback is null || state.Procedures is null ||
            state.ProcedureHistory is null || state.Occurrences is null || state.Applications is null || state.Warm is null || state.Requests is null)
            throw new InvalidDataException("Knowledge collections must be present.");
        var count = (long)state.Metadata.Count + state.Presentations.Count + state.Feedback.Count + state.Procedures.Count +
            state.ProcedureHistory.Count + state.Occurrences.Count + state.Applications.Count + state.Warm.Count + state.Requests.Count;
        if (count > _options.MaximumEntries) throw new InvalidDataException("Knowledge aggregate exceeds its entry limit.");
        RejectNullValues(state.Metadata);
        RejectNullValues(state.Presentations);
        RejectNullValues(state.Feedback);
        RejectNullValues(state.Procedures);
        RejectNullValues(state.ProcedureHistory);
        RejectNullValues(state.Occurrences);
        RejectNullValues(state.Applications);
        RejectNullValues(state.Warm);
        RejectNullValues(state.Requests);
    }

    /// <summary>Prevents malformed persisted null entries from reaching domain operations that require typed records.</summary>
    private static void RejectNullValues<T>(Dictionary<string, T> values)
    {
        foreach (var value in values.Values)
            if (value is null) throw new InvalidDataException("Knowledge collection entries must not be null.");
    }

    /// <summary>Bounds profile growth under the same lease that publishes new documents.</summary>
    private void EnsureProfileCapacity(string path)
    {
        if (File.Exists(path)) return;
        var count = 0;
        foreach (var unused in Directory.EnumerateFiles(_directory, "*.json"))
            if (++count >= _options.MaximumProfiles) throw new InvalidDataException("Knowledge profile capacity reached.");
    }

    /// <summary>Writes and flushes a private temporary file, then publishes one complete generation with atomic replacement.</summary>
    private void Publish(string path, KnowledgeState state, CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporaryPath, options))
            {
                using var bounded = new KnowledgeBoundedWriteStream(file, _options.MaximumDocumentBytes);
                JsonSerializer.Serialize(bounded, state, JsonOptions);
                cancellationToken.ThrowIfCancellationRequested();
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(path);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    /// <summary>Removes only this profile's abandoned pre-commit files while all cooperating writers are excluded.</summary>
    private void CleanupTemps(string path)
    {
        foreach (var temporaryPath in Directory.EnumerateFiles(_directory, Path.GetFileName(path) + ".*.tmp"))
        {
            RejectLink(temporaryPath);
            File.Delete(temporaryPath);
        }
    }
}
