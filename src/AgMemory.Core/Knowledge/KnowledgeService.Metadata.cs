using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed partial class KnowledgeService
{
    /// <summary>Classifies an externally validated canonical revision. Agent calls cannot approve or revoke foundations.</summary>
    public Task<KnowledgeMutationResult> SetMetadataAsync(KnowledgeMetadata metadata, long expectedMetadataRevision,
        string requestKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Text(metadata.SourceId, 200); Text(metadata.Applicability, 1000);
        if (metadata.SourceRevision <= 0 || !Enum.IsDefined(metadata.Class) || !Enum.IsDefined(metadata.Foundation) ||
            metadata.Foundation is FoundationStatus.Approved or FoundationStatus.Revoked ||
            metadata.ReviewAtUtc is { Offset: var offset } && offset != TimeSpan.Zero) throw new ArgumentException("invalid_argument");
        return MutateAsync("metadata", requestKey, new { metadata, expectedMetadataRevision }, (state, now) =>
        {
            var actual = state.Metadata.TryGetValue(metadata.SourceId, out var old) ? old.MetadataRevision : 0;
            Revision(actual, expectedMetadataRevision);
            if (old?.Foundation is FoundationStatus.Approved or FoundationStatus.Revoked)
                throw new InvalidOperationException("foundation_protected");
            state.Metadata[metadata.SourceId] = metadata with { MetadataRevision = expectedMetadataRevision + 1 };
            state.Warm.Remove(metadata.SourceId);
            return new("updated", metadata.SourceId, expectedMetadataRevision + 1);
        }, cancellationToken);
    }

    /// <summary>Excludes or restores derived warm residency without deleting canonical knowledge or modifying confidence.</summary>
    public Task<KnowledgeMutationResult> SetWarmExcludedAsync(string sourceId, bool excluded, string requestKey,
        CancellationToken cancellationToken = default) => MutateAsync("warm_visibility", requestKey, new { sourceId, excluded }, (state, now) =>
        {
            if (!state.Metadata.TryGetValue(sourceId, out var metadata)) throw new InvalidOperationException("source_unavailable");
            if (excluded && metadata.Foundation == FoundationStatus.Approved) throw new InvalidOperationException("foundation_protected");
            state.Metadata[sourceId] = metadata with { WarmExcluded = excluded, MetadataRevision = metadata.MetadataRevision + 1 };
            if (excluded) state.Warm.Remove(sourceId);
            return new("updated", sourceId, metadata.MetadataRevision + 1);
        }, cancellationToken);

    /// <summary>Publishes one shared-authority L2 working set. It does not confer private participant identity.</summary>
    public Task<KnowledgeMutationResult> SetWorkingSetAsync(KnowledgeWorkingSet working, long expectedRevision,
        string requestKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(working);
        Text(working.Goal, 1000); Text(working.NextAction, 1000);
        Lines(working.Facts); Lines(working.Assumptions); Lines(working.Questions);
        return MutateAsync("working_set", requestKey, new { working.Goal, working.Facts, working.Assumptions, working.Questions, working.NextAction, expectedRevision }, (state, now) =>
        {
            Revision(state.WorkingSet?.Revision ?? 0, expectedRevision);
            if (working.ExpiresAtUtc.Offset != TimeSpan.Zero || working.ExpiresAtUtc <= now || working.ExpiresAtUtc > now.AddHours(24))
                throw new ArgumentException("invalid_argument");
            // The proposed state is a private clone; only a successful aggregate CAS publishes this set.
            state.WorkingSet = working with { Revision = expectedRevision + 1 };
            return new("updated", "working", expectedRevision + 1);
        }, cancellationToken);
    }

    private static void Lines(string[] lines)
    {
        if (lines is null || lines.Length > 24) throw new ArgumentException("invalid_argument");
        foreach (var line in lines) Text(line, 1000);
    }
}
