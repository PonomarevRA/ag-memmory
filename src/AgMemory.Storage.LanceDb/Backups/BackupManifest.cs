namespace AgMemory.Storage.LanceDb;

/// <summary>Versioned inventory inside each managed ZIP; SHA256 covers every restored data file.</summary>
public sealed record BackupManifest(int FormatVersion, string Product, DateOnly LocalDate,
    DateTimeOffset CreatedAtUtc, IReadOnlyList<BackupFile> Files);
public sealed record BackupFile(string Path, long Length, string Sha256);
public sealed record LocalBackupResult(string ArchivePath, bool Created, int DeletedArchives, int FileCount);
