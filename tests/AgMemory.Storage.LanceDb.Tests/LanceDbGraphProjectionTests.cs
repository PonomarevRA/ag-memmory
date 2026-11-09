using AgMemory.Contracts;
using AgMemory.Storage.LanceDb;
using Apache.Arrow;
using Apache.Arrow.Types;
using lancedb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Proves graph topology reads only its selected source columns rather than the general memory materializer.</summary>
public sealed class LanceDbGraphProjectionTests
{
    /// <summary>Unused malformed payloads and absent document columns cannot affect an initialized graph-only projection.</summary>
    [Fact]
    public async Task GraphProjection_DoesNotRequireDocumentFieldsOrParseUnusedJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agmemory-graph-projection-{Guid.NewGuid():N}");
        var now = new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
        var scope = new MemoryScope(new("projection-tenant"));
        try
        {
            await using var store = new LanceDbMemoryStore(new(path));
            await store.GetSchemaManifestAsync();
            using var connection = new Connection();
            await connection.Connect(path);
            // This deliberately reduced table exists only inside the isolated fixture. Reopening the adapter
            // would still reject it under the unchanged production manifest policy.
            await connection.DropTable("memory_records");
            string[] columns = ["id", "tenant_id", "project_id", "workspace_id", "chat_id", "run_id", "record_type", "status",
                "importance", "confidence", "entities_json", "expires_at_utc", "provenance_json", "embedding_vector_json"];
            var schemaBuilder = new Schema.Builder();
            foreach (var column in columns) schemaBuilder.Field(new Field(column, StringType.Default, true));
            var schema = schemaBuilder.Build();
            using var table = await connection.CreateEmptyTable("memory_records", new CreateTableOptions { Schema = schema });
            var arrays = new List<IArrowArray>();
            foreach (var column in columns)
            {
                var builder = new StringArray.Builder();
                for (var row = 0; row < 3; row++)
                {
                    var value = column switch
                    {
                        "id" => $"projection-{row}",
                        "tenant_id" => row == 1 ? "foreign-tenant" : "projection-tenant",
                        "record_type" => "Outcome",
                        "status" => "Active",
                        "importance" => "0.8",
                        "confidence" => "0.9",
                        "entities_json" => "[\"structural\"]",
                        "expires_at_utc" => row == 2 ? now.ToString("O") : null,
                        "provenance_json" or "embedding_vector_json" => "malformed unused JSON",
                        _ => null
                    };
                    builder.Append(value);
                }
                arrays.Add(builder.Build());
            }
            using var batch = new RecordBatch(schema, arrays, 3);
            await table.Add(batch);
            var result = await ((IMemoryGraphSource)store).ReadAsync(new(
                new(new([new ScopeSelector(scope)]), null, now), MemoryGraphLimits.MaximumSourceRecords), default);
            var record = Assert.Single(result);
            Assert.Equal(new MemoryId("projection-0"), record.MemoryId);
            Assert.Equal(scope, record.Scope);
            Assert.Equal(MemoryRecordType.Outcome, record.Type);
            Assert.Equal(new[] { "structural" }, record.Entities);
            Assert.Equal(.8, record.Importance);
            Assert.Equal(.9, record.Confidence);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
