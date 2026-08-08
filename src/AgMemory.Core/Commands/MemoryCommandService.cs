using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>
/// Provider-neutral command pipeline. It is deliberately the only Core location
/// that sequences authorization, redaction, idempotency, and transactional writes.
/// </summary>
public sealed class MemoryCommandService : IMemoryCommandService, ICommandReceiver
{
    private const string RememberKind = "remember";
    private const string LifecycleKind = "lifecycle";
    private const string HotMemoryKind = "append-hot-memory";
    private const string ForgetKind = "forget";

    private readonly IMemoryStore _store;
    private readonly CommandPreflight _preflight;
    private readonly IIngressRedactor _redactor;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IEmbeddingPolicy _embeddingPolicy;
    private readonly IRetentionPolicy _retentionPolicy;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly MemoryCoreOptions _options;
    private readonly HotMemoryStateCommandHandler _hotMemoryStateHandler;

    public MemoryCommandService(
        IMemoryStore store,
        IAuthorizationScopeValidator authorization,
        IIngressRedactor redactor,
        IEmbeddingProvider embeddingProvider,
        IEmbeddingPolicy embeddingPolicy,
        IRetentionPolicy retentionPolicy,
        IClock clock,
        IIdGenerator ids,
        MemoryCoreOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _embeddingPolicy = embeddingPolicy ?? throw new ArgumentNullException(nameof(embeddingPolicy));
        _retentionPolicy = retentionPolicy ?? throw new ArgumentNullException(nameof(retentionPolicy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _preflight = new(authorization ?? throw new ArgumentNullException(nameof(authorization)), _options);
        _hotMemoryStateHandler = new(_store, _preflight, _redactor, _clock, _ids, _options);
    }

    public Task<RememberResult> ReceiveAsync(RememberCommand command, CancellationToken cancellationToken) =>
        RememberAsync(command, cancellationToken);

    internal Task<HotMemoryStateResult> ReplaceHotMemoryStateAsync(
        UpdateHotMemoryStateCommand command,
        HotMemoryPolicy policy,
        CancellationToken cancellationToken) =>
        _hotMemoryStateHandler.ReplaceAsync(command, policy, cancellationToken);

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
            if (input.Type == MemoryRecordType.Decision) (input.DecisionDetails ?? throw new ArgumentException()).Validate();
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
                Entities = NormalizeEntities(input.Entities)
            };
            if (string.IsNullOrWhiteSpace(canonical.CanonicalText)) throw new ArgumentException();
            if (canonical.Reason is not null && string.IsNullOrWhiteSpace(canonical.Reason)) throw new ArgumentException();
            if (canonical.Type == MemoryRecordType.Decision) canonical.DecisionDetails!.Validate();
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
