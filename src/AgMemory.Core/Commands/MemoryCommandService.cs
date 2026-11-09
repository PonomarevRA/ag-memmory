using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>
/// Provider-neutral command pipeline. It is deliberately the only Core location
/// that sequences authorization, redaction, idempotency, and transactional writes.
/// </summary>
public sealed partial class MemoryCommandService : IMemoryCommandService, ICommandReceiver
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

    /// <summary>
    /// Receives a durable-memory command through the generic command receiver boundary.
    /// </summary>
    public Task<RememberResult> ReceiveAsync(RememberCommand command, CancellationToken cancellationToken) =>
        RememberAsync(command, cancellationToken);

    /// <summary>
    /// Replaces bounded session state after the hot-memory policy has accepted the requested update.
    /// </summary>
    internal Task<HotMemoryStateResult> ReplaceHotMemoryStateAsync(
        UpdateHotMemoryStateCommand command,
        HotMemoryPolicy policy,
        CancellationToken cancellationToken) =>
        _hotMemoryStateHandler.ReplaceAsync(command, policy, cancellationToken);
}
