using Apache.Arrow;
using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using lancedb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

public sealed class UsageAuditStoreTests
{
    [Fact]
    public async Task Append_IsAppendOnlyIdempotentAndSchemaContainsOnlyApprovedFields()
    {
        var path = TemporaryPath();
        try
        {
            var usageEvent = Event("query text must never persist", DateTimeOffset.UnixEpoch);
            await using (var store = new LanceDbMemoryStore(new(path)))
            {
                Assert.Equal(UsageAuditAppendResult.Appended, await store.AppendAsync(usageEvent, default));
                Assert.Equal(UsageAuditAppendResult.IdempotencyReplay, await store.AppendAsync(usageEvent, default));
                var manifest = await store.GetSchemaManifestAsync();
                var manifestTable = Assert.Single(manifest.Tables, item => item.TableName == "usage_audit_events");
                Assert.Equal(
                [
                    "event_id", "occurred_at_utc", "schema_version", "source", "operation", "client_label", "area_id", "query_hash",
                    "query_length_chars", "result_count", "selected_context_chars", "delivered_tokens_estimate", "estimation_method_version", "outcome", "failure_class"
                ], manifestTable.Fields.Select(field => field.Name).ToArray());
                Assert.DoesNotContain(manifestTable.Fields, field => field.Name.Contains("scope", StringComparison.OrdinalIgnoreCase) ||
                    field.Name.Contains("actor", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(field.Name, "query_text", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("exception", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    field.Name.Contains("storage", StringComparison.OrdinalIgnoreCase));
            }

            using var connection = new Connection();
            await connection.Connect(path);
            using var table = await connection.OpenTable("usage_audit_events");
            var rows = await table.Query().ToArrow();
            Assert.Equal(1, rows.Length);
            Assert.DoesNotContain("query text must never persist", ((StringArray)rows.Column("query_hash")).GetString(0), StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    [Fact]
    public async Task Append_RejectsDivergentDuplicateAndCleanupDeletesStrictlyOlderEvents()
    {
        var path = TemporaryPath();
        try
        {
            var cutoff = DateTimeOffset.UnixEpoch.AddDays(UsageAuditPolicy.RetentionDays);
            var older = Event("older", cutoff.AddTicks(-1));
            var boundary = Event("boundary", cutoff);
            await using var store = new LanceDbMemoryStore(new(path));
            await store.AppendAsync(older, default);
            await store.AppendAsync(boundary, default);
            var divergent = older with { ResultCount = 3 };

            await Assert.ThrowsAsync<UsageAuditEventConflictException>(() => store.AppendAsync(divergent, default));
            Assert.Equal(1, await store.DeleteExpiredAsync(cutoff, default));

            using var connection = new Connection();
            await connection.Connect(path);
            using var table = await connection.OpenTable("usage_audit_events");
            var rows = await table.Query().ToArrow();
            Assert.Equal(1, rows.Length);
            Assert.Equal(boundary.EventId.ToString("D"), ((StringArray)rows.Column("event_id")).GetString(0));
        }
        finally
        {
            DeleteTemporaryPath(path);
        }
    }

    private static UsageAuditEvent Event(string query, DateTimeOffset occurredAt) => new(Guid.NewGuid(), occurredAt, UsageAuditSource.Mcp,
        UsageAuditOperation.ContextBuild, UsageAuditClientLabel.Codex, "default",
        new(string.Concat(Enumerable.Repeat("ab", 32)), query.Length), 2, 16, 4,
        UsageAuditPolicy.EstimationMethodVersion, UsageAuditOutcome.Succeeded, null);

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"agmemory-usage-audit-tests-{Guid.NewGuid():N}");
    private static void DeleteTemporaryPath(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
