using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed partial class MemoryCommandService
{
    /// <summary>
    /// Authorises, redacts and atomically records or reinforces durable memory.
    /// </summary>
    public async Task<RememberResult> RememberAsync(RememberCommand command, CancellationToken cancellationToken)
    {
        if (command is null) return RememberFailure(MemoryErrorCode.InvalidArgument, nameof(command));
        if (!TryValidateEnvelope(command.Envelope, out var envelopeError)) return RememberFailure(envelopeError!);
        if (!TryValidateRecordInput(command.Record, out var inputError)) return RememberFailure(inputError!);

        var authorization = await AuthorizeAsync(
            command.Envelope.Actor, MemoryOperation.Remember, command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAllowed) return RememberFailure(authorization.Error!);
        var scope = GetRequestedSelector(authorization.AuthorizedScopes!, command.Envelope.RequestedScope);
        if (scope is null) return RememberFailure(new(MemoryErrorCode.Unauthorized, null, authorization.PolicyVersion));

        var redaction = await _redactor.RedactAsync(
            new(command.Record.CanonicalText, command.Record.Reason, command.Record.DecisionDetails), cancellationToken).ConfigureAwait(false);
        if (!redaction.IsAccepted) return RememberFailure(redaction.Error!);

        var redacted = redaction.Content!;
        if (!TryCanonicalize(command.Record with
        {
            CanonicalText = redacted.CanonicalText,
            Reason = redacted.Reason,
            DecisionDetails = redacted.DecisionDetails
        }, out var canonical, out var canonicalError))
            return RememberFailure(canonicalError!);

        EmbeddingReference? embedding = null;
        ReadOnlyMemory<float>? embeddingVector = null;
        if (canonical.EmbeddingMode == EmbeddingMode.Required)
        {
            var policy = await _embeddingPolicy.GetAsync(command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
            if (!policy.IsConfigured || policy.Contract is null)
                return RememberFailure(new(MemoryErrorCode.PolicyNotConfigured, null, policy.PolicyVersion));
            var generated = await _embeddingProvider.CreateAsync(canonical.CanonicalText, policy.Contract, cancellationToken).ConfigureAwait(false);
            if (!policy.Contract.Matches(generated.Reference))
                return RememberFailure(new(MemoryErrorCode.EmbeddingContractMismatch, null, policy.PolicyVersion));
            embedding = generated.Reference;
            embeddingVector = generated.Vector.ToArray();
        }

        var fingerprint = Hash($"{canonical.Type}|{canonical.CanonicalText}|{canonical.Reason}|{canonical.ExpiresAt:O}|{canonical.EmbeddingMode}");
        var deduplicationKey = Hash($"{ScopeKey(scope.Scope)}|{canonical.Type}|{canonical.CanonicalText}");
        var now = UtcNow();

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existingReceipt = await transaction.FindReceiptAsync(
            authorization.AuthorizedScopes!, RememberKind, scope, command.Envelope.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existingReceipt is not null)
        {
            if (!string.Equals(existingReceipt.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                return RememberFailure(new(MemoryErrorCode.IdempotencyKeyConflict, null));
            var replayMemory = existingReceipt.MemoryId is null
                ? null
                : await transaction.FindRecordAsync(authorization.AuthorizedScopes!, new(existingReceipt.MemoryId), cancellationToken).ConfigureAwait(false);
            return new(RememberOutcome.IdempotencyReplay, replayMemory, null, _options.SupportedContractVersion);
        }

        var duplicate = await transaction.FindByDeduplicationKeyAsync(
            authorization.AuthorizedScopes!, scope, deduplicationKey, cancellationToken).ConfigureAwait(false);
        MemoryRecord memory;
        RememberOutcome outcome;
        long expectedVersion;
        if (duplicate is null)
        {
            memory = new(
                _ids.NewMemoryId(), command.Envelope.RequestedScope, canonical.Type, MemoryLifecycleStatus.Active,
                canonical.CanonicalText, canonical.Reason, canonical.Importance, canonical.Confidence,
                EstimateTokenCost(canonical.CanonicalText), now, now, 1,
                NormalizeEntities(canonical.Entities), canonical.Provenance, embedding,
                canonical.ExpiresAt, deduplicationKey, canonical.DecisionDetails, embeddingVector);
            outcome = RememberOutcome.Created;
            expectedVersion = 0;
        }
        else
        {
            var evidence = duplicate.Provenance.Evidence
                .Concat(canonical.Provenance.Evidence)
                .GroupBy(item => item.Identity, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(item => item.Identity, StringComparer.Ordinal)
                .ToArray();
            memory = duplicate with
            {
                Confidence = Math.Max(duplicate.Confidence, canonical.Confidence),
                Importance = Math.Max(duplicate.Importance, canonical.Importance),
                UpdatedAt = now,
                Version = duplicate.Version + 1,
                Embedding = embedding ?? duplicate.Embedding,
                EmbeddingVector = embeddingVector ?? duplicate.EmbeddingVector,
                Provenance = duplicate.Provenance with { Evidence = evidence }
            };
            outcome = RememberOutcome.Reinforced;
            expectedVersion = duplicate.Version;
        }

        var written = await transaction.WriteRecordAsync(
            authorization.AuthorizedScopes!, memory, expectedVersion, cancellationToken).ConfigureAwait(false);
        if (!written.Applied) return RememberFailure(new(MemoryErrorCode.Conflict, null));
        await transaction.SaveReceiptAsync(authorization.AuthorizedScopes!, new(
            RememberKind, scope, command.Envelope.IdempotencyKey, fingerprint, memory.Id.Value,
            outcome.ToString(), _options.SupportedContractVersion), cancellationToken).ConfigureAwait(false);
        await transaction.EnqueueAsync(authorization.AuthorizedScopes!, Outbox(command.Envelope, scope, RememberKind, memory.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(outcome, memory, null, _options.SupportedContractVersion);
    }
}
