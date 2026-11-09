using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Exercises a real native store, task L2, annotations and SQLite ledger through a verified archive and same-path restore.</summary>
public sealed class JournalBackupTests
{
    [Fact]
    public async Task RestoredJournalRetainsLateReplayFenceTaskAndClaim()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "agmemory-journal-backup-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var path = Path.Combine(data, "memory-graph.lancedb");
        try
        {
            var config = LocalAgMemoryMcpConfiguration.Create(path, "backup-reader", "backup-tenant");
            string citation;
            await using (var runtime = new LocalAgMemoryMcpRuntime(config))
            {
                await runtime.RememberAsync("Journal backup canonical fixture", "Fact", .5, .8, null, default);
                citation = Assert.Single((await runtime.AgentContextAsync("journal backup canonical", 8192, default)).Data!.Items).CitationRef;
                Assert.Null((await runtime.KnowledgeWorkingAsync("Legacy goal", [], [], [], "Next", 0, "legacy")).Error);
                Assert.Null((await runtime.TaskWorkingAsync("backup-task", "Task backup goal", [], [], [], "Next", 0, "task")).Error);
                Assert.Null((await runtime.KnowledgeClaimAsync(citation, "backup-app", "version", "1.7.0", "ToolObservation", "claim")).Error);
            }
            var profile = KnowledgeProfileIdentity.GetKnowledge(path, config.Actor, config.Scope, null);
            var journal = new KnowledgeJournalStore(path);
            await journal.MigrateAsync(profile);
            await using (var runtime = new LocalAgMemoryMcpRuntime(config with { UseKnowledgeJournal = true }))
                Assert.Null((await runtime.KnowledgeWorkingAsync("Journal goal", [], [], [], "Next", 1, "journal")).Error);
            var legacyBackup = Directory.GetFiles(Path.Combine(path, ".agmemory-knowledge"), "*.v1-backup").Single();
            var beforeBytes = File.ReadAllBytes(legacyBackup);
            var archive = await new LocalBackupService(data, Path.Combine(root, "backups")).CreateAsync(DateTimeOffset.UtcNow);
            Directory.Move(data, Path.Combine(root, "displaced"));
            await LocalBackupService.RestoreAsync(archive.ArchivePath, data);
            // The configured root remains exactly the same; changing it would deliberately select another authority identity.
            await using var restored = new LocalAgMemoryMcpRuntime(config with { UseKnowledgeJournal = true });
            Assert.Null((await restored.AgentResolveAsync(citation, default)).Error);
            Assert.Equal("replayed", (await restored.KnowledgeWorkingAsync("Journal goal", [], [], [], "Next", 1, "journal")).Data!.Outcome);
            Assert.Equal("request_conflict", (await restored.KnowledgeWorkingAsync("Changed", [], [], [], "Next", 1, "journal")).Error!.Code);
            Assert.Equal("Task backup goal", (await restored.TaskContextAsync("backup-task")).Data!.WorkingSet!.Goal);
            Assert.Equal("1.7.0", Assert.Single((await restored.KnowledgeClaimsAsync("backup-app", "version")).Data!.Items).Value);
            Assert.Equal(beforeBytes, File.ReadAllBytes(legacyBackup));
            Assert.Equal(2, await journal.CountRequestsAsync(profile, default));
            await Assert.ThrowsAsync<InvalidDataException>(() => new KnowledgeFileStore(path).ReadAsync(profile, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.RollbackBeforeWritesAsync(profile));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
