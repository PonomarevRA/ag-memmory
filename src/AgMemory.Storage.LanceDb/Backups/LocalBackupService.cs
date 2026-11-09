using System.Globalization;
using System.Security.Cryptography;

namespace AgMemory.Storage.LanceDb;

/// <summary>Creates one verified daily ZIP from a coordinated snapshot; compression never holds the live database leases.</summary>
public sealed partial class LocalBackupService
{
    private readonly string _dataDirectory;
    private readonly string _backupDirectory;
    private readonly StorageLeaseGate _operationGate;
    public const string Extension = ".agmemmory.zip";

    /// <summary>Uses separate operator-owned roots and the standard local memory store layout.</summary>
    public LocalBackupService(string dataDirectory, string backupDirectory)
    {
        _dataDirectory = BackupPaths.Validate(dataDirectory);
        _backupDirectory = BackupPaths.Validate(backupDirectory);
        BackupPaths.RequireDisjoint(_dataDirectory, _backupDirectory);
        _operationGate = new(_backupDirectory, ".backup.lock");
    }

    /// <summary>Publishes at most one archive for the local calendar day, then prunes only recognized verified automatic archives.</summary>
    public async Task<LocalBackupResult> CreateAsync(DateTimeOffset now, CancellationToken token = default)
    {
        BackupPaths.CreatePrivateDirectory(_backupDirectory);
        await _operationGate.WaitAsync(token).ConfigureAwait(false);
        string? snapshot = null;
        string? temporary = null;
        try
        {
            var date = DateOnly.FromDateTime(now.DateTime);
            var archive = Path.Combine(_backupDirectory, date.ToString("ddMMyyyy", CultureInfo.InvariantCulture) + Extension);
            if (File.Exists(archive))
            {
                var existing = await VerifyArchiveAsync(archive, null, token).ConfigureAwait(false);
                if (existing.LocalDate != date) throw new InvalidDataException("Archive date mismatch.");
                return new(archive, false, await PruneAsync(date, token).ConfigureAwait(false), existing.Files.Count);
            }
            if (!Directory.Exists(Path.Combine(_dataDirectory, "memory-graph.lancedb")))
                throw new DirectoryNotFoundException("The configured local memory store does not exist.");
            snapshot = Path.Combine(_backupDirectory, ".snapshot-" + Guid.NewGuid().ToString("N"));
            BackupPaths.CreatePrivateDirectory(snapshot);
            await SnapshotAsync(snapshot, token).ConfigureAwait(false);
            var files = new List<BackupFile>();
            foreach (var path in Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                await using var stream = File.OpenRead(path);
                var hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                files.Add(new(Path.GetRelativePath(snapshot, path).Replace('\\', '/'), stream.Length, Convert.ToHexStringLower(hash)));
            }
            var manifest = new BackupManifest(1, "AgMemory", date, now.ToUniversalTime(), files);
            temporary = archive + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await WriteArchiveAsync(temporary, snapshot, manifest, token).ConfigureAwait(false);
            await VerifyArchiveAsync(temporary, null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, archive, overwrite: false);
            return new(archive, true, await PruneAsync(date, token).ConfigureAwait(false), files.Count);
        }
        finally
        {
            try
            {
                if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
                if (snapshot is not null && Directory.Exists(snapshot)) Directory.Delete(snapshot, recursive: true);
            }
            finally { _operationGate.Release(); }
        }
    }

    /// <summary>Locks canonical writes, audit, shared knowledge and task L2 in fixed order before copying a consistent snapshot.</summary>
    private async Task SnapshotAsync(string destination, CancellationToken token)
    {
        var store = Path.Combine(_dataDirectory, "memory-graph.lancedb");
        var gates = new[] { new StorageLeaseGate(store), new StorageLeaseGate(store, ".agmemory-audit.lock"),
            new StorageLeaseGate(Path.Combine(store, ".agmemory-knowledge"), ".knowledge.lock"),
            new StorageLeaseGate(Path.Combine(store, ".agmemory-working"), ".working.lock"),
            new StorageLeaseGate(Path.Combine(store, ".agmemory-annotations"), ".annotations.lock") };
        var acquired = 0;
        try
        {
            foreach (var gate in gates) { await gate.WaitAsync(token).ConfigureAwait(false); acquired++; }
            CopyDirectory(_dataDirectory, destination, token);
        }
        finally { for (var i = acquired - 1; i >= 0; i--) gates[i].Release(); }
    }

    /// <summary>Copies regular files without following links; ephemeral lease handles are recreated by restored clients.</summary>
    private static void CopyDirectory(string source, string destination, CancellationToken token)
    {
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            if (item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked source entries are not supported.");
            var target = Path.Combine(destination, item.Name);
            if (item is DirectoryInfo)
            {
                BackupPaths.CreatePrivateDirectory(target);
                CopyDirectory(item.FullName, target, token);
            }
            else
            {
                if (IsLease(item.Name)) continue;
                File.Copy(item.FullName, target, overwrite: false);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private static bool IsLease(string name) => name is ".agmemory-store.lock" or ".agmemory-audit.lock" or ".knowledge.lock" or ".working.lock" or ".annotations.lock" ||
        name.StartsWith(".agmemory-catalog-", StringComparison.Ordinal) && name.EndsWith(".lock", StringComparison.Ordinal);
}
