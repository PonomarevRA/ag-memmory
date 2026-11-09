namespace AgMemory.Contracts;

/// <summary>Server-only lexical recall projection; excludes scope, embeddings, entities and provenance JSON.</summary>
public sealed record MemoryLexicalRecallItem(
    MemoryId Id,
    MemoryRecordType Type,
    string Content,
    double Importance,
    double Confidence,
    DateTimeOffset UpdatedAt,
    double Score)
{
    /// <summary>Internal provider identity for cross-channel deduplication; adapters omit it from agent wire projections.</summary>
    public string? DeduplicationKey { get; init; }
}

/// <summary>Measured projected scan work (one legacy pass or two BM25 passes); diagnostics stay outside MCP responses.</summary>
public sealed record MemoryLexicalRecallWork(long RowsScanned, int MaximumBatchRows, int PeakCandidates, int QueryExecutions, int SelectedColumnCount)
{
    public string Provider { get; init; } = "streaming-scan";
    public string RowCountMeaning { get; init; } = "native_projection_rows_materialized";
    public bool IndexRebuilt { get; init; }
}

/// <summary>Bounded internal ranked candidates and server-only scan diagnostics; MCP outputs remain limited to ten.</summary>
public sealed record MemoryLexicalRecallPage(IReadOnlyList<MemoryLexicalRecallItem> Items, MemoryLexicalRecallWork Work);
