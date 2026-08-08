using System.Security.Cryptography;
using System.Text;
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
    private readonly IAuthorizationScopeValidator _authorization;
    private readonly IIngressRedactor _redactor;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IEmbeddingPolicy _embeddingPolicy;
    private readonly IRetentionPolicy _retentionPolicy;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly MemoryCoreOptions _options;

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
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _embeddingProvider = embeddingProvider ?? throw new ArgumentNullException(nameof(embeddingProvider));
        _embeddingPolicy = embeddingPolicy ?? throw new ArgumentNullException(nameof(embeddingPolicy));
        _retentionPolicy = retentionPolicy ?? throw new ArgumentNullException(nameof(retentionPolicy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public Task<RememberResult> ReceiveAsync(RememberCommand command, CancellationToken cancellationToken) =>
        RememberAsync(command, cancellationToken);

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
        if (canonical.EmbeddingMode == EmbeddingMode.Required)
        {
            var policy = await _embeddingPolicy.GetAsync(command.Envelope.RequestedScope, cancellationToken).ConfigureAwait(false);
            if (!policy.IsConfigured || policy.Contract is null)
                return RememberFailure(new(MemoryErrorCode.PolicyNotConfigured, null, policy.PolicyVersion));
            var generated = await _embeddingProvider.CreateAsync(canonical.CanonicalText, policy.Contract, cancellationToken).ConfigureAwait(false);
            if (!policy.Contract.Matches(generated.Reference))
                return RememberFailure(new(MemoryErrorCode.EmbeddingContractMismatch, null, policy.PolicyVersion));
            embedding = generated.Reference;
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
                canonical.ExpiresAt, deduplicationKey, canonical.DecisionDetails);
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
        await _authorization.AuthorizeAsync(actor, operation, scope, cancellationToken).ConfigureAwait(false);

    private bool TryValidateEnvelope(CommandEnvelope envelope, out MemoryError? error)
    {
        error = null;
        try
        {
            envelope.Validate();
            if (envelope.ContractVersion != _options.SupportedContractVersion)
                error = new(MemoryErrorCode.UnsupportedContractVersion, nameof(envelope.ContractVersion));
        }
        catch (ArgumentException)
        {
            error = new(MemoryErrorCode.InvalidArgument, nameof(envelope));
        }
        return error is null;
    }

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
        set.Selectors.SingleOrDefault(selector => selector.Matches(requested));

    private static void ValidateUnitInterval(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0d or > 1d)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static bool TryTransition(
        MemoryLifecycleStatus current, MemoryLifecycleAction action, bool hasRelated, out MemoryLifecycleStatus next)
    {
        next = current;
        switch (action)
        {
            case MemoryLifecycleAction.Confirm when current == MemoryLifecycleStatus.Draft && hasRelated:
                next = MemoryLifecycleStatus.Active;
                return true;
            case MemoryLifecycleAction.Invalidate when current is MemoryLifecycleStatus.Draft or MemoryLifecycleStatus.Active:
                next = MemoryLifecycleStatus.Invalid;
                return true;
            case MemoryLifecycleAction.Supersede when current == MemoryLifecycleStatus.Active && hasRelated:
            case MemoryLifecycleAction.ResolveConflict when current == MemoryLifecycleStatus.Active && hasRelated:
                next = MemoryLifecycleStatus.Superseded;
                return true;
            case MemoryLifecycleAction.Contradict when current == MemoryLifecycleStatus.Active && hasRelated:
                return true;
            case MemoryLifecycleAction.UndoSupersede when current == MemoryLifecycleStatus.Superseded:
                next = MemoryLifecycleStatus.Active;
                return true;
            default:
                return false;
        }
    }

    private DateTimeOffset UtcNow()
    {
        var now = _clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private OutboxMessage Outbox(CommandEnvelope envelope, ScopeSelector scope, string kind, MemoryId id) => new(
        scope, Hash($"{envelope.CommandId.Value}|{kind}"), kind, envelope.CommandId, envelope.CorrelationId,
        id.Value, _options.SupportedContractVersion);

    private RememberResult RememberFailure(MemoryErrorCode code, string? field) => RememberFailure(new(code, field));
    private RememberResult RememberFailure(MemoryError error) => new(RememberOutcome.Failed, null, error, _options.SupportedContractVersion);
    private LifecycleResult LifecycleFailure(MemoryErrorCode code, string? field) => LifecycleFailure(new(code, field));
    private LifecycleResult LifecycleFailure(MemoryError error) => new(LifecycleOutcome.Failed, null, null, error, _options.SupportedContractVersion);
    private HotMemoryResult HotFailure(MemoryErrorCode code, string? field) => HotFailure(new(code, field));
    private HotMemoryResult HotFailure(MemoryError error) => new(HotMemoryOutcome.Failed, null, null, error, _options.SupportedContractVersion);
    private ForgetResult ForgetFailure(MemoryErrorCode code, string? field) => ForgetFailure(new(code, field));
    private ForgetResult ForgetFailure(MemoryError error) => new(ForgetOutcome.Failed, null, error, _options.SupportedContractVersion);

    internal static string Canonicalize(string value) => string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    internal static IReadOnlyList<string> NormalizeEntities(IEnumerable<string> entities) => entities
        .Select(Canonicalize).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal)
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    internal static int EstimateTokenCost(string content) => Math.Max(1, (content.Length + 3) / 4);
    internal static string ScopeKey(MemoryScope scope) => string.Join("|", new[]
    {
        scope.TenantId.Value, scope.ProjectId?.Value ?? "<null>", scope.WorkspaceId?.Value ?? "<null>",
        scope.ChatId?.Value ?? "<null>", scope.RunId?.Value ?? "<null>"
    });
    internal static string Hash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
}

/// <summary>Provider-neutral query orchestration. Authorization happens before every retrieval port.</summary>
public sealed class MemoryQueryService : IMemoryQueryService
{
    private readonly IMemoryStore _store;
    private readonly IAuthorizationScopeValidator _authorization;
    private readonly ILexicalSearch _lexical;
    private readonly IVectorSearch _vector;
    private readonly IMemoryGraph _graph;
    private readonly IEmbeddingPolicy _embeddingPolicy;
    private readonly IClock _clock;
    private readonly MemoryCoreOptions _options;

    public MemoryQueryService(
        IMemoryStore store,
        IAuthorizationScopeValidator authorization,
        ILexicalSearch lexical,
        IVectorSearch vector,
        IMemoryGraph graph,
        IEmbeddingPolicy embeddingPolicy,
        IClock clock,
        MemoryCoreOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _lexical = lexical ?? throw new ArgumentNullException(nameof(lexical));
        _vector = vector ?? throw new ArgumentNullException(nameof(vector));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _embeddingPolicy = embeddingPolicy ?? throw new ArgumentNullException(nameof(embeddingPolicy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<MemorySearchResult> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.Limit <= 0 || request.ContractVersion != _options.SupportedContractVersion)
            return SearchFailure(request, request?.ContractVersion == _options.SupportedContractVersion
                ? new(MemoryErrorCode.InvalidArgument, nameof(request))
                : new(MemoryErrorCode.UnsupportedContractVersion, nameof(MemorySearchRequest.ContractVersion)));
        try { request.RequestedScope.Validate(); }
        catch (ArgumentException) { return SearchFailure(request, new(MemoryErrorCode.InvalidArgument, nameof(request.RequestedScope))); }

        var authorization = await _authorization.AuthorizeAsync(
            request.Actor, MemoryOperation.Search, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAllowed) return SearchFailure(request, authorization.Error!);
        var now = UtcNow();
        var policy = await _embeddingPolicy.GetAsync(request.RequestedScope, cancellationToken).ConfigureAwait(false);
        var eligibility = new MemorySearchEligibility(authorization.AuthorizedScopes!, request.Types, now);
        var portRequest = new SearchPortRequest(eligibility, request.QueryText, null, request.QueryEmbedding, policy.Contract, request.Limit);
        var lexicalTask = _lexical.SearchAsync(portRequest, cancellationToken);
        var vectorTask = _vector.SearchAsync(portRequest, cancellationToken);
        await Task.WhenAll(lexicalTask, vectorTask).ConfigureAwait(false);
        IReadOnlyList<MemorySearchHit> fused;
        try
        {
            fused = DeterministicRetrieval.Fuse(lexicalTask.Result, vectorTask.Result, authorization.AuthorizedScopes!, now,
                request.Types, request.Statuses, request.Limit, _options.ReciprocalRankConstant,
                request.RetrievalConfigurationVersion, _options.RerankerConfigurationVersion);
        }
        catch (ArgumentException)
        {
            return SearchFailure(request, new(MemoryErrorCode.DependencyFailure, null));
        }
        var graph = await _graph.RerankAsync(new(eligibility, fused.Select(hit => hit.Record).ToArray()), cancellationToken).ConfigureAwait(false);
        var validGraph = graph.Where(item => fused.Any(hit => hit.Record.Id == item.MemoryId) && double.IsFinite(item.Score))
            .GroupBy(item => item.MemoryId).ToDictionary(group => group.Key, group => group.Max(item => item.Score));
        var reranked = DeterministicRetrieval.Fuse(lexicalTask.Result, vectorTask.Result, authorization.AuthorizedScopes!, now,
            request.Types, request.Statuses, request.Limit, _options.ReciprocalRankConstant,
            request.RetrievalConfigurationVersion, _options.RerankerConfigurationVersion, validGraph);
        return new(reranked, null, _options.SupportedContractVersion, request.RetrievalConfigurationVersion);
    }

    public async Task<MemoryContext> BuildContextAsync(MemoryContextRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.TokenBudget <= 0 || request.SearchLimit <= 0 || request.ContractVersion != _options.SupportedContractVersion)
            return ContextFailure(request, request?.ContractVersion == _options.SupportedContractVersion
                ? new(MemoryErrorCode.InvalidArgument, nameof(request))
                : new(MemoryErrorCode.UnsupportedContractVersion, nameof(MemoryContextRequest.ContractVersion)));
        var contextAuthorization = await _authorization.AuthorizeAsync(
            request.Actor, MemoryOperation.BuildContext, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!contextAuthorization.IsAllowed)
            return ContextFailure(request, contextAuthorization.Error!);
        var hot = contextAuthorization.AuthorizedScopes!.Contains(request.RequestedScope)
            ? await _store.GetHotMemoryAsync(contextAuthorization.AuthorizedScopes!, request.RequestedScope, cancellationToken).ConfigureAwait(false)
            : null;
        var eligibleHot = hot is not null && hot.Scope == request.RequestedScope && hot.ExpiresAt > UtcNow()
            ? ToSearchHit(hot, request.RetrievalConfigurationVersion)
            : null;
        var search = await SearchAsync(new(
            request.Actor, request.RequestedScope, request.QueryText, request.QueryEmbedding, request.Types, null,
            request.SearchLimit, request.RetrievalConfigurationVersion, request.ContractVersion), cancellationToken).ConfigureAwait(false);
        if (search.Error is not null)
            return ContextFailure(request, search.Error);
        IReadOnlyList<MemorySearchHit> hits = eligibleHot is null
            ? search.Hits
            : [eligibleHot, ..search.Hits.Where(hit => hit.Record.Id != eligibleHot.Record.Id)];
        if (hits.Count == 0 && request.AllowSingleSummaryFallback)
        {
            var authorization = await _authorization.AuthorizeAsync(
                request.Actor, MemoryOperation.BuildContext, request.RequestedScope, cancellationToken).ConfigureAwait(false);
            if (!authorization.IsAllowed) return ContextFailure(request, authorization.Error!);
            var now = UtcNow();
            var summary = (await _store.ListAsync(authorization.AuthorizedScopes!, cancellationToken).ConfigureAwait(false))
                .Where(record => record.Type == MemoryRecordType.Summary &&
                    DeterministicRetrieval.IsEligible(record, authorization.AuthorizedScopes!, now))
                .OrderBy(record => record.Id.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            if (summary is not null)
                hits = [new(summary, 1, 0d, new(null, null, 0d, 0d, 0d, 0d),
                    request.RetrievalConfigurationVersion, _options.RerankerConfigurationVersion)];
        }

        var selected = new List<MemorySearchHit>();
        var cost = 0;
        foreach (var hit in hits)
        {
            if (hit.Record.EstimatedTokenCost > request.TokenBudget - cost) continue;
            selected.Add(hit);
            cost += hit.Record.EstimatedTokenCost;
        }
        var citations = selected.Select(hit => new MemoryCitation(hit.Record.Id,
            hit.Record.Provenance.Evidence.Select(item => item.Identity).OrderBy(id => id, StringComparer.Ordinal).ToArray())).ToArray();
        var content = string.Join("\n", selected.Select(hit => $"[{hit.Record.Id.Value}] {hit.Record.CanonicalText}"));
        return new(content, request.RequireCitations ? citations : [], cost, hits.Count - selected.Count, null,
            _options.SupportedContractVersion, request.RetrievalConfigurationVersion, request.ContextConfigurationVersion);
    }

    public async Task<SessionHotMemory?> ReadHotMemoryAsync(HotMemoryReadRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.ContractVersion != _options.SupportedContractVersion) return null;
        var authorization = await _authorization.AuthorizeAsync(
            request.Actor, MemoryOperation.ReadHotMemory, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        if (!authorization.IsAllowed || !authorization.AuthorizedScopes!.Contains(request.RequestedScope)) return null;
        var memory = await _store.GetHotMemoryAsync(authorization.AuthorizedScopes!, request.RequestedScope, cancellationToken).ConfigureAwait(false);
        return memory is not null && memory.Scope == request.RequestedScope && memory.ExpiresAt > UtcNow() ? memory : null;
    }

    private DateTimeOffset UtcNow()
    {
        var now = _clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private MemorySearchResult SearchFailure(MemorySearchRequest? request, MemoryError error) => new(
        [], error, _options.SupportedContractVersion,
        request?.RetrievalConfigurationVersion ?? _options.SupportedContractVersion);

    private MemoryContext ContextFailure(MemoryContextRequest? request, MemoryError error) => new(
        string.Empty, [], 0, 0, error, _options.SupportedContractVersion,
        request?.RetrievalConfigurationVersion ?? _options.SupportedContractVersion,
        request?.ContextConfigurationVersion ?? _options.SupportedContractVersion);

    private MemorySearchHit ToSearchHit(SessionHotMemory hot, ContractVersion retrievalConfigurationVersion)
    {
        var record = new MemoryRecord(hot.Id, hot.Scope, MemoryRecordType.Summary, MemoryLifecycleStatus.Active,
            hot.Content, null, 1d, 1d, MemoryCommandService.EstimateTokenCost(hot.Content), hot.CreatedAt,
            hot.UpdatedAt, hot.Version, [], hot.Provenance, null, hot.ExpiresAt, $"hot:{hot.Id.Value}");
        return new(record, 0, 0d, new(null, null, 0d, 0d, 0d, 0d), retrievalConfigurationVersion,
            _options.RerankerConfigurationVersion);
    }
}
