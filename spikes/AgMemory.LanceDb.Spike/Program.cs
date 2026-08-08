using System.Collections.Immutable;
using Apache.Arrow;
using Apache.Arrow.Types;
using lancedb;

const int VectorDimension = 3;
const string TableName = "memory_records";

var databaseDirectory = Path.Combine(Path.GetTempPath(), $"agmemory-lancedb-spike-{Guid.NewGuid():N}");
Directory.CreateDirectory(databaseDirectory);

try
{
    await CreateAndWriteAsync(databaseDirectory);
    await ReopenAndVerifyAsync(databaseDirectory);
    Console.WriteLine("PASS: LanceDB local durability spike completed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine("FAIL: LanceDB local durability spike failed.");
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
finally
{
    Directory.Delete(databaseDirectory, recursive: true);
}

static async Task CreateAndWriteAsync(string databaseDirectory)
{
    using var connection = new Connection();
    await connection.Connect(databaseDirectory);
    Require(connection.IsOpen(), "Connection was not open after Connect.");

    var table = await connection.CreateEmptyTable(TableName, new CreateTableOptions { Schema = CreateSchema() });
    try
    {
        var initialRecords = new[]
        {
            new MemoryRow("memory-1", "tenant-a", "workspace-a", "Active", "Fact", "LanceDB persists durable vector records.", "test-embedding-v1", [1f, 0f, 0f]),
            new MemoryRow("memory-2", "tenant-a", "workspace-a", "Active", "Summary", "AGM migration requires isolation.", "test-embedding-v1", [0.9f, 0.1f, 0f]),
            new MemoryRow("memory-3", "tenant-b", "workspace-b", "Active", "Fact", "Foreign tenant record must remain hidden.", "test-embedding-v1", [0f, 1f, 0f]),
            new MemoryRow("memory-4", "tenant-a", "workspace-a", "Superseded", "Decision", "Obsolete decision.", "test-embedding-v1", [0f, 0f, 1f]),
        };

        await table.Add(BuildBatch(initialRecords));
        Require(await table.CountRows() == initialRecords.Length, "Initial Arrow batch was not persisted.");
        Console.WriteLine("PASS: create table and add Arrow-mapped metadata/vector batch.");

        var filteredRecords = await table.Query()
            .Where("tenant_id = 'tenant-a' AND status = 'Active'")
            .ToArrow();
        Require(filteredRecords.Length == 2, "Metadata filter did not exclude the other tenant or inactive record.");
        Require(ReadStrings(filteredRecords, "tenant_id").All(tenant => tenant == "tenant-a"), "Metadata filter leaked another tenant.");
        Console.WriteLine("PASS: metadata filter is applied before result materialisation.");

        var vectorResults = await table.Query()
            .NearestTo([1f, 0f, 0f])
            .DistanceType(DistanceType.Cosine)
            .Where("tenant_id = 'tenant-a' AND status = 'Active'")
            .Limit(2)
            .ToArrow();
        var vectorIds = ReadStrings(vectorResults, "id");
        Require(vectorIds.Count == 2 && vectorIds[0] == "memory-1", "Scoped cosine vector search did not return the expected nearest record.");
        Require(ReadStrings(vectorResults, "tenant_id").All(tenant => tenant == "tenant-a"), "Vector query leaked another tenant.");
        Console.WriteLine("PASS: scoped cosine vector search.");

        var upsertRecords = new[]
        {
            new MemoryRow("memory-2", "tenant-a", "workspace-a", "Active", "Summary", "AGM migration requires explicit package boundaries.", "test-embedding-v1", [0.8f, 0.2f, 0f]),
            new MemoryRow("memory-5", "tenant-a", "workspace-a", "Active", "Outcome", "Spike confirms local table reopen.", "test-embedding-v1", [0.7f, 0.3f, 0f]),
        };

        await table.MergeInsert("id")
            .WhenMatchedUpdateAll()
            .WhenNotMatchedInsertAll()
            .Execute(BuildBatch(upsertRecords));
        Require(await table.CountRows() == 5, "Merge insert did not provide expected upsert semantics.");
        Require(await table.CountRows("id = 'memory-2' AND canonical_text = 'AGM migration requires explicit package boundaries.'") == 1,
            "Merge insert did not update the existing metadata/text row.");
        Console.WriteLine("PASS: batch merge-insert upsert.");

        await table.Delete("id = 'memory-4'");
        Require(await table.CountRows("id = 'memory-4'") == 0, "Delete did not remove the requested record.");
        Console.WriteLine("PASS: metadata predicate delete.");
    }
    finally
    {
        table.Dispose();
    }
}

static async Task ReopenAndVerifyAsync(string databaseDirectory)
{
    using var connection = new Connection();
    await connection.Connect(databaseDirectory);
    var table = await connection.OpenTable(TableName);
    try
    {
        Require(await table.CountRows() == 4, "Reopened table did not retain write/delete state.");

        var persistedRows = await table.Query()
            .Where("tenant_id = 'tenant-a' AND status = 'Active'")
            .ToArrow();
        var persistedIds = ReadStrings(persistedRows, "id");
        Require(persistedIds.Contains("memory-2") && persistedIds.Contains("memory-5"), "Reopened table did not retain upserted rows.");
        Require(persistedIds.All(id => id != "memory-3"), "Reopened scoped filter leaked a foreign tenant.");
        Console.WriteLine("PASS: reopen local table and read durable data.");
    }
    finally
    {
        table.Dispose();
    }
}

static Schema CreateSchema()
{
    var vectorItem = new Field("item", FloatType.Default, nullable: false);
    var vectorType = new FixedSizeListType(vectorItem, VectorDimension);

    return new Schema.Builder()
        .Field(new Field("id", StringType.Default, nullable: false))
        .Field(new Field("tenant_id", StringType.Default, nullable: false))
        .Field(new Field("workspace_id", StringType.Default, nullable: false))
        .Field(new Field("status", StringType.Default, nullable: false))
        .Field(new Field("record_type", StringType.Default, nullable: false))
        .Field(new Field("canonical_text", StringType.Default, nullable: false))
        .Field(new Field("embedding_model", StringType.Default, nullable: false))
        .Field(new Field("vector", vectorType, nullable: false))
        .Build();
}

static RecordBatch BuildBatch(IReadOnlyCollection<MemoryRow> rows)
{
    Require(rows.Count > 0, "A batch must contain at least one row.");
    Require(rows.All(row => row.Vector.Length == VectorDimension), "Every vector must match the table embedding dimension.");

    var vectorItem = new Field("item", FloatType.Default, nullable: false);
    var vectorBuilder = new FixedSizeListArray.Builder(vectorItem, VectorDimension);
    var vectorValues = (FloatArray.Builder)vectorBuilder.ValueBuilder;
    foreach (var row in rows)
    {
        vectorBuilder.Append();
        vectorValues.AppendRange(row.Vector);
    }

    return new RecordBatch(
        CreateSchema(),
        [
            BuildStrings(rows.Select(row => row.Id)),
            BuildStrings(rows.Select(row => row.TenantId)),
            BuildStrings(rows.Select(row => row.WorkspaceId)),
            BuildStrings(rows.Select(row => row.Status)),
            BuildStrings(rows.Select(row => row.RecordType)),
            BuildStrings(rows.Select(row => row.CanonicalText)),
            BuildStrings(rows.Select(row => row.EmbeddingModel)),
            vectorBuilder.Build(),
        ],
        rows.Count);
}

static StringArray BuildStrings(IEnumerable<string> values) => new StringArray.Builder().AppendRange(values).Build();

static IReadOnlyList<string> ReadStrings(RecordBatch batch, string columnName)
{
    var strings = batch.Column(columnName) as StringArray
        ?? throw new InvalidOperationException($"Column '{columnName}' was not a StringArray.");

    return Enumerable.Range(0, batch.Length).Select(index => strings.GetString(index)).ToImmutableArray();
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record MemoryRow(
    string Id,
    string TenantId,
    string WorkspaceId,
    string Status,
    string RecordType,
    string CanonicalText,
    string EmbeddingModel,
    float[] Vector);
