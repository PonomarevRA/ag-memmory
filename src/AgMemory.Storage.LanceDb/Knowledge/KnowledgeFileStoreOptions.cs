namespace AgMemory.Storage.LanceDb;

/// <summary>Hard resource bounds for one server profile's knowledge aggregate.</summary>
public sealed record KnowledgeFileStoreOptions
{
    public int MaximumDocumentBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumProfiles { get; init; } = 128;
    public int MaximumEntries { get; init; } = 20_000;

    /// <summary>Rejects unsafe configuration before any file is created.</summary>
    internal void Validate()
    {
        if (MaximumDocumentBytes is < 1024 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumDocumentBytes));
        if (MaximumProfiles is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(MaximumProfiles));
        if (MaximumEntries is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(MaximumEntries));
    }
}
