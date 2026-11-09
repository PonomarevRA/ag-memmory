using Apache.Arrow;
using Apache.Arrow.Types;

namespace AgMemory.Storage.LanceDb;

/// <summary>
/// Version information for the physical tables owned by <see cref="LanceDbMemoryStore"/>.
/// The model deliberately contains no LanceDB or Arrow types so it can be included in a
/// migration manifest or benchmark result without taking a storage-provider dependency.
/// </summary>
public sealed record LanceDbSchemaManifest(
    string StorageSchemaVersion,
    IReadOnlyList<LanceDbTableSchemaMetadata> Tables);

/// <summary>One versioned physical table in a <see cref="LanceDbSchemaManifest"/>.</summary>
public sealed record LanceDbTableSchemaMetadata(
    string TableName,
    string SchemaVersion,
    string SchemaFingerprint,
    IReadOnlyList<LanceDbSchemaFieldMetadata> Fields,
    LanceDbEmbeddingSchemaMetadata? Embedding = null);

/// <summary>A provider-neutral description of one physical column.</summary>
public sealed record LanceDbSchemaFieldMetadata(string Name, string LogicalType, bool IsNullable);

/// <summary>
/// The embedding identity used by a versioned vector table. ModelVersion is intentionally
/// explicit so benchmark and migration manifests do not have to infer it from a table name.
/// </summary>
public sealed record LanceDbEmbeddingSchemaMetadata(
    string Provider,
    string Model,
    string ModelVersion,
    int Dimension,
    string Normalization);

/// <summary>
/// Raised before reads or writes when a table owned by this adapter does not match the
/// registered versioned schema. The adapter never mutates a mismatched table automatically.
/// </summary>
public sealed class LanceDbSchemaMismatchException : IOException
{
    public LanceDbSchemaMismatchException(string tableName, string expected, string actual, string detail)
        : base($"LanceDB schema mismatch for table '{tableName}': {detail}. Expected '{expected}', actual '{actual}'.")
    {
        TableName = tableName;
        Expected = expected;
        Actual = actual;
    }

    public string TableName { get; }
    public string Expected { get; }
    public string Actual { get; }
}

internal sealed record LanceDbTableSchemaDefinition(
    string TableName,
    string Version,
    Schema Schema,
    LanceDbEmbeddingSchemaMetadata? Embedding = null)
{
    public string Fingerprint => LanceDbSchemaFingerprint.Create(Schema);

    public LanceDbTableSchemaMetadata ToMetadata() => new(
        TableName,
        Version,
        Fingerprint,
        Schema.FieldsList.Select(LanceDbSchemaFingerprint.ToMetadata).ToArray(),
        Embedding);
}

internal static class LanceDbSchemaFingerprint
{
    public static string Create(Schema schema) => string.Join(
        "|",
        schema.FieldsList.Select(field => $"{field.Name}:{LogicalType(field.DataType)}:{(field.IsNullable ? "nullable" : "required")}"));

    public static LanceDbSchemaFieldMetadata ToMetadata(Field field) => new(field.Name, LogicalType(field.DataType), field.IsNullable);

    public static void RequireMatch(LanceDbTableSchemaDefinition expected, Schema actual)
    {
        var expectedFields = expected.Schema.FieldsList;
        var actualFields = actual.FieldsList;
        if (actualFields.Count != expectedFields.Count)
            throw new LanceDbSchemaMismatchException(
                expected.TableName,
                expected.Fingerprint,
                Create(actual),
                $"field count is {actualFields.Count}, not {expectedFields.Count}");

        for (var index = 0; index < expectedFields.Count; index++)
        {
            var expectedField = expectedFields[index];
            var actualField = actualFields[index];
            if (!string.Equals(expectedField.Name, actualField.Name, StringComparison.Ordinal) ||
                expectedField.IsNullable != actualField.IsNullable ||
                !string.Equals(LogicalType(expectedField.DataType), LogicalType(actualField.DataType), StringComparison.Ordinal))
            {
                throw new LanceDbSchemaMismatchException(
                    expected.TableName,
                    expected.Fingerprint,
                    Create(actual),
                    $"field {index} must be '{FieldDescription(expectedField)}' but is '{FieldDescription(actualField)}'");
            }
        }
    }

    private static string FieldDescription(Field field) =>
        $"{field.Name}:{LogicalType(field.DataType)}:{(field.IsNullable ? "nullable" : "required")}";

    private static string LogicalType(IArrowType dataType) => dataType switch
    {
        StringType => "utf8",
        FloatType => "float32",
        FixedSizeListType vector => $"fixed_size_list<{LogicalType(vector.ValueField.DataType)},{vector.ListSize}>",
        _ => dataType.ToString() ?? dataType.GetType().Name
    };
}
