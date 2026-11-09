using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed partial class KnowledgeService
{
    /// <summary>Reports actual bounded-state counts without mutating receipts or implying safe automatic compaction.</summary>
    public static KnowledgeCapacity GetCapacity(KnowledgeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new([
            Capacity("requests", state.Requests.Count, MaximumRequests),
            Capacity("metadata", state.Metadata.Count, MaximumEntries),
            Capacity("procedures", state.Procedures.Count, MaximumEntries),
            Capacity("procedureHistory", state.ProcedureHistory.Count, MaximumHistoryEntries),
            Capacity("occurrences", state.Occurrences.Count, MaximumEntries),
            Capacity("applications", state.Applications.Count, MaximumEntries),
            Capacity("presentations", state.Presentations.Count, MaximumHistoryEntries),
            Capacity("feedback", state.Feedback.Count, MaximumHistoryEntries),
            Capacity("warm", state.Warm.Count, MaximumWarmEntries)
        ], "retained_no_automatic_compaction");
    }

    /// <summary>Exposes nonnegative headroom; the warning starts at 80% of a domain bound.</summary>
    private static KnowledgeCollectionCapacity Capacity(string collection, int used, int limit) =>
        new(collection, used, limit, Math.Max(0, limit - used), used >= Math.Ceiling(limit * .8d));
}
