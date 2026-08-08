using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed partial class MemoryCommandService
{
    private async Task<ScopeAuthorizationResult> AuthorizeAsync(
        ActorId actor, MemoryOperation operation, MemoryScope scope, CancellationToken cancellationToken) =>
        await _preflight.AuthorizeAsync(actor, operation, scope, cancellationToken).ConfigureAwait(false);

    private bool TryValidateEnvelope(CommandEnvelope envelope, out MemoryError? error) =>
        _preflight.TryValidateEnvelope(envelope, out error);

    private static bool TryValidateRecordInput(MemoryRecordInput input, out MemoryError? error)
    {
        error = null;
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.CanonicalText, nameof(input.CanonicalText));
            ValidateUnitInterval(input.Importance, nameof(input.Importance));
            ValidateUnitInterval(input.Confidence, nameof(input.Confidence));
            input.Provenance.Validate();
            if (input.Type == MemoryRecordType.Decision)
                DecisionTraceValidator.Validate(input.DecisionDetails ?? throw new ArgumentException());
        }
        catch (ArgumentException)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(input));
        }
        return error is null;
    }

    private static bool TryCanonicalize(MemoryRecordInput input, out MemoryRecordInput canonical, out MemoryError? error)
    {
        error = null;
        canonical = input;
        try
        {
            canonical = input with
            {
                CanonicalText = Canonicalize(input.CanonicalText),
                Reason = input.Reason is null ? null : Canonicalize(input.Reason),
                Entities = NormalizeEntities(input.Entities),
                DecisionDetails = input.DecisionDetails is null ? null : DecisionTraceValidator.Canonicalize(input.DecisionDetails)
            };
            if (string.IsNullOrWhiteSpace(canonical.CanonicalText)) throw new ArgumentException();
            if (canonical.Reason is not null && string.IsNullOrWhiteSpace(canonical.Reason)) throw new ArgumentException();
            if (canonical.Type == MemoryRecordType.Decision) DecisionTraceValidator.Validate(canonical.DecisionDetails!);
        }
        catch (ArgumentException)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(input));
        }
        return error is null;
    }

    private static ScopeSelector? GetRequestedSelector(AuthorizedScopeSet set, MemoryScope requested) =>
        CommandPreflight.ExactRequestedSelector(set, requested);

    private static void ValidateUnitInterval(double value, string parameterName) =>
        CommandValueSupport.ValidateUnitInterval(value, parameterName);

    private static bool TryTransition(
        MemoryLifecycleStatus current, MemoryLifecycleAction action, bool hasRelated, out MemoryLifecycleStatus next)
        => CommandValueSupport.TryTransition(current, action, hasRelated, out next);

    private DateTimeOffset UtcNow()
    {
        var now = _clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private OutboxMessage Outbox(CommandEnvelope envelope, ScopeSelector scope, string kind, MemoryId id) =>
        CommandOutbox.Create(envelope, scope, kind, id, _options.SupportedContractVersion);

    private RememberResult RememberFailure(MemoryErrorCode code, string? field) => RememberFailure(new(code, field));
    private RememberResult RememberFailure(MemoryError error) => new(RememberOutcome.Failed, null, error, _options.SupportedContractVersion);
    private LifecycleResult LifecycleFailure(MemoryErrorCode code, string? field) => LifecycleFailure(new(code, field));
    private LifecycleResult LifecycleFailure(MemoryError error) => new(LifecycleOutcome.Failed, null, null, error, _options.SupportedContractVersion);
    private HotMemoryResult HotFailure(MemoryErrorCode code, string? field) => HotFailure(new(code, field));
    private HotMemoryResult HotFailure(MemoryError error) => new(HotMemoryOutcome.Failed, null, null, error, _options.SupportedContractVersion);
    private ForgetResult ForgetFailure(MemoryErrorCode code, string? field) => ForgetFailure(new(code, field));
    private ForgetResult ForgetFailure(MemoryError error) => new(ForgetOutcome.Failed, null, error, _options.SupportedContractVersion);

    internal static string Canonicalize(string value) => CommandValueSupport.Canonicalize(value);
    internal static IReadOnlyList<string> NormalizeEntities(IEnumerable<string> entities) => CommandValueSupport.NormalizeEntities(entities);
    internal static int EstimateTokenCost(string content) => CommandValueSupport.EstimateTokenCost(content);
    internal static string ScopeKey(MemoryScope scope) => CommandValueSupport.ScopeKey(scope);
    internal static string Hash(string source) => CommandValueSupport.Hash(source);
}
