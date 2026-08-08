using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed partial class MemoryCommandService
{
    /// <summary>
    /// Applies a version-checked lifecycle transition after authorisation and ingress redaction.
    /// </summary>
    public async Task<LifecycleResult> ApplyLifecycleAsync(LifecycleCommand command, CancellationToken cancellationToken)
    {
        if (command is null) return LifecycleFailure(MemoryErrorCode.InvalidArgument, nameof(command));
        if (!TryValidateEnvelope(command.Envelope, out var envelopeError) || command.ExpectedVersion <= 0)
            return LifecycleFailure(envelopeError ?? new(MemoryErrorCode.InvalidArgument, nameof(command.ExpectedVersion)));

        var authorization = await AuthorizeAsync(
            command.Envelope.Actor, MemoryOperation.Lifecycle, command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAllowed) return LifecycleFailure(authorization.Error!);
        var scope = GetRequestedSelector(authorization.AuthorizedScopes!, command.Envelope.RequestedScope);
        if (scope is null) return LifecycleFailure(new(MemoryErrorCode.Unauthorized, null, authorization.PolicyVersion));

        // Lifecycle reasons are ingress content too; Core never puts their raw value into an outbox receipt.
        var redaction = await _redactor.RedactAsync(new("lifecycle", command.Reason, null), cancellationToken).ConfigureAwait(false);
        if (!redaction.IsAccepted) return LifecycleFailure(redaction.Error!);

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = Hash($"{command.MemoryId}|{command.Action}|{command.ExpectedVersion}|{command.RelatedMemoryId}");
        var receipt = await transaction.FindReceiptAsync(
            authorization.AuthorizedScopes!, LifecycleKind, scope, command.Envelope.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (!string.Equals(receipt.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                return LifecycleFailure(new(MemoryErrorCode.IdempotencyKeyConflict, null));
            var replay = await transaction.FindRecordAsync(authorization.AuthorizedScopes!, command.MemoryId, cancellationToken).ConfigureAwait(false);
            return new(LifecycleOutcome.Applied, replay, replay?.Version, null, _options.SupportedContractVersion);
        }
        var current = await transaction.FindRecordAsync(authorization.AuthorizedScopes!, command.MemoryId, cancellationToken).ConfigureAwait(false);
        if (current is null) return new(LifecycleOutcome.NotFound, null, null,
            new(MemoryErrorCode.NotFound, null), _options.SupportedContractVersion);
        if (current.Version != command.ExpectedVersion) return new(LifecycleOutcome.StaleVersion, current, current.Version,
            new(MemoryErrorCode.StaleVersion, null), _options.SupportedContractVersion);
        if (command.RelatedMemoryId is { } relatedId &&
            await transaction.FindRecordAsync(authorization.AuthorizedScopes!, relatedId, cancellationToken).ConfigureAwait(false) is null)
            return LifecycleFailure(new(MemoryErrorCode.Unauthorized, nameof(command.RelatedMemoryId), authorization.PolicyVersion));
        if (!TryTransition(current.Status, command.Action, command.RelatedMemoryId is not null, out var nextStatus))
            return new(LifecycleOutcome.InvalidTransition, current, current.Version,
                new(MemoryErrorCode.InvalidTransition, null), _options.SupportedContractVersion);

        var updated = current with { Status = nextStatus, Version = current.Version + 1, UpdatedAt = UtcNow() };
        var write = await transaction.WriteRecordAsync(
            authorization.AuthorizedScopes!, updated, current.Version, cancellationToken).ConfigureAwait(false);
        if (!write.Applied) return new(LifecycleOutcome.StaleVersion, current, write.CurrentVersion,
            new(MemoryErrorCode.StaleVersion, null), _options.SupportedContractVersion);
        await transaction.SaveReceiptAsync(authorization.AuthorizedScopes!, new(LifecycleKind, scope, command.Envelope.IdempotencyKey, fingerprint,
            updated.Id.Value, LifecycleOutcome.Applied.ToString(), _options.SupportedContractVersion), cancellationToken).ConfigureAwait(false);
        await transaction.EnqueueAsync(authorization.AuthorizedScopes!, Outbox(command.Envelope, scope, LifecycleKind, updated.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(LifecycleOutcome.Applied, updated, updated.Version, null, _options.SupportedContractVersion);
    }
}
