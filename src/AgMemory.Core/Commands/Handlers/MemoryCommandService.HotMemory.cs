using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed partial class MemoryCommandService
{
    /// <summary>
    /// Appends bounded, redacted session memory while preserving idempotency and optimistic concurrency.
    /// </summary>
    public async Task<HotMemoryResult> AppendHotMemoryAsync(AppendHotMemoryCommand command, CancellationToken cancellationToken)
    {
        if (command is null) return HotFailure(MemoryErrorCode.InvalidArgument, nameof(command));
        if (!TryValidateEnvelope(command.Envelope, out var envelopeError) ||
            string.IsNullOrWhiteSpace(command.CaptureKey) || command.ExpectedVersion < 0 || command.ExpiresAt.Offset != TimeSpan.Zero)
            return HotFailure(envelopeError ?? new(MemoryErrorCode.InvalidArgument, nameof(command)));

        var authorization = await AuthorizeAsync(
            command.Envelope.Actor, MemoryOperation.AppendHotMemory, command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAllowed) return HotFailure(authorization.Error!);
        var scope = GetRequestedSelector(authorization.AuthorizedScopes!, command.Envelope.RequestedScope);
        if (scope is null) return HotFailure(new(MemoryErrorCode.Unauthorized, null, authorization.PolicyVersion));
        if (command.ExpiresAt <= UtcNow()) return HotFailure(new(MemoryErrorCode.InvalidArgument, nameof(command.ExpiresAt)));

        var redaction = await _redactor.RedactAsync(new(command.Content, null, null), cancellationToken).ConfigureAwait(false);
        if (!redaction.IsAccepted) return HotFailure(redaction.Error!);
        var content = Canonicalize(redaction.Content!.CanonicalText);
        if (string.IsNullOrWhiteSpace(content)) return HotFailure(new(MemoryErrorCode.InvalidArgument, nameof(command.Content)));

        var fingerprint = Hash($"{command.CaptureKey}|{content}|{command.ExpiresAt:O}|{command.ExpectedVersion}");
        await using var transaction = await _store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await transaction.FindReceiptAsync(
            authorization.AuthorizedScopes!, HotMemoryKind, scope, command.Envelope.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (!string.Equals(receipt.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                return HotFailure(new(MemoryErrorCode.IdempotencyKeyConflict, null));
            var replay = await transaction.FindHotMemoryAsync(authorization.AuthorizedScopes!, command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
            return new(HotMemoryOutcome.IdempotencyReplay, replay, replay?.Version, null, _options.SupportedContractVersion);
        }

        var existing = await transaction.FindHotMemoryAsync(authorization.AuthorizedScopes!, command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (existing is not null && command.ExpectedVersion == 0)
            return new(HotMemoryOutcome.Conflict, existing, existing.Version, new(MemoryErrorCode.Conflict, null), _options.SupportedContractVersion);
        if (existing is not null && command.ExpectedVersion != existing.Version)
            return new(HotMemoryOutcome.StaleVersion, existing, existing.Version, new(MemoryErrorCode.StaleVersion, null), _options.SupportedContractVersion);

        var now = UtcNow();
        var memory = existing is null
            ? new SessionHotMemory(_ids.NewMemoryId(), command.Envelope.RequestedScope, content, command.Provenance, 1, now, now, command.ExpiresAt)
            : existing with
            {
                Content = Canonicalize($"{existing.Content}\n{content}"),
                Provenance = existing.Provenance with
                {
                    Evidence = existing.Provenance.Evidence.Concat(command.Provenance.Evidence)
                        .GroupBy(item => item.Identity, StringComparer.Ordinal).Select(group => group.First())
                        .OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray()
                },
                Version = existing.Version + 1,
                UpdatedAt = now,
                ExpiresAt = command.ExpiresAt
            };
        var write = await transaction.WriteHotMemoryAsync(
            authorization.AuthorizedScopes!, memory, existing?.Version ?? 0, cancellationToken).ConfigureAwait(false);
        if (!write.Applied) return new(HotMemoryOutcome.StaleVersion, existing, write.CurrentVersion,
            new(MemoryErrorCode.StaleVersion, null), _options.SupportedContractVersion);
        await transaction.SaveReceiptAsync(authorization.AuthorizedScopes!, new(HotMemoryKind, scope, command.Envelope.IdempotencyKey, fingerprint,
            memory.Id.Value, (existing is null ? HotMemoryOutcome.Created : HotMemoryOutcome.Updated).ToString(),
            _options.SupportedContractVersion), cancellationToken).ConfigureAwait(false);
        await transaction.EnqueueAsync(authorization.AuthorizedScopes!, Outbox(command.Envelope, scope, HotMemoryKind, memory.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(existing is null ? HotMemoryOutcome.Created : HotMemoryOutcome.Updated, memory, memory.Version, null,
            _options.SupportedContractVersion);
    }
}
