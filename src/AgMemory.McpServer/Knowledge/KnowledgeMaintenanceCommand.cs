using System.Text.Json;
using AgMemory.Storage.LanceDb;

namespace AgMemory.McpServer;

/// <summary>Operator-only commands shipped with the standalone MCP binary. No migration operation is exposed as an agent tool.</summary>
public static class KnowledgeMaintenanceCommand
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0].StartsWith("--knowledge-", StringComparison.Ordinal);

    /// <summary>Uses exact configured authority; rollback is rejected after journal mutations and errors disclose no private paths.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("--knowledge-migrate" or "--knowledge-rollback-before-writes" or "--knowledge-status"))
        {
            Console.Error.WriteLine("Use --knowledge-migrate, --knowledge-rollback-before-writes or --knowledge-status with the configured MCP environment.");
            return 2;
        }
        try
        {
            var configuration = LocalAgMemoryMcpConfiguration.FromEnvironment();
            var profile = KnowledgeProfileIdentity.GetKnowledge(configuration.StoragePath, configuration.Actor,
                configuration.Scope, configuration.KnowledgeTeamId);
            var journal = new KnowledgeJournalStore(configuration.StoragePath);
            if (args[0] == "--knowledge-migrate") await journal.MigrateAsync(profile);
            else if (args[0] == "--knowledge-rollback-before-writes")
            {
                await journal.RollbackBeforeWritesAsync(profile);
                Console.WriteLine("Legacy generation restored; disable journal mode before reconnecting.");
                return 0;
            }
            var state = await journal.ReadAsync(profile, default);
            var receipts = await journal.CountRequestsAsync(profile, default);
            var total = await journal.CountNamespaceRequestsAsync(profile, default);
            Console.WriteLine(JsonSerializer.Serialize(new { Schema = "knowledge-journal/v2", state.Version, ProfileReceipts = receipts,
                NamespaceReceipts = total, MaximumNamespaceReceipts = journal.MaximumRequests,
                MaximumDatabaseBytes = KnowledgeJournalStore.MaximumDatabaseBytes,
                ByteLimitScope = "SQLite database only; rollback journal and preserved v1 files are separate",
                Activation = "Set AGMEMORY_KNOWLEDGE_STORAGE=journal and reconnect all knowledge writers. Canonical memory tools remain available." }));
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine("knowledge_maintenance_failed: check configured authority, migration/fence state, capacity and rollback-before-writes precondition.");
            return 1;
        }
    }
}
