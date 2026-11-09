using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

/// <summary>Atomic private evidence sidecars, independent of legacy aggregates. Limits are128 profiles,4MiB and1024 entries per collection.</summary>
public sealed class KnowledgeAnnotationStore : IKnowledgeAnnotationStore
{
    private readonly string _root;
    private readonly string _directory;
    private readonly StorageLeaseGate _gate;
    private const int MaximumBytes = 4 * 1024 * 1024;

    public KnowledgeAnnotationStore(string storagePath)
    {
        _root = Path.GetFullPath(storagePath);
        _directory = Path.Combine(_root, ".agmemory-annotations");
        _gate = new(_directory, ".annotations.lock");
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
            if ((await ReadDocumentAsync(path, token).ConfigureAwait(false)).Version != expectedVersion) return false;
            if (!File.Exists(path) && Directory.EnumerateFiles(_directory, "*.json").Take(128).Count() >= 128)
                throw new InvalidOperationException("knowledge_capacity_exceeded");
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

    /// <summary>Validates authority identifiers and paths before any lease can create or follow a file.</summary>
    private string Prepare(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Length > 1024 || profile.Any(char.IsControl)) throw new ArgumentException("Invalid annotation profile.");
        var path = Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profile))) + ".json");
        CheckPaths(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private void CheckPaths(string document)
    {
        foreach (var path in new[] { _root, _directory, Path.Combine(_directory, ".annotations.lock"), document })
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
