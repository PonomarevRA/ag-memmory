using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Decision ingress reuses the command boundary so no durable write bypasses redaction or idempotency.</summary>
public sealed class DecisionMemoryService(
    IMemoryCommandService commands,
    MemoryCoreOptions options) : IDecisionMemoryService
{
    public Task<RememberResult> RecordDecisionAsync(
        RecordDecisionCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null) return Task.FromResult(Failure(MemoryErrorCode.InvalidArgument, nameof(command)));
        DecisionDetails trace;
        try
        {
            DecisionTraceValidator.Validate(command.Trace);
            trace = DecisionTraceValidator.Canonicalize(command.Trace);
            DecisionTraceValidator.Validate(trace);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(Failure(MemoryErrorCode.InvalidArgument, nameof(command.Trace)));
        }

        return commands.RememberAsync(new(command.Envelope, new(
            MemoryRecordType.Decision,
            $"Decision: {trace.Decision}. Problem: {trace.Problem}",
            trace.Reason,
            command.Importance,
            command.Confidence,
            command.Entities,
            command.Provenance,
            command.ExpiresAt,
            trace,
            command.EmbeddingMode)), cancellationToken);
    }

    private RememberResult Failure(MemoryErrorCode code, string? field) =>
        new(RememberOutcome.Failed, null, new(code, field), options.SupportedContractVersion);
}
