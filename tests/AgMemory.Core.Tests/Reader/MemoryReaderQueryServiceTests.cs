using System.Text;
using AgMemory.Contracts;
using AgMemory.Core;
using Xunit;

namespace AgMemory.Core.Tests.Reader;

public sealed class MemoryReaderQueryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly ActorId Actor = new("reader-actor");
    private static readonly MemoryScope Scope = new(new("tenant-a"), new("project-a"), new("workspace-a"), new("chat-a"), new("run-a"));
    private static readonly ContractVersion ContractVersion = new("reader-test-v1");

    [Fact]
    public async Task ReadHome_UsesExactScopeRendersEightBlocksAndResolvesLocalAndRemoteLinks()
    {
        var foreignScope = Scope with { RunId = new ScopeId("other-run") };
        var remote = Record("remote", Scope, "# Remote ^remote\nremote body");
        var encodedRemote = Encode(remote.MemoryId);
        var home = Record("home", Scope, Blocks(10, firstBody:
            $"[[#part-2|local link]] [[memory:{encodedRemote}#remote|remote link]]"));
        var source = new ReaderSource([home, remote]);
        var authorization = new FixedAuthorization();
        authorization.Allow(Actor, MemoryOperation.ReaderRead, Scope, foreignScope);
        var service = Service(source, authorization);

        var result = await service.ReadHomeAsync(new(Actor, Scope, home.MemoryId, ContractVersion), default);

        Assert.Equal(MemoryReaderDocumentState.Available, result.State);
        Assert.Equal("route-home", result.RouteKey);
        Assert.Equal(MemoryReaderLimits.BlocksPerPage, result.Blocks.Count);
        Assert.Equal("part-1", result.Blocks[0].BlockKey);
        Assert.Equal(new MemoryReaderBlockCursor(1, 8), result.NextCursor);
        Assert.Contains(result.Blocks[0].Content, inline =>
            inline.Kind == MemoryReaderInlineKind.Link && inline.Text == "local link" &&
            inline.RouteKey == "route-home" && inline.BlockKey == "part-2");
        Assert.Contains(result.Blocks[0].Content, inline =>
            inline.Kind == MemoryReaderInlineKind.Link && inline.Text == "remote link" &&
            inline.RouteKey == "route-remote" && inline.BlockKey == "remote");
        Assert.Equal(2, source.RouteCalls);
        Assert.All(source.EligibilityRequests, eligibility =>
        {
            Assert.True(eligibility.AuthorizedScopes.Contains(Scope));
            Assert.False(eligibility.AuthorizedScopes.Contains(foreignScope));
        });
    }

    [Fact]
    public async Task ReadHome_MalformedAndIneligibleRemoteLinksRemainLiteralText()
    {
        var foreign = Record("foreign", Scope with { RunId = new ScopeId("other-run") }, "foreign");
        var inactive = Record("inactive", Scope, "inactive") with { Status = MemoryLifecycleStatus.Invalid };
        var expired = Record("expired", Scope, "expired") with { ExpiresAt = Now };
        var malformed = "[[memory:not-base64#root|malformed]]";
        var foreignLink = $"[[memory:{Encode(foreign.MemoryId)}#root|foreign]]";
        var inactiveLink = $"[[memory:{Encode(inactive.MemoryId)}#root|inactive]]";
        var expiredLink = $"[[memory:{Encode(expired.MemoryId)}#root|expired]]";
        var missingLink = $"[[memory:{Encode(new MemoryId("missing"))}#root|missing]]";
        var home = Record("home", Scope, $"# Root ^start\n{malformed} {foreignLink} {inactiveLink} {expiredLink} {missingLink}");
        var source = new ReaderSource([home, foreign, inactive, expired]);
        var authorization = Allowed();
        var service = Service(source, authorization);

        var result = await service.ReadHomeAsync(new(Actor, Scope, home.MemoryId, ContractVersion), default);

        Assert.Equal(MemoryReaderDocumentState.Available, result.State);
        var content = Assert.Single(result.Blocks).Content;
        Assert.Contains(content, inline => inline.Kind == MemoryReaderInlineKind.Text && inline.Text == malformed);
        Assert.Contains(content, inline => inline.Kind == MemoryReaderInlineKind.Text && inline.Text == foreignLink);
        Assert.Contains(content, inline => inline.Kind == MemoryReaderInlineKind.Text && inline.Text == inactiveLink);
        Assert.Contains(content, inline => inline.Kind == MemoryReaderInlineKind.Text && inline.Text == expiredLink);
        Assert.Contains(content, inline => inline.Kind == MemoryReaderInlineKind.Text && inline.Text == missingLink);
        Assert.Equal(1, source.RouteCalls);
    }

    [Fact]
    public async Task ReadHome_MissingAndSyntheticLinkTargetsRemainLiteralText()
    {
        var remoteMissing = Record("remote-missing", Scope, "# Remote ^present\nbody");
        var remoteAutomatic = Record("remote-automatic", Scope, "# Automatic\nbody");
        var localMissing = "[[#missing-local|missing local]]";
        var remoteMissingLink = $"[[memory:{Encode(remoteMissing.MemoryId)}#missing-remote|missing remote]]";
        var remoteAutomaticLink = $"[[memory:{Encode(remoteAutomatic.MemoryId)}#auto-1|automatic remote]]";
        var home = Record("home", Scope, $"# Root ^start\n{localMissing} {remoteMissingLink} {remoteAutomaticLink}");
        var source = new ReaderSource([home, remoteMissing, remoteAutomatic]);

        var result = await Service(source, Allowed()).ReadHomeAsync(new(Actor, Scope, home.MemoryId, ContractVersion), default);

        Assert.Equal(MemoryReaderDocumentState.Available, result.State);
        var content = Assert.Single(result.Blocks).Content;
        Assert.Contains(content, inline => inline.Kind == MemoryReaderInlineKind.Text && inline.Text == localMissing);
        Assert.Contains(content, inline => inline.Kind == MemoryReaderInlineKind.Text && inline.Text == remoteMissingLink);
        Assert.Contains(content, inline => inline.Kind == MemoryReaderInlineKind.Text && inline.Text == remoteAutomaticLink);
        Assert.Equal(1, source.RouteCalls);
    }

    [Fact]
    public async Task ReadHome_DuplicateExplicitAnchorsAreUnavailable()
    {
        var home = Record("home", Scope, "# First ^duplicate\nfirst\n# Second ^duplicate\nsecond");
        var source = new ReaderSource([home]);

        var result = await Service(source, Allowed()).ReadHomeAsync(new(Actor, Scope, home.MemoryId, ContractVersion), default);

        Assert.Equal(MemoryReaderDocumentState.Unavailable, result.State);
        Assert.Empty(result.Blocks);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task ReadHome_LongBlockPrefersParagraphBoundaryAndKeepsSoftLineBreaks()
    {
        const string softBreak = "soft-line-one\nsoft-line-two";
        var firstParagraph = softBreak + new string('a', 5_000);
        var secondParagraph = new string('b', 5_000);
        var home = Record("home", Scope, $"{firstParagraph}\n\n{secondParagraph}");
        var source = new ReaderSource([home]);

        var result = await Service(source, Allowed()).ReadHomeAsync(new(Actor, Scope, home.MemoryId, ContractVersion), default);

        Assert.Equal(MemoryReaderDocumentState.Available, result.State);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal("root", result.Blocks[0].BlockKey);
        Assert.Equal("root--2", result.Blocks[1].BlockKey);
        var first = Assert.Single(result.Blocks[0].Content).Text;
        var second = Assert.Single(result.Blocks[1].Content).Text;
        Assert.Contains(softBreak, first, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", first, StringComparison.Ordinal);
        Assert.Equal(secondParagraph, second);
        Assert.All(result.Blocks, block => Assert.True(Assert.Single(block.Content).Text.Length <= MemoryReaderLimits.MaximumBlockCharacters));
    }

    [Fact]
    public async Task ReadHome_OversizedSingleParagraphUsesBoundedHardFallback()
    {
        var paragraph = new string('x', MemoryReaderLimits.MaximumBlockCharacters + 37);
        var home = Record("home", Scope, paragraph);
        var source = new ReaderSource([home]);

        var result = await Service(source, Allowed()).ReadHomeAsync(new(Actor, Scope, home.MemoryId, ContractVersion), default);

        Assert.Equal(MemoryReaderDocumentState.Available, result.State);
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(MemoryReaderLimits.MaximumBlockCharacters, Assert.Single(result.Blocks[0].Content).Text.Length);
        Assert.Equal(37, Assert.Single(result.Blocks[1].Content).Text.Length);
        Assert.All(result.Blocks, block => Assert.True(Assert.Single(block.Content).Text.Length <= MemoryReaderLimits.MaximumBlockCharacters));
    }

    [Fact]
    public async Task ReadDocument_ForeignInactiveExpiredAndMissingRoutesAreGenericNotFound()
    {
        var foreign = Record("foreign", Scope with { RunId = new ScopeId("other-run") }, "foreign");
        var inactive = Record("inactive", Scope, "inactive") with { Status = MemoryLifecycleStatus.Invalid };
        var expired = Record("expired", Scope, "expired") with { ExpiresAt = Now };
        var source = new ReaderSource([foreign, inactive, expired]);
        source.AddRoute("route-foreign", foreign.MemoryId);
        source.AddRoute("route-inactive", inactive.MemoryId);
        source.AddRoute("route-expired", expired.MemoryId);
        source.AddRoute("route-missing", new("missing"));
        var service = Service(source, Allowed());

        foreach (var route in new[] { "route-foreign", "route-inactive", "route-expired", "route-missing" })
        {
            var result = await service.ReadDocumentAsync(new(Actor, Scope, route, null, null, ContractVersion), default);

            Assert.Equal(MemoryReaderDocumentState.NotFound, result.State);
            Assert.Equal(route, result.RouteKey);
            Assert.Empty(result.Blocks);
            Assert.Null(result.NextCursor);
        }
    }

    [Fact]
    public async Task ReadDocument_VersionMismatchedContinuationReturnsChanged()
    {
        var home = Record("home", Scope, Blocks(9)) with { Version = 2 };
        var source = new ReaderSource([home]);
        source.AddRoute("route-home", home.MemoryId);
        var service = Service(source, Allowed());

        var result = await service.ReadDocumentAsync(new(
            Actor, Scope, "route-home", null, new MemoryReaderBlockCursor(1, 8), ContractVersion), default);

        Assert.Equal(MemoryReaderDocumentState.Changed, result.State);
        Assert.Equal("route-home", result.RouteKey);
        Assert.Empty(result.Blocks);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task ReadHome_DeniedBeforeSourceInvocationReturnsGenericNotFound()
    {
        var home = Record("home", Scope, "# Root ^start\nbody");
        var source = new ReaderSource([home]);
        var authorization = new FixedAuthorization();
        authorization.Deny(Actor, MemoryOperation.ReaderRead);
        var service = Service(source, authorization);

        var result = await service.ReadHomeAsync(new(Actor, Scope, home.MemoryId, ContractVersion), default);

        Assert.Equal(MemoryReaderDocumentState.NotFound, result.State);
        Assert.Empty(result.Blocks);
        Assert.Equal(0, source.ReadCalls);
    }

    private static MemoryReaderQueryService Service(ReaderSource source, FixedAuthorization authorization) => new(
        source, authorization, new TestClock(Now), ContractVersion);

    private static FixedAuthorization Allowed()
    {
        var authorization = new FixedAuthorization();
        authorization.Allow(Actor, MemoryOperation.ReaderRead, Scope);
        return authorization;
    }

    private static MemoryReaderSourceRecord Record(string id, MemoryScope scope, string text) => new(
        new(id), scope, MemoryRecordType.Fact, MemoryLifecycleStatus.Active, text, Now, Now, 1, null, []);

    private static string Blocks(int count, string? firstBody = null) => string.Join('\n', Enumerable.Range(1, count)
        .Select(index => $"# Part {index} ^part-{index}\n{(index == 1 ? firstBody ?? $"Body {index}" : $"Body {index}")}"));

    private static string Encode(MemoryId id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(id.Value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class ReaderSource(IEnumerable<MemoryReaderSourceRecord> records) : IMemoryReaderSource
    {
        private readonly Dictionary<MemoryId, MemoryReaderSourceRecord> _records = records.ToDictionary(record => record.MemoryId);
        private readonly Dictionary<string, MemoryId> _routes = [];

        public int ReadCalls { get; private set; }
        public int RouteCalls { get; private set; }
        public List<MemorySearchEligibility> EligibilityRequests { get; } = [];

        public Task<MemoryReaderSourceRecord?> ReadByIdAsync(
            MemorySearchEligibility eligibility,
            MemoryId memoryId,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            EligibilityRequests.Add(eligibility);
            return Task.FromResult(_records.GetValueOrDefault(memoryId));
        }

        public Task<MemoryReaderRoute> GetOrCreateRouteAsync(MemoryId memoryId, CancellationToken cancellationToken)
        {
            RouteCalls++;
            var existing = _routes.SingleOrDefault(entry => entry.Value == memoryId);
            var routeKey = existing.Key ?? $"route-{memoryId.Value}";
            _routes[routeKey] = memoryId;
            return Task.FromResult(new MemoryReaderRoute(routeKey, memoryId));
        }

        public Task<MemoryId?> ResolveRouteAsync(string routeKey, CancellationToken cancellationToken) =>
            Task.FromResult(_routes.TryGetValue(routeKey, out var memoryId) ? (MemoryId?)memoryId : null);

        public void AddRoute(string routeKey, MemoryId memoryId) => _routes[routeKey] = memoryId;
    }
}
