using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Structured session memory facade; durable promotion stays inside the command boundary.</summary>
public sealed class SessionHotMemoryCoordinator : IHotMemoryService
{
    private readonly MemoryCommandService _commands;
    private readonly IMemoryQueryService _query;
    private readonly HotMemoryPolicy _policy;
    private readonly IClock _clock;
    private readonly HotMemoryPromotionService _promotion;
    private readonly ContractVersion _contractVersion;

    public SessionHotMemoryCoordinator(
        MemoryCommandService commands,
        IMemoryQueryService query,
        HotMemoryPolicy policy,
        IClock clock,
        MemoryCoreOptions options)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _policy.Validate();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _contractVersion = options.SupportedContractVersion;
        _promotion = new(_commands, _clock, _policy, _contractVersion);
    }

    public Task<HotMemoryStateResult> UpdateAsync(
        UpdateHotMemoryStateCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null) return Task.FromResult(Failure(MemoryErrorCode.InvalidArgument, nameof(command)));
        var now = UtcNow();
        if (command.ExpiresAt.Offset != TimeSpan.Zero || command.ExpectedVersion < 0 ||
            command.ExpiresAt <= now || command.ExpiresAt - now > _policy.MaximumTtl)
            return Task.FromResult(Failure(MemoryErrorCode.InvalidArgument, nameof(command.ExpiresAt)));
        try
        {
            var bounded = HotMemoryStateBounder.Bind(command.State, _policy, now);
            return _commands.ReplaceHotMemoryStateAsync(command with { State = bounded }, _policy, cancellationToken);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(Failure(MemoryErrorCode.InvalidArgument, nameof(command.State)));
        }
    }

    public async Task<HotMemoryStateSnapshot?> ReadStateAsync(
        HotMemoryReadRequest request,
        CancellationToken cancellationToken)
    {
        var memory = await _query.ReadHotMemoryAsync(request, cancellationToken).ConfigureAwait(false);
        return memory is not null && HotMemoryStateCodec.TryDecode(memory.Content, out var state) && state is not null
            ? new(memory, state)
            : null;
    }

    public async Task<HotMemoryPromotionResult> PromoteAsync(
        HotMemoryPromotionCommand command,
        CancellationToken cancellationToken)
    {
        if (command is null || string.IsNullOrWhiteSpace(command.EntryKey) || command.ExpectedHotMemoryVersion <= 0)
            return PromotionFailure(MemoryErrorCode.InvalidArgument, nameof(command));
        try
        {
            command.Envelope.Validate();
        }
        catch (ArgumentException)
        {
            return PromotionFailure(MemoryErrorCode.InvalidArgument, nameof(command.Envelope));
        }
        if (command.Envelope.ContractVersion != _contractVersion)
            return PromotionFailure(MemoryErrorCode.UnsupportedContractVersion, nameof(command.Envelope.ContractVersion));

        var snapshot = await ReadStateAsync(new(
            command.Envelope.Actor, command.Envelope.RequestedScope, command.Envelope.ContractVersion), cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
            return new(HotMemoryPromotionOutcome.NotFound, null, null,
                new(MemoryErrorCode.NotFound, null), _contractVersion);
        if (snapshot.Memory.Version != command.ExpectedHotMemoryVersion)
            return new(HotMemoryPromotionOutcome.StaleVersion, null, snapshot.Memory.Version,
                new(MemoryErrorCode.StaleVersion, null), _contractVersion);
        return await _promotion.PromoteAsync(command, snapshot, cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset UtcNow()
    {
        var now = _clock.UtcNow;
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private HotMemoryStateResult Failure(MemoryErrorCode code, string? field) =>
        new(HotMemoryOutcome.Failed, null, null, null, new(code, field), _contractVersion);

    private HotMemoryPromotionResult PromotionFailure(MemoryErrorCode code, string? field) =>
        new(HotMemoryPromotionOutcome.Failed, null, null, new(code, field), _contractVersion);
}
