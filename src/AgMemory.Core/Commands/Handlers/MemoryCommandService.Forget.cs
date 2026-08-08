using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed partial class MemoryCommandService
{
    /// <summary>
    /// Removes an authorised memory record when its retention policy and expected version permit it.
    /// </summary>
    public async Task<ForgetResult> ForgetAsync(ForgetMemoryCommand command, CancellationToken cancellationToken)
    {
        if (command is null) return ForgetFailure(MemoryErrorCode.InvalidArgument, nameof(command));
        if (!TryValidateEnvelope(command.Envelope, out var envelopeError) || command.ExpectedVersion <= 0)
            return ForgetFailure(envelopeError ?? new(MemoryErrorCode.InvalidArgument, nameof(command.ExpectedVersion)));
        var authorization = await AuthorizeAsync(
            command.Envelope.Actor, MemoryOperation.Forget, command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAllowed) return ForgetFailure(authorization.Error!);
        var scope = GetRequestedSelector(authorization.AuthorizedScopes!, command.Envelope.RequestedScope);
        if (scope is null) return ForgetFailure(new(MemoryErrorCode.Unauthorized, null, authorization.PolicyVersion));

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var fingerprint = Hash($"{command.MemoryId}|{command.ExpectedVersion}");
        var receipt = await transaction.FindReceiptAsync(
            authorization.AuthorizedScopes!, ForgetKind, scope, command.Envelope.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (!string.Equals(receipt.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                return ForgetFailure(new(MemoryErrorCode.IdempotencyKeyConflict, null));
            return new(ForgetOutcome.Forgotten, null, null, _options.SupportedContractVersion);
        }
        var record = await transaction.FindRecordAsync(authorization.AuthorizedScopes!, command.MemoryId, cancellationToken).ConfigureAwait(false);
        if (record is null) return new(ForgetOutcome.NotFound, null, new(MemoryErrorCode.NotFound, null), _options.SupportedContractVersion);
        if (record.Version != command.ExpectedVersion) return new(ForgetOutcome.StaleVersion, record.Version,
            new(MemoryErrorCode.StaleVersion, null), _options.SupportedContractVersion);
        var policy = await _retentionPolicy.EvaluateForgetAsync(record, command.Envelope.Actor, cancellationToken).ConfigureAwait(false);
        if (!policy.IsConfigured || !policy.AllowsForget)
            return ForgetFailure(new(MemoryErrorCode.PolicyNotConfigured, null, policy.PolicyVersion));
        var deletion = await transaction.DeleteRecordAsync(
            authorization.AuthorizedScopes!, record, record.Version, cancellationToken).ConfigureAwait(false);
        if (!deletion.Applied) return new(ForgetOutcome.StaleVersion, deletion.CurrentVersion,
            new(MemoryErrorCode.StaleVersion, null), _options.SupportedContractVersion);
        await transaction.SaveReceiptAsync(authorization.AuthorizedScopes!, new(ForgetKind, scope, command.Envelope.IdempotencyKey,
            fingerprint, record.Id.Value, ForgetOutcome.Forgotten.ToString(),
            _options.SupportedContractVersion), cancellationToken).ConfigureAwait(false);
        await transaction.EnqueueAsync(authorization.AuthorizedScopes!, Outbox(command.Envelope, scope, ForgetKind, record.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(ForgetOutcome.Forgotten, record.Version, null, _options.SupportedContractVersion);
    }
}
