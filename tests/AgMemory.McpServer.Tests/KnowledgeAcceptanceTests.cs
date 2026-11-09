using System.Text.Json;
using AgMemory.McpServer;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Contract acceptance for authority-local knowledge; fixtures never use the configured user store.</summary>
public sealed class KnowledgeAcceptanceTests : IDisposable
{
    private readonly List<string> _fixturePaths = [];

    /// <summary>Static discovery does not open knowledge state; optional reads count receipts without consuming one.</summary>
    [Fact]
    public async Task CapacityIsExplicitReadOnlyAndCountsRealUsage()
    {
        var config = Configuration();
        await using var runtime = new LocalAgMemoryMcpRuntime(config);
        Assert.Null((await runtime.KnowledgeCapabilitiesAsync()).Data!.Capacity);
        Assert.False(Directory.Exists(Path.Combine(config.StoragePath, ".agmemory-knowledge")));
        Assert.Null((await runtime.KnowledgeOccurrenceAsync("capacity_event", "fixture", "Observed failure", "test", "Open", "capacity_write")).Error);
        var first = await runtime.KnowledgeCapabilitiesAsync(true);
        var second = await runtime.KnowledgeCapabilitiesAsync(true);
        Assert.Null(first.Error);
        var requests = Assert.Single(first.Data!.Capacity!.Collections, item => item.Collection == "requests");
        Assert.Equal(1, requests.Used);
        Assert.Equal(4095, requests.Remaining);
        Assert.Equal(JsonSerializer.Serialize(first.Data.Capacity), JsonSerializer.Serialize(second.Data!.Capacity));
        Assert.Equal(1, Assert.Single(first.Data.Capacity.Collections, item => item.Collection == "occurrences").Used);
    }

    /// <summary>Operators opt in explicitly; typos cannot silently switch retrieval modes.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("lexical", false)]
    [InlineData(" BM25 ", true)]
    public void RankingConfigurationHasCompatibleDefault(string? value, bool expected)
    {
        Assert.Equal(expected, LocalAgMemoryMcpConfiguration.ParseRecallRanking(value));
        Assert.Throws<ArgumentException>(() => LocalAgMemoryMcpConfiguration.ParseRecallRanking("semantic"));
    }
    /// <summary>A retried occurrence is one event; a new event with the same symptom remains separately recorded.</summary>
    [Fact]
    public async Task OccurrenceReplayDiffersFromRecurrenceAndConflictingReplayIsRejected()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration());
        var first = await runtime.KnowledgeOccurrenceAsync("event_one", "logs_timeout", "Export timed out", "fixture", "Open", "write_one");
        var replay = await runtime.KnowledgeOccurrenceAsync("event_one", "logs_timeout", "Export timed out", "fixture", "Open", "write_one");
        var recurrence = await runtime.KnowledgeOccurrenceAsync("event_two", "logs_timeout", "Export timed out", "fixture", "Open", "write_two");
        Assert.Null(first.Error);
        Assert.Null(replay.Error);
        Assert.Contains("replay", replay.Data!.Outcome, StringComparison.OrdinalIgnoreCase);
        Assert.Null(recurrence.Error);
        Assert.NotEqual(first.Data!.Key, recurrence.Data!.Key);
        var conflict = await runtime.KnowledgeOccurrenceAsync("event_one", "logs_timeout", "Changed symptom", "fixture", "Open", "write_one");
        Assert.NotNull(conflict.Error);
    }

    /// <summary>Public procedure revisions survive reopen and stale writes cannot overwrite a newer revision.</summary>
    [Fact]
    public async Task ProcedurePersistsAndConcurrentRevisionUpdateHasOneWinner()
    {
        var configuration = Configuration();
        string reference;
        long revision;
        await using (var runtime = new LocalAgMemoryMcpRuntime(configuration))
        {
            var created = await runtime.KnowledgeProcedureAsync(null, 0, "Export logs", ["скачать логи"], "fixture", "Read access", "Export bounded interval", "Check archive interval", "docs/runbook.md", "create_procedure");
            Assert.Null(created.Error);
            var replay = await runtime.KnowledgeProcedureAsync(null, 0, "Export logs", ["скачать логи"], "fixture", "Read access", "Export bounded interval", "Check archive interval", "docs/runbook.md", "create_procedure");
            Assert.Null(replay.Error);
            Assert.Equal(created.Data!.Revision, replay.Data!.Revision);
            reference = created.Data.ProcedureRef;
            revision = created.Data.Revision;
        }
        await using var first = new LocalAgMemoryMcpRuntime(configuration);
        await using var second = new LocalAgMemoryMcpRuntime(configuration);
        var catalog = await first.KnowledgeProceduresAsync("скачать логи", "fixture");
        Assert.Null(catalog.Error);
        Assert.Single(catalog.Data!);
        var updates = await Task.WhenAll(
            first.KnowledgeProcedureAsync(reference, revision, "Export logs A", ["скачать логи"], "fixture", "Read access", "Export bounded interval", "Check archive interval", null, "update_a"),
            second.KnowledgeProcedureAsync(reference, revision, "Export logs B", ["скачать логи"], "fixture", "Read access", "Export bounded interval", "Check archive interval", null, "update_b"));
        Assert.Single(updates, item => item.Error is null);
        Assert.Single(updates, item => item.Error is not null);
        var originalReplay = await first.KnowledgeProcedureAsync(null, 0, "Export logs", ["скачать логи"], "fixture", "Read access", "Export bounded interval", "Check archive interval", "docs/runbook.md", "create_procedure");
        Assert.Null(originalReplay.Error);
        Assert.Equal(revision, originalReplay.Data!.Revision);
        Assert.Equal("Export logs", originalReplay.Data.Goal);
    }

    /// <summary>Caller evidence can report success but cannot claim trusted verification or borrow another profile's reference.</summary>
    [Fact]
    public async Task ApplicationRejectsVerifiedSuccessAndForeignProcedureReferences()
    {
        var configuration = Configuration();
        await using var runtime = new LocalAgMemoryMcpRuntime(configuration);
        var created = await runtime.KnowledgeProcedureAsync(null, 0, "Collect diagnostics", ["logs"], "fixture", "Read access", "Export logs", "Check artifact", null, "create");
        Assert.Null(created.Error);
        var reference = created.Data!.ProcedureRef;
        var rejected = await runtime.KnowledgeApplicationAsync("attempt_one", reference, "fixture", "VerifiedSuccess", "Caller says passed", "verified");
        Assert.NotNull(rejected.Error);
        var reported = await runtime.KnowledgeApplicationAsync("attempt_one", reference, "fixture", "ReportedSuccess", "Checked expected artifact", "reported");
        Assert.Null(reported.Error);
        var replay = await runtime.KnowledgeApplicationAsync("attempt_one", reference, "fixture", "ReportedSuccess", "Checked expected artifact", "reported");
        Assert.Null(replay.Error);
        Assert.Contains("replay", replay.Data!.Outcome, StringComparison.OrdinalIgnoreCase);
        await using var foreign = new LocalAgMemoryMcpRuntime(Configuration());
        Assert.NotNull((await foreign.KnowledgeApplicationAsync("attempt_two", reference, "fixture", "ReportedSuccess", "Checked expected artifact", "foreign")).Error);
    }

    /// <summary>Feedback needs a real presentation receipt; transport replay and caller labels cannot elevate its trust.</summary>
    [Fact]
    public async Task FeedbackRequiresReceiptAndRejectsStaleCanonicalCitation()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration());
        await runtime.RememberAsync("FeedbackFixture canonical claim", "Fact", .6, .7, null, default);
        var item = Assert.Single((await runtime.AgentContextAsync("FeedbackFixture", 2048, default)).Data!.Items);
        var presented = await runtime.KnowledgePresentAsync(item.CitationRef, "caller_task", "present");
        Assert.Null(presented.Error);
        var first = await runtime.KnowledgeFeedbackAsync(presented.Data!.ReceiptRef, "Helpful", 0, "helpful");
        Assert.Null(first.Error);
        var replay = await runtime.KnowledgeFeedbackAsync(presented.Data.ReceiptRef, "Helpful", 0, "helpful");
        Assert.Null(replay.Error);
        Assert.Contains("replay", replay.Data!.Outcome, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull((await runtime.KnowledgeFeedbackAsync("invented", "Helpful", 0, "fake")).Error);
        await runtime.RememberAsync("FeedbackFixture canonical claim", "Fact", .9, .9, null, default);
        Assert.NotNull((await runtime.KnowledgePresentAsync(item.CitationRef, "caller_task", "stale_present")).Error);
    }

    /// <summary>A scope-wide working set uses CAS without treating task text as participant authentication.</summary>
    [Fact]
    public async Task WorkingSetUsesCasAndDoesNotCrossProfiles()
    {
        var configuration = Configuration();
        await using var first = new LocalAgMemoryMcpRuntime(configuration);
        await using var second = new LocalAgMemoryMcpRuntime(configuration);
        var results = await Task.WhenAll(
            first.KnowledgeWorkingAsync("Goal A", ["A confirmed"], ["A assumption"], [], "Check A", 0, "working_a"),
            second.KnowledgeWorkingAsync("Goal B", ["B confirmed"], ["B assumption"], [], "Check B", 0, "working_b"));
        Assert.Single(results, item => item.Error is null);
        Assert.Single(results, item => item.Error is not null);
        Assert.NotNull((await first.WorkingContextAsync("", 2048)).Data!.WorkingSet);
        await using var foreign = new LocalAgMemoryMcpRuntime(Configuration());
        Assert.Null((await foreign.WorkingContextAsync("", 2048)).Data!.WorkingSet);
    }

    /// <summary>New responses expose opaque references while suppressing private configuration values and paths.</summary>
    [Fact]
    public async Task KnowledgeResponseDoesNotLeakConfiguredIdentity()
    {
        var configuration = Configuration();
        await using var runtime = new LocalAgMemoryMcpRuntime(configuration);
        var created = await runtime.KnowledgeProcedureAsync(null, 0, "Safe procedure", ["safe"], "fixture", "Read access", "Read fixture", "Inspect output", null, "safe");
        Assert.Null(created.Error);
        var encoded = JsonSerializer.Serialize(created);
        Assert.DoesNotContain(configuration.StoragePath, encoded);
        Assert.DoesNotContain(configuration.Actor.Value, encoded);
        Assert.DoesNotContain(configuration.Scope.TenantId.Value, encoded);
    }

    /// <summary>Metadata has its own CAS revision; classifying a source never changes canonical content or trust.</summary>
    [Fact]
    public async Task MetadataCasIsIndependentOfCanonicalRevision()
    {
        var configuration = Configuration();
        await using var first = new LocalAgMemoryMcpRuntime(configuration);
        await using var second = new LocalAgMemoryMcpRuntime(configuration);
        await first.RememberAsync("MetadataFixture confirmed statement", "Fact", .6, .7, null, default);
        var source = Assert.Single((await first.AgentContextAsync("MetadataFixture", 2048, default)).Data!.Items);
        var results = await Task.WhenAll(
            first.KnowledgeMetadataAsync(source.CitationRef, "Knowledge", "fixture", "metadata_a"),
            second.KnowledgeMetadataAsync(source.CitationRef, "Experience", "fixture", "metadata_b"));
        Assert.Single(results, item => item.Error is null);
        Assert.Single(results, item => item.Error?.Code == "revision_conflict");
        var unchanged = (await first.AgentResolveAsync(source.CitationRef, default)).Data!;
        Assert.Equal(source.Revision, unchanged.Revision);
        Assert.Equal(source.Content, unchanged.Content);
    }

    /// <summary>Two runtimes revalidate a warm source after correction instead of returning its cached obsolete revision.</summary>
    [Fact]
    public async Task WarmResidencyRejectsCorrectedRevisionAcrossRuntimes()
    {
        var configuration = Configuration();
        await using var first = new LocalAgMemoryMcpRuntime(configuration);
        await using var second = new LocalAgMemoryMcpRuntime(configuration);
        await first.RememberAsync("WarmFixture obsolete claim", "Fact", .6, .7, null, default);
        var source = Assert.Single((await first.AgentContextAsync("WarmFixture", 2048, default)).Data!.Items);
        Assert.Null((await first.KnowledgePresentAsync(source.CitationRef, "task", "warm_present")).Error);
        var warm = await second.WorkingContextAsync("WarmFixture", 2048);
        Assert.Equal("L3", Assert.Single(warm.Data!.Items).Level);
        var correction = await first.AgentCorrectAsync(source.CitationRef, source.Revision, "warm_correct", "WarmFixture corrected claim", default);
        Assert.Null(correction.Error);
        var refreshed = await second.WorkingContextAsync("WarmFixture", 2048);
        Assert.Null(refreshed.Error);
        Assert.DoesNotContain(refreshed.Data!.Items, item => item.Memory.Content == source.Content);
        Assert.All(refreshed.Data.Items, item => Assert.NotEqual("L3", item.Level));
    }

    /// <summary>Required working constraints must fail explicitly when the whole response exceeds the requested budget.</summary>
    [Fact]
    public async Task RequiredWorkingSetCannotBeSilentlyTruncated()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration());
        var facts = new string[8];
        for (var i = 0; i < facts.Length; i++) facts[i] = "Required constraint " + i + " " + new string('x', 850);
        Assert.Null((await runtime.KnowledgeWorkingAsync("BudgetFixture", facts, [], [], "Check constraints", 0, "budget_working")).Error);
        var response = await runtime.WorkingContextAsync("", 256);
        Assert.Equal("required_context_exceeds_budget", response.Error!.Code);
        var bounded = await runtime.WorkingContextAsync("", 8192);
        Assert.Null(bounded.Error);
        Assert.Equal(facts, bounded.Data!.WorkingSet!.Facts);
        var encoded = JsonSerializer.Serialize(bounded);
        Assert.Equal((encoded.Length + 3) / 4, bounded.Data.EstimatedTokenCost);
    }

    /// <summary>Citation and feedback receipt locators cannot be reused as procedure locators.</summary>
    [Fact]
    public async Task OpaqueReferencesArePurposeBound()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration());
        await runtime.RememberAsync("PurposeFixture claim", "Fact", .6, .7, null, default);
        var source = Assert.Single((await runtime.AgentContextAsync("PurposeFixture", 2048, default)).Data!.Items);
        var receipt = (await runtime.KnowledgePresentAsync(source.CitationRef, "task", "purpose_present")).Data!.ReceiptRef;
        Assert.Equal("reference_unavailable", (await runtime.KnowledgeApplicationAsync("attempt", source.CitationRef, "fixture", "ReportedSuccess", "Checked artifact", "wrong_citation")).Error!.Code);
        Assert.Equal("reference_unavailable", (await runtime.KnowledgeApplicationAsync("attempt", receipt, "fixture", "ReportedSuccess", "Checked artifact", "wrong_receipt")).Error!.Code);
        Assert.Equal("reference_unavailable", (await runtime.AgentResolveAsync(receipt, default)).Error!.Code);
    }

    /// <summary>Occurrence status uses independent CAS and a procedure application cannot close the incident implicitly.</summary>
    [Fact]
    public async Task OccurrenceStatusCasAndApplicationLinkRemainIndependent()
    {
        await using var runtime = new LocalAgMemoryMcpRuntime(Configuration());
        var occurrence = await runtime.KnowledgeOccurrenceAsync("outage", "export_family", "Service unavailable", "fixture", "Open", "incident_create");
        Assert.Null(occurrence.Error);
        var procedure = await runtime.KnowledgeProcedureAsync(null, 0, "Download logs", ["logs"], "fixture", "Read access", "Export interval", "Check artifact", null, "logs_procedure");
        Assert.Null(procedure.Error);
        var missing = await runtime.KnowledgeApplicationAsync("logs_attempt", procedure.Data!.ProcedureRef, "fixture", "ReportedSuccess", "Archive checked", "bad_link", "missing");
        Assert.Equal("reference_unavailable", missing.Error!.Code);
        var application = await runtime.KnowledgeApplicationAsync("logs_attempt", procedure.Data.ProcedureRef, "fixture", "ReportedSuccess", "Archive checked", "good_link", "outage");
        Assert.Null(application.Error);
        // If successful diagnostics had changed the outage revision, this expected revision would conflict.
        var mitigated = await runtime.KnowledgeOccurrenceUpdateAsync("outage", "Mitigated", occurrence.Data!.Revision, "mitigate");
        Assert.Null(mitigated.Error);
        var conflict = await runtime.KnowledgeOccurrenceUpdateAsync("outage", "Resolved", occurrence.Data.Revision, "stale_resolve");
        Assert.Equal("revision_conflict", conflict.Error!.Code);
        var replay = await runtime.KnowledgeOccurrenceUpdateAsync("outage", "Mitigated", occurrence.Data.Revision, "mitigate");
        Assert.Null(replay.Error);
        Assert.Equal(mitigated.Data!.Revision, replay.Data!.Revision);
    }

    /// <summary>Different actors share opted-in team procedures and L2 while retaining private citation and procedure locators.</summary>
    [Fact]
    public async Task LocalTeamSharesKnowledgeButNotActorReferences()
    {
        var config = Configuration() with { KnowledgeTeamId = "local-team" };
        await using var codex = new LocalAgMemoryMcpRuntime(config);
        await using var cursor = new LocalAgMemoryMcpRuntime(config with { Actor = new("cursor") });
        var first = (await codex.KnowledgeCapabilitiesAsync()).Data!;
        var second = (await cursor.KnowledgeCapabilitiesAsync()).Data!;
        Assert.NotEqual(first.ProfileRef, second.ProfileRef);
        Assert.Equal(first.KnowledgeProfileRef, second.KnowledgeProfileRef);
        Assert.Equal("local_team", second.KnowledgeSharing);
        var created = await codex.KnowledgeProcedureAsync(null, 0, "Team logs", ["logs"], "fixture", "Read access", "Export interval", "Check archive", null, "codex_create");
        Assert.Null(created.Error);
        var found = Assert.Single((await cursor.KnowledgeProceduresAsync("Team logs", "fixture")).Data!);
        Assert.Equal(created.Data!.Goal, found.Goal);
        Assert.NotEqual(created.Data.ProcedureRef, found.ProcedureRef);
        var borrowed = await cursor.KnowledgeApplicationAsync("borrowed", created.Data.ProcedureRef, "fixture", "ReportedSuccess", "Checked", "cursor_borrowed");
        Assert.Equal("reference_unavailable", borrowed.Error!.Code);
        Assert.Null((await cursor.KnowledgeApplicationAsync("own", found.ProcedureRef, "fixture", "ReportedSuccess", "Checked", "cursor_own")).Error);
        var working = await codex.KnowledgeWorkingAsync("Team context", [], [], [], "Inspect result", 0, "codex_working");
        Assert.Null(working.Error);
        Assert.Equal("Team context", (await cursor.WorkingContextAsync("")).Data!.WorkingSet!.Goal);
        await codex.RememberAsync("SharedTeamFixture canonical", "Fact", .6, .8, null, default);
        var citation = Assert.Single((await codex.AgentContextAsync("SharedTeamFixture", 2048, default)).Data!.Items).CitationRef;
        var presented = await codex.KnowledgePresentAsync(citation, "codex_task", "codex_present");
        Assert.Null(presented.Error);
        Assert.Null((await codex.KnowledgeFeedbackAsync(presented.Data!.ReceiptRef, "Helpful", 0, "codex_helpful")).Error);
        var warm = await cursor.WorkingContextAsync("SharedTeamFixture");
        Assert.Single(warm.Data!.Items);
        Assert.Equal("L3", warm.Data.Items[0].Level);
        Assert.Equal("reference_unavailable", (await cursor.KnowledgeFeedbackAsync(presented.Data.ReceiptRef, "Harmful", 0, "cursor_borrowed_receipt")).Error!.Code);
        Assert.Equal("reference_unavailable", (await cursor.AgentResolveAsync(citation, default)).Error!.Code);
        Assert.Single((await cursor.AgentContextAsync("SharedTeamFixture", 2048, default)).Data!.Items);
        await using var foreign = new LocalAgMemoryMcpRuntime(config with { KnowledgeTeamId = "other-team" });
        Assert.Empty((await foreign.KnowledgeProceduresAsync("Team logs", "fixture")).Data!);
        await using var isolated = new LocalAgMemoryMcpRuntime(config with { KnowledgeTeamId = null });
        Assert.Empty((await isolated.KnowledgeProceduresAsync("Team logs", "fixture")).Data!);
    }

    /// <summary>Allocates an independent temporary authority for one acceptance fixture.</summary>
    private LocalAgMemoryMcpConfiguration Configuration()
    {
        var path = Path.Combine(Path.GetTempPath(), "agmemory-knowledge-qa-" + Guid.NewGuid().ToString("N"));
        _fixturePaths.Add(path);
        return LocalAgMemoryMcpConfiguration.Create(path, "qa-private-actor", "qa-private-tenant", "qa-project");
    }

    /// <summary>Removes fixture stores after async runtime disposal has released their storage handles.</summary>
    public void Dispose()
    {
        foreach (var path in _fixturePaths)
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
