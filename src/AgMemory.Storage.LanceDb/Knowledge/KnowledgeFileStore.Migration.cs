using AgMemory.Contracts;

namespace AgMemory.Storage.LanceDb;

public sealed partial class KnowledgeFileStore
{
    /// <summary>Reuses the legacy structural/aggregate validator for journal snapshots; the journal owns receipt-table bounds separately.</summary>
    internal void ValidateJournalAggregate(KnowledgeState state) => ValidateState(state);

    /// <summary>Validates the profile and private paths before the migration caller opens the shared lease.</summary>
    internal void PrepareMigrationLease(string profile)
    {
        _ = DocumentPath(profile);
        PrepareDirectory();
    }

    /// <summary>Internal migration access only; caller must hold the same namespace lease before reading or fencing.</summary>
    internal Task<KnowledgeState> ReadForMigrationAsync(string profile, CancellationToken token)
    {
        PrepareDirectory();
        var path = DocumentPath(profile);
        ValidatePaths(path);
        return ReadDocumentAsync(path, token);
    }

    internal string MigrationPath(string profile) => DocumentPath(profile);

    /// <summary>Preserves the exact legacy bytes before atomically publishing a schema fence that old writers reject.</summary>
    internal void FenceForJournal(string profile, long version, CancellationToken token)
    {
        var path = DocumentPath(profile);
        ValidatePaths(path);
        EnsureProfileCapacity(path);
        var backup = path + ".v1-backup";
        RejectLink(backup);
        var temporary = backup + ".tmp";
        RejectLink(temporary);
        try
        {
            var legacy = File.Exists(path) ? File.ReadAllBytes(path) : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new KnowledgeState());
            if (File.Exists(temporary)) File.Delete(temporary);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options)) { file.Write(legacy); file.Flush(true); }
            File.Move(temporary, backup, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Publish(path, new() { SchemaVersion = 2, Version = version }, token);
    }

    /// <summary>Restores the preserved generation only when the journal caller has proved that no post-migration write exists.</summary>
    internal void RestoreBeforeJournalWrites(string profile, CancellationToken token)
    {
        var path = DocumentPath(profile);
        ValidatePaths(path);
        var backup = path + ".v1-backup";
        RejectLink(backup);
        RejectLink(path + ".rollback.tmp");
        token.ThrowIfCancellationRequested();
        File.Copy(backup, path + ".rollback.tmp", true);
        try { File.Move(path + ".rollback.tmp", path, true); }
        finally { if (File.Exists(path + ".rollback.tmp")) File.Delete(path + ".rollback.tmp"); }
    }
}
