namespace AgMemory.Contracts;

/// <summary>Compact reusable decision trace; it intentionally has no reasoning, transcript, or log field.</summary>
public sealed record DecisionDetails(
    string Problem,
    string Context,
    IReadOnlyList<string> Options,
    string Decision,
    string Reason,
    IReadOnlyList<string> Consequences,
    string? Outcome)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Problem, nameof(Problem));
        ArgumentException.ThrowIfNullOrWhiteSpace(Context, nameof(Context));
        ArgumentNullException.ThrowIfNull(Options);
        if (Options.Count == 0 || Options.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-blank option is required.", nameof(Options));
        ArgumentException.ThrowIfNullOrWhiteSpace(Decision, nameof(Decision));
        ArgumentException.ThrowIfNullOrWhiteSpace(Reason, nameof(Reason));
        ArgumentNullException.ThrowIfNull(Consequences);
        if (Consequences.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Consequences cannot contain blank values.", nameof(Consequences));
        if (Outcome is not null) ArgumentException.ThrowIfNullOrWhiteSpace(Outcome, nameof(Outcome));
    }
}

public sealed record RecordDecisionCommand(
    CommandEnvelope Envelope,
    DecisionDetails Trace,
    double Importance,
    double Confidence,
    IReadOnlyList<string> Entities,
    MemoryProvenance Provenance,
    DateTimeOffset? ExpiresAt = null,
    EmbeddingMode EmbeddingMode = EmbeddingMode.None);

public interface IDecisionMemoryService
{
    Task<RememberResult> RecordDecisionAsync(RecordDecisionCommand command, CancellationToken cancellationToken);
}
