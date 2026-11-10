using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

/// <summary>Atomic private evidence sidecars, independent of legacy aggregates. Limits are128 profiles,4MiB and1024 entries per collection.</summary>
public sealed class KnowledgeAnnotationStore : IKnowledgeAnnotationStore
{
    private readonly string _root;
    private readonly string _leaseDirectory;
    private readonly string _directory;
    private readonly StorageLeaseGate _gate;
    private const int MaximumBytes = 4 * 1024 * 1024;

    public KnowledgeAnnotationStore(string storagePath) : this(storagePath, false) { }

    /// <summary>Gives short-lived notes a separate quota and document namespace under the same backup lease.</summary>
    public static KnowledgeAnnotationStore ForTemporaryNotes(string storagePath) => new(storagePath, true);

    private KnowledgeAnnotationStore(string storagePath, bool temporaryNotes)
    {
        _root = Path.GetFullPath(storagePath);
        _leaseDirectory = Path.Combine(_root, ".agmemory-annotations");
        _directory = temporaryNotes ? Path.Combine(_leaseDirectory, "temporary-notes") : _leaseDirectory;
        _gate = new(_leaseDirectory, ".annotations.lock");
    }

    public async Task<KnowledgeAnnotationState> ReadAsync(string profile, CancellationToken token)
    {
        var path = Prepare(profile);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { CheckPaths(path); CleanupTemps(path); return await ReadDocumentAsync(path, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Publishes a complete state only under the observed aggregate version; interrupted temp files are never read as state.</summary>
    public async Task<bool> CompareExchangeAsync(string profile, long expectedVersion, KnowledgeAnnotationState next, CancellationToken token)
    {
        Validate(next);
        if (expectedVersion < 0 || expectedVersion == long.MaxValue || next.Version != expectedVersion + 1)
            throw new ArgumentException("Annotation version must advance by one.");
        var path = Prepare(profile);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            CheckPaths(path);
            CleanupTemps(path);
            var current = await ReadDocumentAsync(path, token).ConfigureAwait(false);
            if (current.Version != expectedVersion || current.Version != 0 &&
                current.TemporaryNoteGeneration != next.TemporaryNoteGeneration) return false;
            if (!File.Exists(path)) await EnsureProfileCapacityAsync(token).ConfigureAwait(false);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                using var bounded = new KnowledgeBoundedWriteStream(file, MaximumBytes);
                JsonSerializer.Serialize(bounded, next);
                token.ThrowIfCancellationRequested(); file.Flush(true);
            }
            token.ThrowIfCancellationRequested(); CheckPaths(path);
            File.Move(temporary, path, true);
            return true;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Reclaims only fully expired note scopes; generation IDs fence stale writers after re-creation.</summary>
    private async Task EnsureProfileCapacityAsync(CancellationToken token)
    {
        const int maximumProfiles = 128;
        if (Directory.EnumerateFiles(_directory, "*.json").Take(maximumProfiles).Count() < maximumProfiles) return;
        if (_directory != _leaseDirectory)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-48);
            foreach (var candidate in Directory.EnumerateFiles(_directory, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                CheckPaths(candidate);
                var existing = await ReadDocumentAsync(candidate, token).ConfigureAwait(false);
                if (existing.TemporaryNotes?.Values.All(note => note.ExpiresAtUtc <= cutoff) != true) continue;
                File.Delete(candidate);
                if (Directory.EnumerateFiles(_directory, "*.json").Take(maximumProfiles).Count() < maximumProfiles) return;
            }
        }
        throw new InvalidOperationException("knowledge_capacity_exceeded");
    }

    /// <summary>Validates authority identifiers and paths before any lease can create or follow a file.</summary>
    private string Prepare(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 1024 || profile.Any(char.IsControl)) throw new ArgumentException("Invalid annotation profile.");
        var path = Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profile))) + ".json");
        CheckPaths(path);
        CreatePrivateDirectory(_leaseDirectory);
        if (_directory != _leaseDirectory) CreatePrivateDirectory(_directory);
        return path;
    }

    /// <summary>Creates controlled document directories with owner-only Unix permissions.</summary>
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void CheckPaths(string document)
    {
        foreach (var path in new[] { _root, _leaseDirectory, _directory, Path.Combine(_leaseDirectory, ".annotations.lock"), document })
            if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException("Annotation paths cannot be symbolic links.");
    }

    /// <summary>Rejects corrupt/future states and bounds allocation before deserializing; absence alone means empty.</summary>
    private static async Task<KnowledgeAnnotationState> ReadDocumentAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return new();
        await using var file = File.OpenRead(path);
        if (file.Length > MaximumBytes) throw new InvalidDataException("Annotation document exceeds its byte limit.");
        var state = await JsonSerializer.DeserializeAsync<KnowledgeAnnotationState>(file, cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Null annotation state.");
        Validate(state); return state;
    }

    private static void Validate(KnowledgeAnnotationState state)
    {
        if (state.SchemaVersion != 1 || state.Version < 0 || state.Claims is null || state.ProcedureFacets is null ||
            state.Claims.Count > 1024 || state.ProcedureFacets.Count > 1024 ||
            state.Claims.Values.Any(value => value is null) || state.ProcedureFacets.Values.Any(value => value is null))
            throw new InvalidDataException("Invalid or unbounded annotation state.");
        if (state.TemporaryNotes is { } notes &&
            (state.TemporaryNoteGeneration is null || state.TemporaryNoteGeneration == Guid.Empty ||
             notes.Count > 256 || notes.Values.Any(InvalidTemporaryNote)))
            throw new InvalidDataException("Invalid or unbounded temporary notes.");
    }

    /// <summary>Checks that active notes carry their author and expired tombstones carry no note or recipient data.</summary>
    private static bool InvalidTemporaryNote(TemporaryNote? note)
    {
        if (note?.Deliveries is null || note.Deliveries.Length > 32 ||
            note.Deliveries.Any(delivery => delivery is null || delivery.Recipient is null || delivery.Count < 1)) return true;
        if (note.Content is null)
            return note.Title is not null || note.Author is not null || note.Deliveries.Length != 0;
        return note.Title is null || note.Author is null;
    }

    /// <summary>Reclaims only this profile's abandoned pre-commit files while every cooperating writer is excluded.</summary>
    private void CleanupTemps(string path)
    {
        foreach (var temporary in Directory.EnumerateFiles(_directory, Path.GetFileName(path) + ".*.tmp"))
        {
            CheckPaths(temporary);
            File.Delete(temporary);
        }
    }
}
