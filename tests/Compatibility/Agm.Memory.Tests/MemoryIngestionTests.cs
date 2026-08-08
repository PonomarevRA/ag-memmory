using Agm.Memory.Abstractions;
using Xunit;

namespace Agm.Memory.Tests;

public sealed class MemoryIngestionTests
{
    [Fact]
    public async Task ManualIngestion_CreatesCanonicalMemoryWithDirectProvenanceAndAudit()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var request = Request(
            "ingest-1",
            Candidate("PostgreSQL with pgvector is selected.", confidence: 0.93));

        var result = await service.IngestAsync(request, CancellationToken.None);
        var memory = Assert.Single(result.Items).Memory!;
        var provenance = await service.GetProvenanceAsync(
            request.Scope, memory.Id, CancellationToken.None);
        var fragments = await service.GetSourceFragmentsAsync(
            request.Scope, memory.Id, CancellationToken.None);
        var history = await service.GetHistoryAsync(
            request.Scope, memory.Id, CancellationToken.None);
        var audit = await service.GetAuditAsync(request.Scope, CancellationToken.None);

        Assert.Equal(CanonicalMemoryStatus.Active, memory.Status);
        Assert.Equal(MemoryIngestionAction.Created, result.Items[0].Action);
        Assert.False(result.Items[0].RequiresReview);
        Assert.Equal(MemoryEvidenceRole.Direct, Assert.Single(provenance).Role);
        Assert.Equal("source evidence", Assert.Single(fragments).Content);
        Assert.Equal(1, Assert.Single(history).Version);
        Assert.Equal("create", Assert.Single(audit).Action);
    }

    [Fact]
    public async Task DuplicateDelivery_IsIdempotentAndShapeConflictIsRejected()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var request = Request("same-key", Candidate("Stable decision", 0.9));

        var first = await service.IngestAsync(request, CancellationToken.None);
        var replay = await service.IngestAsync(request, CancellationToken.None);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IngestAsync(
                request with { Candidates = [Candidate("Different decision", 0.9)] },
                CancellationToken.None));

        Assert.False(first.IdempotencyHit);
        Assert.True(replay.IdempotencyHit);
        Assert.Equal(first.SourceId, replay.SourceId);
        Assert.Equal("MemoryIngestionIdempotencyConflict", conflict.Message);
        Assert.Single(await service.ListAsync(request.Scope, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateCanonicalStatement_ReinforcesInsteadOfCreatingMemory()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var first = Request("first", Candidate("Use hybrid retrieval", 0.72));
        var second = Request("second", Candidate("  Use   hybrid retrieval  ", 0.95)) with
        {
            Source = new(MemorySourceType.Document, "doc-2")
        };

        await service.IngestAsync(first, CancellationToken.None);
        var result = await service.IngestAsync(second, CancellationToken.None);
        var memories = await service.ListAsync(first.Scope, CancellationToken.None);

        Assert.Equal(MemoryIngestionAction.Reinforced, Assert.Single(result.Items).Action);
        Assert.Equal(0.95, result.Items[0].Memory?.Confidence);
        Assert.Single(memories);
        Assert.Equal(2, (await service.GetProvenanceAsync(
            first.Scope, memories[0].Id, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SameSourceEventWithDifferentRequestKey_ReusesSourceAndFragment()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var first = Request("delivery-a", Candidate("One decision", 0.9));
        var second = first with { IdempotencyKey = "delivery-b" };

        var initial = await service.IngestAsync(first, CancellationToken.None);
        var replay = await service.IngestAsync(second, CancellationToken.None);

        Assert.Equal(initial.SourceId, replay.SourceId);
        Assert.Equal(initial.SourceFragmentIds, replay.SourceFragmentIds);
        Assert.Single(await service.ListAsync(first.Scope, CancellationToken.None));
    }

    [Fact]
    public async Task LowConfidenceOrImportantMemory_RequiresReviewAndCannotLeakAcrossTenant()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var request = Request(
            "draft", Candidate("Unverified claim", confidence: 0.42, importance: 0.95));
        var result = await service.IngestAsync(request, CancellationToken.None);
        var memory = Assert.Single(result.Items).Memory!;
        var foreignScope = new MemoryScope(Guid.NewGuid(), request.Scope.ProjectId);

        Assert.Equal(CanonicalMemoryStatus.Draft, memory.Status);
        Assert.True(result.Items[0].RequiresReview);
        Assert.Null(await service.GetAsync(foreignScope, memory.Id, CancellationToken.None));
        Assert.Empty(await service.ListAsync(foreignScope, CancellationToken.None));
    }

    [Fact]
    public async Task QuestionOrHypothesis_CannotBecomeActiveFactEvenWithHighConfidence()
    {
        var service = new MemoryIngestionService(TimeProvider.System);
        var question = Candidate("Should PostgreSQL be selected?", 0.99) with
        {
            AssertionKind = MemoryAssertionKind.Question
        };

        var result = await service.IngestAsync(
            Request("question", question), CancellationToken.None);

        Assert.Equal(CanonicalMemoryStatus.Draft, Assert.Single(result.Items).Memory?.Status);
        Assert.True(result.Items[0].RequiresReview);
    }

    [Fact]
    public async Task ExtractMode_UsesVersionedExtractorAndRejectsMissingProvenance()
    {
        var extracted = Candidate("Extracted decision", 0.88);
        var service = new MemoryIngestionService(TimeProvider.System, new StubExtractor(extracted));
        var request = Request("extract", extracted) with
        {
            Mode = MemoryIngestionMode.Extract,
            Candidates = null,
            Extraction = new("extractor-v2", "model-x", "prompt-v3")
        };

        var result = await service.IngestAsync(request, CancellationToken.None);
        var invalid = request with
        {
            IdempotencyKey = "invalid",
            Mode = MemoryIngestionMode.Manual,
            Extraction = null,
            Candidates = [extracted with { SourceFragmentIndexes = [] }]
        };

        Assert.Equal(MemoryIngestionAction.Created, Assert.Single(result.Items).Action);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.IngestAsync(invalid, CancellationToken.None));
    }

    private static MemoryIngestRequest Request(
        string idempotencyKey, CanonicalMemoryCandidate candidate) => new(
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222")),
        new(MemorySourceType.Conversation, "chat-42"),
        [new("source evidence", "message-1", "user")],
        MemoryIngestionMode.Manual,
        idempotencyKey,
        "test-user",
        Guid.NewGuid(),
        [candidate]);

    private static CanonicalMemoryCandidate Candidate(
        string statement, double confidence, double importance = 0.5) => new(
        CanonicalMemoryType.Decision,
        "storage",
        statement,
        "measured requirements",
        confidence,
        importance,
        ["memory", "storage"],
        [0]);

    private sealed class StubExtractor(CanonicalMemoryCandidate candidate) : IMemoryCandidateExtractor
    {
        public Task<IReadOnlyList<CanonicalMemoryCandidate>> ExtractAsync(
            MemoryScope scope,
            IReadOnlyList<MemorySourceFragmentInput> fragments,
            MemoryExtractionMetadata metadata,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CanonicalMemoryCandidate>>([candidate]);
    }
}
