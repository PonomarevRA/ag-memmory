using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LocalBackupService
{
    private const int MaximumFiles = 200_000;
    private const long MaximumExpandedBytes = 64L * 1024 * 1024 * 1024;
    private const string ManifestEntry = "manifest.json";

    /// <summary>Writes a private ZIP and flushes it before publication; a failed write cannot replace a previous daily archive.</summary>
    private static async Task WriteArchiveAsync(string path, string snapshot, BackupManifest manifest, CancellationToken token)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, options);
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            var metadata = zip.CreateEntry(ManifestEntry, CompressionLevel.Fastest);
            await using (var target = metadata.Open())
                await JsonSerializer.SerializeAsync(target, manifest, cancellationToken: token).ConfigureAwait(false);
            foreach (var item in manifest.Files)
            {
                BackupPaths.ValidateEntry(item.Path);
                var entry = zip.CreateEntry("data/" + item.Path, CompressionLevel.Fastest);
                await using var input = File.OpenRead(Path.Combine(snapshot, item.Path));
                await using var output = entry.Open();
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
        }
        file.Flush(flushToDisk: true);
    }

    /// <summary>Verifies the full inventory and SHA256; optional extraction is bounded and never trusts entry-selected filesystem paths.</summary>
    private static async Task<BackupManifest> VerifyArchiveAsync(string path, string? destination, CancellationToken token)
    {
        BackupPaths.Validate(path);
        await using var file = File.OpenRead(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 2 or > MaximumFiles + 1) throw new InvalidDataException("Unsupported ZIP size.");
        var metadata = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("Missing AgMemory manifest.");
        if (metadata.Length > 32 * 1024 * 1024) throw new InvalidDataException("Manifest is too large.");
        BackupManifest manifest;
        await using (var input = metadata.Open())
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(input, cancellationToken: token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Invalid manifest.");
        if (manifest.FormatVersion != 1 || manifest.Product != "AgMemory" || manifest.Files is null ||
            manifest.Files.Count is < 1 or > MaximumFiles || zip.Entries.Count != manifest.Files.Count + 1)
            throw new InvalidDataException("Unsupported AgMemory manifest.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            BackupPaths.ValidateEntry(entry.FullName);
            if (!names.Add(entry.FullName) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Duplicate or linked ZIP entry.");
        }
        var totalBytes = 0L;
        var inventory = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            if (item is null) throw new InvalidDataException("Invalid inventory.");
            BackupPaths.ValidateEntry(item.Path);
            if (!inventory.Add(item.Path) || item.Length < 0 || item.Length > MaximumExpandedBytes - totalBytes ||
                item.Sha256 is null || item.Sha256.Length != 64) throw new InvalidDataException("Invalid file inventory.");
            totalBytes += item.Length;
            var entry = zip.GetEntry("data/" + item.Path) ?? throw new InvalidDataException("Missing data entry.");
            if (entry.Length != item.Length) throw new InvalidDataException("Data length mismatch.");
            var target = destination is null ? null : Path.Combine(destination, item.Path);
            await VerifyEntryAsync(entry, item, target, token).ConfigureAwait(false);
        }
        if (!inventory.Any(name => name.StartsWith("memory-graph.lancedb/", StringComparison.Ordinal)))
            throw new InvalidDataException("Archive contains no local memory store.");
        return manifest;
    }

    /// <summary>Reads at most the declared file length and hashes actual bytes, rejecting damaged or inflated streams before publication.</summary>
    private static async Task VerifyEntryAsync(ZipArchiveEntry entry, BackupFile expected, string? target, CancellationToken token)
    {
        if (target is not null) BackupPaths.CreatePrivateDirectory(Path.GetDirectoryName(target)!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var output = target is null ? null : new FileStream(target, options);
        await using var input = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var length = 0L;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (read > expected.Length - length) throw new InvalidDataException("Expanded ZIP entry is too large.");
            length += read;
            hash.AppendData(buffer, 0, read);
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (length != expected.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != expected.Sha256)
            throw new InvalidDataException("Backup checksum mismatch.");
        output?.Flush(flushToDisk: true);
    }

    /// <summary>Restores to a new directory only; validation failure removes staging and never overwrites existing or running data.</summary>
    public static async Task<BackupManifest> RestoreAsync(string archive, string destination, CancellationToken token = default)
    {
        var target = BackupPaths.Validate(destination);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Restore destination must not exist.");
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("Invalid restore destination.");
        BackupPaths.CreatePrivateDirectory(parent);
        var staging = Path.Combine(parent, ".agmemory-restore-" + Guid.NewGuid().ToString("N"));
        BackupPaths.CreatePrivateDirectory(staging);
        try
        {
            var manifest = await VerifyArchiveAsync(archive, staging, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return manifest;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }
}
