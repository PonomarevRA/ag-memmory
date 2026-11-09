using AgMemory.Contracts;

namespace AgMemory.Core;

internal sealed class HotMemoryStateCommandHandler(
    IMemoryStore store,
    CommandPreflight preflight,
    IIngressRedactor redactor,
    IClock clock,
    IIdGenerator ids,
    MemoryCoreOptions options)
{
    private const string CommandKind = "replace-hot-memory-state";

    public async Task<HotMemoryStateResult> ReplaceAsync(
        UpdateHotMemoryStateCommand command,
        HotMemoryPolicy policy,
        CancellationToken cancellationToken)
    {
        if (command is null || policy is null) return Failure(MemoryErrorCode.InvalidArgument, nameof(command));
        if (!preflight.TryValidateEnvelope(command.Envelope, out var envelopeError) ||
            command.ExpectedVersion < 0 || command.ExpiresAt.Offset != TimeSpan.Zero)
            return Failure(envelopeError ?? new(MemoryErrorCode.InvalidArgument, nameof(command)));

        try
        {
            command.State.Validate();
            command.Provenance.Validate();
        }
        catch (ArgumentException)
        {
            return Failure(MemoryErrorCode.InvalidArgument, nameof(command.State));
        }

        var authorization = await preflight.AuthorizeAsync(
            command.Envelope.Actor, MemoryOperation.AppendHotMemory, command.Envelope.RequestedScope, cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsAllowed) return Failure(authorization.Error!);
        var scope = CommandPreflight.ExactRequestedSelector(authorization.AuthorizedScopes!, command.Envelope.RequestedScope);
        if (scope is null) return Failure(new(MemoryErrorCode.Unauthorized, null, authorization.PolicyVersion));
        var now = UtcNow();
        if (command.ExpiresAt <= now) return Failure(MemoryErrorCode.InvalidArgument, nameof(command.ExpiresAt));

        var redacted = new List<HotMemoryEntry>(command.State.Entries.Count);
        foreach (var entry in command.State.Entries)
        {
            var result = await redactor.RedactAsync(new(entry.Content, null, null), cancellationToken).ConfigureAwait(false);
            if (!result.IsAccepted) return Failure(result.Error!);
            var content = CommandValueSupport.Canonicalize(result.Content!.CanonicalText);
            if (string.IsNullOrWhiteSpace(content)) return Failure(MemoryErrorCode.InvalidArgument, nameof(command.State));
            redacted.Add(entry with { Content = content });
        }

        HotMemoryState state;
        try
        {
            state = HotMemoryStateBounder.Bind(new(redacted), policy, now);
        }
        catch (ArgumentException)
        {
            return Failure(MemoryErrorCode.InvalidArgument, nameof(command.State));
        }

        var encoded = HotMemoryStateCodec.Encode(state);
        var fingerprint = CommandValueSupport.Hash($"{encoded}|{command.ExpiresAt:O}|{command.ExpectedVersion}");
        await using var transaction = await store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await transaction.FindReceiptAsync(
            authorization.AuthorizedScopes!, CommandKind, scope, command.Envelope.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (!string.Equals(receipt.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                return Failure(MemoryErrorCode.IdempotencyKeyConflict, null);
            var replay = await transaction.FindHotMemoryAsync(
                authorization.AuthorizedScopes!, command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
            return new(HotMemoryOutcome.IdempotencyReplay, replay,
                replay is not null && HotMemoryStateCodec.TryDecode(replay.Content, out var replayState) ? replayState : null,
                replay?.Version, null, options.SupportedContractVersion);
        }

        var existing = await transaction.FindHotMemoryAsync(
            authorization.AuthorizedScopes!, command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (existing is not null && command.ExpectedVersion == 0)
            return new(HotMemoryOutcome.Conflict, existing, null, existing.Version,
                new(MemoryErrorCode.Conflict, null), options.SupportedContractVersion);
        if (existing is not null && command.ExpectedVersion != existing.Version)
            return new(HotMemoryOutcome.StaleVersion, existing, null, existing.Version,
                new(MemoryErrorCode.StaleVersion, null), options.SupportedContractVersion);

        var memory = existing is null
            ? new SessionHotMemory(ids.NewMemoryId(), command.Envelope.RequestedScope, encoded, command.Provenance,
                1, now, now, command.ExpiresAt)
            : existing with
            {
                Content = encoded,
                Provenance = existing.Provenance with { Evidence = MergeEvidence(existing.Provenance, command.Provenance) },
                Version = existing.Version + 1,
                UpdatedAt = now,
                ExpiresAt = command.ExpiresAt
            };
        var write = await transaction.WriteHotMemoryAsync(
            authorization.AuthorizedScopes!, memory, existing?.Version ?? 0, cancellationToken).ConfigureAwait(false);
        if (!write.Applied)
            return new(HotMemoryOutcome.StaleVersion, existing, null, write.CurrentVersion,
                new(MemoryErrorCode.StaleVersion, null), options.SupportedContractVersion);

        var outcome = existing is null ? HotMemoryOutcome.Created : HotMemoryOutcome.Updated;
        await transaction.SaveReceiptAsync(authorization.AuthorizedScopes!, new(
            CommandKind, scope, command.Envelope.IdempotencyKey, fingerprint, memory.Id.Value, outcome.ToString(),
            options.SupportedContractVersion), cancellationToken).ConfigureAwait(false);
        await transaction.EnqueueAsync(authorization.AuthorizedScopes!, CommandOutbox.Create(
            command.Envelope, scope, CommandKind, memory.Id, options.SupportedContractVersion), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(outcome, memory, state, memory.Version, null, options.SupportedContractVersion);
    }

    private IReadOnlyList<SourceEvidenceRef> MergeEvidence(MemoryProvenance existing, MemoryProvenance incoming) =>
        existing.Evidence.Concat(incoming.Evidence)
            .GroupBy(item => item.Identity, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();

    private DateTimeOffset UtcNow()
    {
        var now = clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private HotMemoryStateResult Failure(MemoryErrorCode code, string? field) =>
        Failure(new MemoryError(code, field));

    private HotMemoryStateResult Failure(MemoryError error) =>
        new(HotMemoryOutcome.Failed, null, null, null, error, options.SupportedContractVersion);
}
