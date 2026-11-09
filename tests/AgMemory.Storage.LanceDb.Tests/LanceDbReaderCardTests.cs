using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Apache.Arrow;
using Apache.Arrow.Types;
using lancedb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Current card delivery must not depend on cached catalog contents or broad record materialization.</summary>
public sealed class LanceDbReaderCardTests
{
    [Fact]
    public async Task ReaderCards_FilterCurrentExactScopeAndReadOnlyRequiredColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-card-projection-{Guid.NewGuid():N}");
        var now = DateTimeOffset.UtcNow;
        var scope = new MemoryScope(new("card-tenant"));
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            var routes = new List<string>();
            for (var index = 0; index < 25; index++)
                routes.Add((await store.GetOrCreateRouteAsync(new($"card-{index}"), default)).RouteKey);
            using var connection = new Connection();
            await connection.Connect(path);
            await connection.DropTable("memory_records");
            string[] columns = ["id", "tenant_id", "project_id", "workspace_id", "chat_id", "run_id", "record_type", "status",
                "expires_at_utc", "canonical_text", "version", "entities_json", "provenance_json", "embedding_vector_json"];
            var builder = new Schema.Builder();
            foreach (var column in columns) builder.Field(new Field(column, StringType.Default, true));
            var schema = builder.Build();
            using var table = await connection.CreateEmptyTable("memory_records", new CreateTableOptions { Schema = schema });
            var arrays = new List<IArrowArray>();
            foreach (var column in columns)
            {
                var values = new StringArray.Builder();
                for (var index = 0; index < 24; index++)
                    values.Append(column switch
                    {
                        "id" => $"card-{index}", "tenant_id" => index == 1 ? "foreign" : "card-tenant",
                        "record_type" => "Outcome", "status" => index == 3 ? "Superseded" : "Active",
                        "expires_at_utc" => index == 2 ? now.ToString("O") : null,
                        "canonical_text" => $"Current source {index}", "version" => "7",
                        "entities_json" or "provenance_json" or "embedding_vector_json" => "malformed unused JSON",
                        _ => null
                    });
                arrays.Add(values.Build());
            }
            using var batch = new RecordBatch(schema, arrays, 24);
            await table.Add(batch);
            var eligibility = new MemorySearchEligibility(new([new ScopeSelector(scope)]), null, now);
            var result = await store.ReadCardsAsync(eligibility, routes, default);
            Assert.Equal(21, result.Count);
            Assert.DoesNotContain(result, card => routes.Skip(1).Take(3).Contains(card.RouteKey) || card.RouteKey == routes[24]);
            Assert.All(result, card => { Assert.Equal(7, card.Version); Assert.StartsWith("Current source ", card.CanonicalText); });
            Assert.Single(await store.ReadCardsAsync(eligibility, [routes[0], routes[0]], default));
            await table.Delete("id = 'card-0'");
            Assert.Empty(await store.ReadCardsAsync(eligibility, [routes[0]], default));
            var changedColumns = new List<IArrowArray>();
            foreach (var column in columns)
            {
                var value = column switch { "id" => "card-0", "tenant_id" => "card-tenant", "record_type" => "Outcome",
                    "status" => "Active", "canonical_text" => "Changed source", "version" => "8", _ => null };
                changedColumns.Add(new StringArray.Builder().Append(value).Build());
            }
            using var replacement = new RecordBatch(schema, changedColumns, 1);
            await table.Add(replacement);
            var changed = Assert.Single(await store.ReadCardsAsync(eligibility, [routes[0]], default));
            Assert.Equal("Changed source", changed.CanonicalText); Assert.Equal(8, changed.Version);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task InvalidOrOversizedRouteBatchesDoNotInitializeStorage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-card-reject-{Guid.NewGuid():N}");
        await using var store = new LanceDbMemoryStore(new(path));
        var eligibility = new MemorySearchEligibility(new([new ScopeSelector(new(new("card-tenant")))]), null, DateTimeOffset.UtcNow);
        Assert.Empty(await store.ReadCardsAsync(eligibility, Enumerable.Repeat("valid", 26).ToArray(), default));
        Assert.Empty(await store.ReadCardsAsync(eligibility, ["bad'route"], default));
        Assert.False(Directory.Exists(path));
    }
}
