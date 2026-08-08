namespace AgMemory.Storage.LanceDb;

/// <summary>Configuration for one local LanceDB-backed AgMemory store.</summary>
public sealed record LanceDbMemoryStoreOptions(string StoragePath)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StoragePath, nameof(StoragePath));
    }
}
