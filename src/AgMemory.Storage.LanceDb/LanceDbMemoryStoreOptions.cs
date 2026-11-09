namespace AgMemory.Storage.LanceDb;

/// <summary>Configuration for one local LanceDB-backed AgMemory store.</summary>
public sealed record LanceDbMemoryStoreOptions(string StoragePath)
{
    /// <summary>Minimum retirement age before derived generations may be removed. Durable records and metadata are never collected.</summary>
    public TimeSpan ReaderCatalogRetirementGrace { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Minimum interval between automatic derived-generation collection attempts during catalog rebuilds.</summary>
    public TimeSpan ReaderCatalogCollectionInterval { get; init; } = TimeSpan.FromHours(1);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StoragePath, nameof(StoragePath));
        if (ReaderCatalogRetirementGrace < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ReaderCatalogRetirementGrace));
        if (ReaderCatalogCollectionInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ReaderCatalogCollectionInterval));
    }
}
