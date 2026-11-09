using AgMemory.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace AgMemory.Core;

/// <summary>Serializes revision-checked corrections through the store transaction; native failure may require reconciliation.</summary>
public sealed class MemoryCorrectionService(IMemoryStore store, IAuthorizationScopeValidator authorization,
    IIngressRedactor redactor, IClock clock)
{
    private const string Kind = "CorrectMemory";

    /// <summary>Checks replay before revision, then stages source invalidation, optional replacement and receipt. Never hard deletes.</summary>
    public async Task<CorrectMemoryResult> CorrectAsync(CorrectMemoryCommand command, CancellationToken cancellationToken)
    {
        if (command is null) return Failure(MemoryErrorCode.InvalidArgument);
        try { command.Envelope.Validate(); command.MemoryId.Validate(nameof(command.MemoryId)); }
        catch (ArgumentException) { return Failure(MemoryErrorCode.InvalidArgument); }
        if (command.ExpectedVersion <= 0) return Failure(MemoryErrorCode.InvalidArgument);
        var access = await authorization.AuthorizeAsync(command.Envelope.Actor, MemoryOperation.Lifecycle,
            command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!access.IsAllowed) return Failure(MemoryErrorCode.Unauthorized);
        var selector = new ScopeSelector(command.Envelope.RequestedScope);
        if (!access.AuthorizedScopes!.Contains(selector.Scope)) return Failure(MemoryErrorCode.Unauthorized);
        var scopes = new AuthorizedScopeSet([selector]);
        var replacement = command.Replacement;
        if (replacement is not null)
        {
            var rememberAccess = await authorization.AuthorizeAsync(command.Envelope.Actor, MemoryOperation.Remember,
                selector.Scope, cancellationToken).ConfigureAwait(false);
            if (!rememberAccess.IsAllowed || !rememberAccess.AuthorizedScopes!.Contains(selector.Scope)) return Failure(MemoryErrorCode.Unauthorized);
            if (!Enum.IsDefined(replacement.Type) || replacement.EmbeddingMode != EmbeddingMode.None || replacement.ExpiresAt <= clock.UtcNow)
                return Failure(MemoryErrorCode.InvalidArgument);
            var redaction = await redactor.RedactAsync(new(replacement.CanonicalText, replacement.Reason,
                replacement.DecisionDetails), cancellationToken).ConfigureAwait(false);
            if (!redaction.IsAccepted) return Failure(MemoryErrorCode.RedactionRejected);
            replacement = replacement with { CanonicalText = CommandValueSupport.Canonicalize(redaction.Content!.CanonicalText),
                Reason = redaction.Content.Reason, DecisionDetails = redaction.Content.DecisionDetails };
        }
        var fingerprint = CommandValueSupport.Hash(System.Text.Json.JsonSerializer.Serialize(new
            { command.MemoryId, command.ExpectedVersion, Replacement = replacement }));
        await using var transaction = await store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await transaction.FindReceiptAsync(scopes, Kind, selector, command.Envelope.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (receipt.PayloadFingerprint != fingerprint) return Failure(MemoryErrorCode.IdempotencyKeyConflict);
            var replay = receipt.MemoryId is null ? null : await transaction.FindRecordAsync(scopes, new(receipt.MemoryId), cancellationToken).ConfigureAwait(false);
            return new("Replay", replay, null);
        }
        var current = await transaction.FindRecordAsync(scopes, command.MemoryId, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow.ToUniversalTime();
        var marker = "correction-v1:" + CommandValueSupport.Hash(command.Envelope.IdempotencyKey + "|" + fingerprint);
        var targetStatus = replacement is null ? MemoryLifecycleStatus.Invalid : MemoryLifecycleStatus.Superseded;
        // The marker and deterministic replacement ID permit retry after a partial native commit.
        // A different request cannot claim another correction's source revision.
        var recovering = current is not null && current.Version == command.ExpectedVersion + 1 &&
            current.Status == targetStatus && current.Reason == marker;
        if (current is null || current.ExpiresAt <= now || (!recovering &&
            (current.Version != command.ExpectedVersion || current.Status != MemoryLifecycleStatus.Active)))
            return Failure(MemoryErrorCode.StaleVersion);
        MemoryRecord? created = null;
        if (replacement is not null)
        {
            var identity = System.Text.Json.JsonSerializer.Serialize(new { command.Envelope.Actor, command.Envelope.RequestedScope,
                command.Envelope.IdempotencyKey, command.MemoryId });
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            var replacementId = new MemoryId(new Guid(bytes.AsSpan(0, 16)).ToString("D"));
            created = new(replacementId, current.Scope, replacement.Type, MemoryLifecycleStatus.Active,
                replacement.CanonicalText, replacement.Reason, replacement.Importance, replacement.Confidence,
                CommandValueSupport.EstimateTokenCost(replacement.CanonicalText), now, now, 1,
                CommandValueSupport.NormalizeEntities(replacement.Entities), replacement.Provenance, null, replacement.ExpiresAt,
                CommandValueSupport.Hash($"{CommandValueSupport.ScopeKey(current.Scope)}|{replacement.Type}|{replacement.CanonicalText}"), replacement.DecisionDetails);
            try { created.Validate(); }
            catch (ArgumentException) { return Failure(MemoryErrorCode.InvalidArgument); }
            var duplicate = await transaction.FindByDeduplicationKeyAsync(scopes, selector, created.DeduplicationKey, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                if (!recovering || !MatchesIntendedReplacement(duplicate, created)) return Failure(MemoryErrorCode.Conflict);
            }
        }
        // Source first: a failed native commit must never knowingly leave both versions active.
        var source = current;
        if (!recovering)
        {
            source = current with { Status = targetStatus, Reason = marker, Version = current.Version + 1, UpdatedAt = now };
            var changed = await transaction.WriteRecordAsync(scopes, source, current.Version, cancellationToken).ConfigureAwait(false);
            if (!changed.Applied) return Failure(MemoryErrorCode.StaleVersion);
        }
        if (created is not null)
        {
            var existingReplacement = await transaction.FindRecordAsync(scopes, created.Id, cancellationToken).ConfigureAwait(false);
            if (existingReplacement is null)
            {
                var inserted = await transaction.WriteRecordAsync(scopes, created, 0, cancellationToken).ConfigureAwait(false);
                if (!inserted.Applied) return Failure(MemoryErrorCode.Conflict);
            }
            else
            {
                if (!recovering || !MatchesIntendedReplacement(existingReplacement, created)) return Failure(MemoryErrorCode.Conflict);
                // Return the revalidated persisted instance, never a reconstructed replacement that was not written.
                created = existingReplacement;
            }
        }
        await transaction.SaveReceiptAsync(scopes, new(Kind, selector, command.Envelope.IdempotencyKey,
            fingerprint, created?.Id.Value, "Applied", command.Envelope.ContractVersion), cancellationToken).ConfigureAwait(false);
        await transaction.EnqueueAsync(scopes, new(selector, Guid.NewGuid().ToString("N"), Kind, command.Envelope.CommandId,
            command.Envelope.CorrelationId, source.Id.Value, command.Envelope.ContractVersion), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new("Applied", created, null);
    }

    /// <summary>Accepts only the exact intended revision and durable payload during recovery; original server timestamps may differ on retry.</summary>
    private static bool MatchesIntendedReplacement(MemoryRecord actual, MemoryRecord intended)
    {
        var comparable = intended with { CreatedAt = actual.CreatedAt, UpdatedAt = actual.UpdatedAt };
        // Include status, version, identity, scope, metadata and evidence, not merely canonical text or deduplication key.
        return System.Text.Json.JsonSerializer.Serialize(actual) == System.Text.Json.JsonSerializer.Serialize(comparable);
    }

    private static CorrectMemoryResult Failure(MemoryErrorCode code) => new("Failed", null, new(code, null));
}
