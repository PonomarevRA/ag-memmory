using AgMemory.Contracts;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Claim provenance, temporal boundaries and contradictions are distinct from source citation eligibility.</summary>
public sealed class ClaimsTemporalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agmemory-claims-" + Guid.NewGuid().ToString("N"));
    private LocalAgMemoryMcpConfiguration Configuration => LocalAgMemoryMcpConfiguration.Create(_path, "claims-reader", "claims-tenant");

    /// <summary>Legacy content stays unknown; attributed inference is never silently promoted to a tool observation or factual verification.</summary>
    [Fact]
    public async Task LegacyUnknownAndClaimReplayRemainExplicit()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration);
        var source = await SourceAsync(runtime);
        var evidence = (await runtime.KnowledgeEvidenceAsync(source.CitationRef)).Data!;
        Assert.Equal("LegacyUnknown", evidence.SourceKind);
        Assert.Empty(evidence.Claims);
        var created = await runtime.KnowledgeClaimAsync(source.CitationRef, "application", "version", "candidate-version", "AgentInference", "claim_one");
        var replay = await runtime.KnowledgeClaimAsync(source.CitationRef, "application", "version", "candidate-version", "AgentInference", "claim_one");
        Assert.Null(created.Error); Assert.Null(replay.Error);
        Assert.Equal(created.Data!.RecordedAtUtc, replay.Data!.RecordedAtUtc);
        Assert.Equal("AgentInference", created.Data.SourceKind);
        Assert.True(created.Data.ValidityUnknown);
        Assert.Null(created.Data.ObservedAtUtc);
        Assert.Equal("caller_reported_unverified", created.Data.EvidenceTrust);
        Assert.Equal("request_conflict", (await runtime.KnowledgeClaimAsync(source.CitationRef, "application", "version", "different", "AgentInference", "claim_one")).Error!.Code);
        Assert.Single((await runtime.KnowledgeEvidenceAsync(source.CitationRef)).Data!.Claims);
    }

    /// <summary>Half-open validity, independent known-at time, contradictions and same-tuple supersession preserve historical answers.</summary>
    [Fact]
    public async Task TemporalIntervalsConflictsAndSupersessionAreNotLastWriteWins()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration);
        var source = await SourceAsync(runtime);
        var boundary = DateTimeOffset.UtcNow.AddDays(-1);
        var old = await runtime.KnowledgeClaimAsync(source.CitationRef, "application", "version", "1.6.6", "ToolObservation", "old_version",
            validFromUtc: boundary.AddDays(-5), validToUtc: boundary);
        var current = await runtime.KnowledgeClaimAsync(source.CitationRef, "application", "version", "1.6.7", "UserStatement", "new_version", validFromUtc: boundary);
        Assert.Null(old.Error); Assert.Null(current.Error);
        Assert.Equal("1.6.6", Assert.Single((await runtime.KnowledgeClaimsAsync("application", "version", boundary.AddTicks(-1))).Data!.Items).Value);
        Assert.Equal("1.6.7", Assert.Single((await runtime.KnowledgeClaimsAsync("application", "version", boundary)).Data!.Items).Value);
        Assert.Empty((await runtime.KnowledgeClaimsAsync("application", "version", boundary, boundary)).Data!.Items);
        var alternative = await runtime.KnowledgeClaimAsync(source.CitationRef, "application", "version", "1.6.8", "AgentInference", "alternative", validFromUtc: boundary);
        Assert.Null(alternative.Error);
        var conflict = (await runtime.KnowledgeClaimsAsync("application", "version", limit: 1)).Data!;
        Assert.True(conflict.HasConflict); Assert.True(Assert.Single(conflict.Items).HasConflict == true); Assert.Equal(1, conflict.OmittedClaimCount);
        Assert.All((await runtime.KnowledgeEvidenceAsync(source.CitationRef)).Data!.Claims, claim => Assert.Null(claim.HasConflict));
        var corrected = await runtime.KnowledgeClaimAsync(source.CitationRef, "application", "version", "1.6.7", "ToolObservation", "correct_alternative",
            validFromUtc: boundary, supersedesRef: alternative.Data!.ClaimRef);
        Assert.Null(corrected.Error);
        Assert.False((await runtime.KnowledgeClaimsAsync("application", "version")).Data!.HasConflict);
        Assert.Equal("1.6.6", Assert.Single((await runtime.KnowledgeClaimsAsync("application", "version", boundary.AddTicks(-1))).Data!.Items).Value);
        Assert.Equal("invalid_argument", (await runtime.KnowledgeClaimAsync(source.CitationRef, "other_subject", "version", "x", "UserStatement", "bad_supersession",
            supersedesRef: current.Data!.ClaimRef)).Error!.Code);
    }

    /// <summary>Stale sources disappear by default; explicit history is flagged without a current citation and cannot cross scope.</summary>
    [Fact]
    public async Task CorrectedSourcesCannotRetainFreshClaimCitations()
    {
        var config = Configuration;
        await using var runtime = new LocalAgMemoryMcpRuntime(config);
        var source = await SourceAsync(runtime);
        Assert.Null((await runtime.KnowledgeClaimAsync(source.CitationRef, "application", "version", "old", "UserStatement", "old")).Error);
        Assert.Null((await runtime.AgentCorrectAsync(source.CitationRef, source.Revision, "correct_source", "Corrected fixture version source", default)).Error);
        Assert.Empty((await runtime.KnowledgeClaimsAsync("application", "version")).Data!.Items);
        var history = (await runtime.KnowledgeClaimsAsync("application", "version", includeHistorical: true)).Data!;
        Assert.False(Assert.Single(history.Items).SourceCurrent);
        Assert.Null(history.Items[0].SourceCitationRef);
        await using var foreign = new LocalAgMemoryMcpRuntime(config with { Scope = config.Scope with { WorkspaceId = new("foreign") } });
        Assert.Empty((await foreign.KnowledgeClaimsAsync("application", "version", includeHistorical: true)).Data!.Items);
    }

    /// <summary>Closed kinds, UTC times and safe locators are checked before sidecar writes.</summary>
    [Fact]
    public async Task InvalidEvidenceCannotBecomeStructuredAuthority()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration);
        var source = await SourceAsync(runtime);
        foreach (var kind in new[] { "VerifiedSuccess", "0", "LegacyUnknown" })
            Assert.Equal("invalid_argument", (await runtime.KnowledgeClaimAsync(source.CitationRef, "app", "version", "x", kind, "invalid_" + kind)).Error!.Code);
        Assert.Equal("invalid_argument", (await runtime.KnowledgeClaimAsync(source.CitationRef, "app", "version", "x", "ToolObservation", "future", observedAtUtc: DateTimeOffset.UtcNow.AddDays(1))).Error!.Code);
        Assert.Equal("invalid_argument", (await runtime.KnowledgeClaimAsync(source.CitationRef, "app", "version", "x", "ToolObservation", "interval",
            validFromUtc: DateTimeOffset.UtcNow, validToUtc: DateTimeOffset.UtcNow.AddDays(-1))).Error!.Code);
        Assert.Equal("unsafe_content", (await runtime.KnowledgeClaimAsync(source.CitationRef, "app", "version", "x", "UserStatement", "path", locator: "/Users/example/file")).Error!.Code);
        Assert.Empty((await runtime.KnowledgeClaimsAsync("app", "version")).Data!.Items);
    }

    /// <summary>An unavailable superseder cannot suppress a still-current claim backed by another source.</summary>
    [Fact]
    public async Task StaleSupersederDoesNotHideCurrentPredecessor()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration);
        var predecessorSource = await SourceAsync(runtime);
        var predecessor = (await runtime.KnowledgeClaimAsync(predecessorSource.CitationRef, "app", "version", "retained", "UserStatement", "predecessor")).Data!;
        await runtime.RememberAsync("Independent replacement-only source", "Fact", .5, .8, null, default);
        var replacementSource = Assert.Single((await runtime.AgentContextAsync("replacement-only", 8192, default)).Data!.Items);
        Assert.Null((await runtime.KnowledgeClaimAsync(replacementSource.CitationRef, "app", "version", "replacement", "UserStatement", "replacement",
            supersedesRef: predecessor.ClaimRef)).Error);
        Assert.Equal("replacement", Assert.Single((await runtime.KnowledgeClaimsAsync("app", "version")).Data!.Items).Value);
        Assert.Null((await runtime.AgentCorrectAsync(replacementSource.CitationRef, replacementSource.Revision, "retract_replacement", null, default)).Error);
        Assert.Equal("retained", Assert.Single((await runtime.KnowledgeClaimsAsync("app", "version")).Data!.Items).Value);
    }

    private static async Task<AgentMemory> SourceAsync(LocalAgMemoryMcpRuntime runtime)
    {
        await runtime.RememberAsync("Fixture version source with observed package details", "Fact", .5, .8, null, default);
        return Assert.Single((await runtime.AgentContextAsync("fixture version source", 8192, default)).Data!.Items);
    }

    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
}
