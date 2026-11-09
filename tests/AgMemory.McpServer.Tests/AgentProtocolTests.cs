using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.McpServer;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.McpServer.Tests;

public sealed class AgentProtocolTests
{
    [Fact]
    public async Task ContextBoundsWholeEnvelopeAndResolveSurvivesRestartWithoutLeakingIdentity()
    {
        var configuration = Configuration();
        AgentMemory item;
        string profile;
        await using (var runtime = new LocalAgMemoryMcpRuntime(configuration))
        {
            await runtime.RememberAsync("Orion reusable retrieval evidence.", "Fact", .6, .7, null, default);
            var response = await runtime.AgentContextAsync("Orion", 1024, default);
            Assert.Null(response.Error);
            item = Assert.Single(response.Data!.Items);
            var encoded = JsonSerializer.Serialize(response);
            Assert.Equal((encoded.Length + 3) / 4, response.Data.EstimatedTokenCost);
            Assert.True(response.Data.EstimatedTokenCost <= 1024);
            Assert.DoesNotContain(configuration.StoragePath, encoded);
            Assert.DoesNotContain(configuration.Actor.Value, encoded);
            Assert.DoesNotContain(configuration.Scope.TenantId.Value, encoded);
            profile = (await runtime.AgentCapabilitiesAsync()).Data!.ProfileRef;
        }
        await using var restarted = new LocalAgMemoryMcpRuntime(configuration);
        var resolved = await restarted.AgentResolveAsync(item.CitationRef, default);
        Assert.Equal(item.Content, resolved.Data!.Content);
        Assert.Equal(profile, (await restarted.AgentCapabilitiesAsync()).Data!.ProfileRef);
        await using var store = new LanceDbMemoryStore(new(configuration.StoragePath));
        var record = Assert.Single(await store.ListAsync(Scopes(configuration), default));
        Assert.DoesNotContain(record.Id.Value, JsonSerializer.Serialize(resolved));
        Assert.DoesNotContain(record.Provenance.SourceSystem, JsonSerializer.Serialize(resolved));
    }

    [Fact]
    public async Task BudgetOmitsOversizedCandidatesAndReportsOnlyCandidateOmissions()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration());
        await runtime.RememberAsync("BudgetMarker " + new string('x', 5000), "Fact", .6, .7, null, default);
        var result = await runtime.AgentContextAsync("BudgetMarker", 256, default);
        Assert.Null(result.Error);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(1, result.Data.OmittedCandidateCount);
        Assert.True(result.Data.EstimatedTokenCost <= 256);
        Assert.Equal("invalid_argument", (await runtime.AgentContextAsync("BudgetMarker", 0, default)).Error!.Code);
    }

    [Fact]
    public async Task MalformedForeignExpiredStaleAndInactiveReferencesHaveIdenticalErrors()
    {
        var configuration = Configuration();
        await using var runtime = new LocalAgMemoryMcpRuntime(configuration);
        await runtime.RememberAsync("ReferenceMarker original fact", "Fact", .6, .7, null, default);
        var item = Assert.Single((await runtime.AgentContextAsync("ReferenceMarker", 2048, default)).Data!.Items);
        var malformed = await runtime.AgentResolveAsync("invalid", default);
        await using var foreign = new LocalAgMemoryMcpRuntime(configuration with { Scope = configuration.Scope with { ProjectId = new ScopeId("foreign") } });
        Assert.Equal(malformed.Error, (await foreign.AgentResolveAsync(item.CitationRef, default)).Error);
        var protector = new AgentReferenceProtector(configuration);
        var expired = protector.Issue("missing", 1, null, DateTimeOffset.UtcNow.AddDays(-2));
        Assert.Equal(malformed.Error, (await runtime.AgentResolveAsync(expired, default)).Error);
        await runtime.RememberAsync("ReferenceMarker original fact", "Fact", .9, .9, null, default);
        Assert.Equal(malformed.Error, (await runtime.AgentResolveAsync(item.CitationRef, default)).Error);
        var latest = Assert.Single((await runtime.AgentContextAsync("ReferenceMarker", 2048, default)).Data!.Items);
        var retracted = await runtime.AgentCorrectAsync(latest.CitationRef, latest.Revision, "retract_original", null, default);
        Assert.Null(retracted.Error);
        Assert.Equal("Replay", (await runtime.AgentCorrectAsync(latest.CitationRef, latest.Revision, "retract_original", null, default)).Data!.Outcome);
        Assert.Equal(malformed.Error, (await runtime.AgentResolveAsync(latest.CitationRef, default)).Error);
        Assert.Empty((await runtime.AgentContextAsync("ReferenceMarker", 2048, default)).Data!.Items);
    }

    [Fact]
    public async Task CorrectionCompetingRuntimesPermitOneWinnerAndExactReplayAfterRevisionChanges()
    {
        var configuration = Configuration();
        await using var first = new LocalAgMemoryMcpRuntime(configuration);
        await using var second = new LocalAgMemoryMcpRuntime(configuration);
        await first.RememberAsync("CorrectionMarker obsolete setting", "Fact", .6, .7, null, default);
        var item = Assert.Single((await first.AgentContextAsync("CorrectionMarker", 2048, default)).Data!.Items);
        var results = await Task.WhenAll(
            first.AgentCorrectAsync(item.CitationRef, item.Revision, "correction_a", "CorrectionMarker setting A", default),
            second.AgentCorrectAsync(item.CitationRef, item.Revision, "correction_b", "CorrectionMarker setting B", default));
        var winnerIndex = Array.FindIndex(results, result => result.Error is null);
        Assert.Single(results, result => result.Error is null);
        Assert.Single(results, result => result.Error?.Code == "reference_unavailable");
        var key = winnerIndex == 0 ? "correction_a" : "correction_b";
        var content = winnerIndex == 0 ? "CorrectionMarker setting A" : "CorrectionMarker setting B";
        var replay = await first.AgentCorrectAsync(item.CitationRef, item.Revision, key, content, default);
        Assert.Null(replay.Error);
        Assert.Equal("Replay", replay.Data!.Outcome);
        var conflict = await first.AgentCorrectAsync(item.CitationRef, item.Revision, key, "Different correction", default);
        Assert.Equal("request_conflict", conflict.Error!.Code);
        var remaining = Assert.Single((await first.AgentContextAsync("CorrectionMarker", 2048, default)).Data!.Items);
        Assert.Equal(content, remaining.Content);
    }

    [Fact]
    public async Task StructuredHandoffAndDecisionValidateTrustExpiryAndReplay()
    {
        var configuration = Configuration();
        await using var runtime = new LocalAgMemoryMcpRuntime(configuration);
        var handoff = new AgentHandoff(["HandoffMarker tests passed"], ["Needs deployment verification"], [], "Inspect deployment",
            [new("unit-test", "Caller reports one successful test")]);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O");
        var written = await runtime.AgentHandoffAsync(handoff, "handoff_one", expiry, default);
        Assert.Null(written.Error);
        Assert.Contains("caller_supplied_unverified", written.Data!.Memory!.Content);
        Assert.Contains("IdempotencyReplay", (await runtime.AgentHandoffAsync(handoff, "handoff_one", expiry, default)).Data!.Outcome);
        var decision = new AgentDecision("DecisionMarker choose protocol", "Local integration", ["v1", "v2"], "v2", "Bounded context", ["Keep v1 compatibility"]);
        var recorded = await runtime.AgentDecisionAsync(decision, "decision_one", expiry, default);
        Assert.Null(recorded.Error);
        Assert.Equal("Decision", recorded.Data!.Memory!.Type);
        Assert.NotNull((await runtime.AgentDecisionAsync(decision with { Options = [] }, "bad_decision", expiry, default)).Error);
        Assert.NotNull((await runtime.AgentHandoffAsync(handoff, "bad_expiry", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"), default)).Error);
        await using var store = new LanceDbMemoryStore(new(configuration.StoragePath));
        var records = await store.ListAsync(Scopes(configuration), default);
        Assert.All(records, record => Assert.NotNull(record.ExpiresAt));
        var task = records.Single(record => record.Type == MemoryRecordType.Task);
        await using (var transaction = await store.BeginTransactionAsync(default))
        {
            await transaction.WriteRecordAsync(Scopes(configuration), task with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, task.Version, default);
            await transaction.CommitAsync(default);
        }
        Assert.Equal("reference_unavailable", (await runtime.AgentResolveAsync(written.Data.Memory.CitationRef, default)).Error!.Code);
        Assert.Empty((await runtime.AgentContextAsync("HandoffMarker", 2048, default)).Data!.Items);
    }

    [Theory]
    [InlineData("/Users/example/source.cs")]
    [InlineData("/private/tmp/source.cs")]
    [InlineData("api_key=supersecret")]
    [InlineData("Bearer abcdefgh")]
    public async Task RejectsUnsafeCallerEvidenceWithoutWriting(string claim)
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration());
        var response = await runtime.AgentHandoffAsync(new([], [], [], "Read evidence", [new("source", claim)]), "unsafe", null, default);
        Assert.Equal("unsafe_content", response.Error!.Code);
        Assert.Equal(0, (await runtime.GetStatusAsync(default)).ActiveMemoryCount);
    }

    [Fact]
    public async Task KeyPermissionsAndSymlinksFailClosedWithoutExposingPaths()
    {
        if (OperatingSystem.IsWindows()) return;
        var configuration = Configuration();
        Directory.CreateDirectory(configuration.StoragePath);
        var target = Path.Combine(configuration.StoragePath, "target");
        File.WriteAllBytes(target, new byte[32]);
        var key = Path.Combine(configuration.StoragePath, ".agmemory-agent-key");
        File.CreateSymbolicLink(key, target);
        await using var runtime = new LocalAgMemoryMcpRuntime(configuration);
        var response = await runtime.AgentCapabilitiesAsync();
        Assert.Equal("dependency_unavailable", response.Error!.Code);
        Assert.DoesNotContain(configuration.StoragePath, JsonSerializer.Serialize(response));
    }


    [Fact]
    public async Task ContextTypeFiltersNarrowMixedTypesAndRejectNumericOrUnknownNames()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration());
        await runtime.RememberAsync("MixedMarker fact record", "Fact", .6, .7, null, default);
        await runtime.RememberAsync("MixedMarker task record", "Task", .6, .7, null, default);
        Assert.Equal(2, (await runtime.AgentContextAsync("MixedMarker", 2048, default)).Data!.Items.Count);
        var filtered = await runtime.AgentContextAsync("MixedMarker", 2048, default, ["Task"]);
        Assert.Equal("Task", Assert.Single(filtered.Data!.Items).Type);
        Assert.Empty((await runtime.AgentContextAsync("MixedMarker", 2048, default, [])).Data!.Items);
        Assert.Equal("invalid_argument", (await runtime.AgentContextAsync("MixedMarker", 2048, default, ["0"])).Error!.Code);
        Assert.Equal("invalid_argument", (await runtime.AgentContextAsync("MixedMarker", 2048, default, ["Unknown"])).Error!.Code);
    }

    [Fact]
    public void CandidateTypeMutationBetweenScanAndProjectionCannotWidenRequestedTypes()
    {
        var scope = new MemoryScope(new("tenant"));
        var now = DateTimeOffset.UtcNow;
        var eligibility = new MemorySearchEligibility(new([new ScopeSelector(scope)]), new HashSet<MemoryRecordType> { MemoryRecordType.Fact }, now);
        var scanned = new MemoryLexicalRecallItem(new("id"), MemoryRecordType.Fact, "same content", .5, .5, now, 1);
        var current = new MemoryRecord(new("id"), scope, MemoryRecordType.Task, MemoryLifecycleStatus.Active, "same content", null,
            .5, .5, 3, now, now, 2, [], new("test", null, null, null, null, null, null, []), null, null, "dedup");
        Assert.False(LocalAgMemoryMcpRuntime.IsCurrentAgentCandidate(eligibility, current, scanned));
        Assert.True(LocalAgMemoryMcpRuntime.IsCurrentAgentCandidate(eligibility, current with { Type = MemoryRecordType.Fact }, scanned));
    }

    private static LocalAgMemoryMcpConfiguration Configuration() => LocalAgMemoryMcpConfiguration.Create(
        Path.Combine(Path.GetTempPath(), "agmemory-agent-" + Guid.NewGuid().ToString("N")), "private-actor", "private-tenant", "project");
    private static AuthorizedScopeSet Scopes(LocalAgMemoryMcpConfiguration configuration) => new([new ScopeSelector(configuration.Scope)]);
}
