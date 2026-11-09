using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Versioned technical defaults. Product policy is provided only by ports.</summary>
public sealed record MemoryCoreOptions(
    ContractVersion SupportedContractVersion,
    ContractVersion RerankerConfigurationVersion,
    int ReciprocalRankConstant = 60)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SupportedContractVersion.Value))
            throw new ArgumentException("A supported contract version is required.", nameof(SupportedContractVersion));
        if (string.IsNullOrWhiteSpace(RerankerConfigurationVersion.Value))
            throw new ArgumentException("A reranker configuration version is required.", nameof(RerankerConfigurationVersion));
        if (ReciprocalRankConstant < 0)
            throw new ArgumentOutOfRangeException(nameof(ReciprocalRankConstant));
    }
}
