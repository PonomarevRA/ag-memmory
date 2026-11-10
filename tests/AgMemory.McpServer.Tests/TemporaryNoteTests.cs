using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.McpServer.Tests;

/// <summary>Exercises temporary notes against isolated, real annotation documents.</summary>
public sealed class TemporaryNoteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agmemory-notes-" + Guid.NewGuid().ToString("N"));

    /// <summary>Replay preserves the original expiry, receipt reads are atomic, and expired data is scrubbed on mutation.</summary>
    [Fact]
    public async Task ExpiryAndConcurrentDeliveryHistoryAreDeterministic()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));
        var store = KnowledgeAnnotationStore.ForTemporaryNotes(_path);
        var service = new TemporaryNoteService(store, "notes-fixture", clock);
        var author = new TemporaryNoteParticipant("writer", "codex", "chat-a", "caller_reported", null);
        var reader = new TemporaryNoteParticipant("reader", "cursor", "chat-b", "caller_reported", null);
        var other = reader with { ConversationId = "chat-c" };

        var created = await service.CreateAsync("Kafka handoff", "Offset inspection only", author, "save", default);
        Assert.Equal(clock.GetUtcNow().AddHours(48), created.ExpiresAtUtc);
        clock.Advance(TimeSpan.FromHours(1));
        var replay = await service.CreateAsync("Kafka handoff", "Offset inspection only", author, "save", default);
        Assert.Equal(created.NoteId, replay.NoteId);
        Assert.Equal(created.ExpiresAtUtc, replay.ExpiresAtUtc);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync("Kafka handoff", "Changed", author, "save", default));
        Assert.Equal("request_conflict", conflict.Message);
        Assert.Empty(Assert.Single(await service.ListAsync("Kafka", 5, default)).Deliveries);

        await Task.WhenAll(service.ReadAsync(created.NoteId, reader, default),
            service.ReadAsync(created.NoteId, other, default), service.ReadAsync(created.NoteId, reader, default));
        var history = Assert.Single(await service.ListAsync("kafka", 5, default)).Deliveries;
        Assert.Equal(2, history.Length);
        Assert.Equal(2, Assert.Single(history, item => item.Recipient == reader).Count);
        Assert.Equal(1, Assert.Single(history, item => item.Recipient == other).Count);
        Assert.Equal(clock.GetUtcNow(), history[0].FirstIssuedAtUtc);

        clock.Advance(TimeSpan.FromHours(47));
        Assert.Empty(await service.ListAsync("", 5, default));
        Assert.Equal("source_unavailable", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReadAsync(created.NoteId, reader, default))).Message);
        Assert.Equal("source_unavailable", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync("Kafka handoff", "Offset inspection only", author, "save", default))).Message);
        await service.CreateAsync("New handoff", "Next check", author, "new", default);
        var state = await store.ReadAsync("notes-fixture", default);
        Assert.Null((await new KnowledgeAnnotationStore(_path).ReadAsync("notes-fixture", default)).TemporaryNotes);
        var expired = Assert.Single(state.TemporaryNotes!.Values, note => note.Id == created.NoteId);
        Assert.Null(expired.Content);
        Assert.Null(expired.Author);
        Assert.Empty(expired.Deliveries);
        Assert.Empty(expired.Fingerprint);
    }

    /// <summary>Abandoned note scopes release capacity without allowing an old writer to overwrite a new generation.</summary>
    [Fact]
    public async Task ExpiredScopeReclamationRejectsStaleGeneration()
    {
        var store = KnowledgeAnnotationStore.ForTemporaryNotes(_path);
        var oldClock = new TestClock(DateTimeOffset.UtcNow.AddDays(-5));
        var author = new TemporaryNoteParticipant("writer", "codex", null, "unknown", null);
        var states = new Dictionary<string, KnowledgeAnnotationState>();
        for (var index = 0; index < 128; index++)
        {
            var key = $"old-{index}";
            await new TemporaryNoteService(store, key, oldClock).CreateAsync("Old", "Expired body", author, "seed", default);
            states.Add(key, await store.ReadAsync(key, default));
        }

        var clock = new TestClock(DateTimeOffset.UtcNow);
        await new TemporaryNoteService(store, "new-scope", clock).CreateAsync("New", "Live body", author, "new", default);
        string? reclaimed = null;
        foreach (var key in states.Keys)
        {
            if ((await store.ReadAsync(key, default)).Version != 0) continue;
            reclaimed = key;
            break;
        }
        Assert.NotNull(reclaimed);
        var current = new TemporaryNoteService(store, reclaimed!, clock);
        await current.CreateAsync("Replacement", "Fresh body", author, "replacement", default);
        var stale = states[reclaimed!];
        Assert.False(await store.CompareExchangeAsync(reclaimed!, stale.Version, stale with { Version = stale.Version + 1 }, default));
        Assert.Equal("Replacement", Assert.Single(await current.ListAsync("", 5, default)).Title);
    }

    /// <summary>Distinct agents can read in one exact scope, but attribution never grants access to another scope.</summary>
    [Fact]
    public async Task NoteHistoryUsesConfiguredActorsAndDoesNotCrossProjectOrTenant()
    {
        var config = LocalAgMemoryMcpConfiguration.Create(_path, "writer", "tenant", "project");
        await using var writer = new LocalAgMemoryMcpRuntime(config);
        await using var reader = new LocalAgMemoryMcpRuntime(config with { Actor = new("reader"), OriginClient = "cursor" });
        await using var otherProject = new LocalAgMemoryMcpRuntime(config with { Scope = config.Scope with { ProjectId = new("other") } });
        await using var otherTenant = new LocalAgMemoryMcpRuntime(config with { Scope = config.Scope with { TenantId = new("other") } });
        await using var configuredChat = new LocalAgMemoryMcpRuntime(config with { Scope = config.Scope with { ChatId = new("configured-chat") } });
        var created = (await writer.TemporaryNoteCreateAsync("Short note", "Fixture content", "note_key", "writer-chat", default)).Data!;
        Assert.Equal("writer", created.Author.Actor);
        Assert.Equal("caller_reported", created.Author.ConversationAttribution);
        Assert.Empty((await otherProject.TemporaryNotesAsync("", 5, default)).Data!);
        Assert.Equal("reference_unavailable", (await otherTenant.TemporaryNoteReadAsync(created.NoteId, "writer-chat", default)).Error!.Code);
        Assert.Equal("invalid_argument", (await configuredChat.TemporaryNoteCreateAsync("Short", "Text", "wrong_chat", "fake-chat", default)).Error!.Code);
        var trusted = (await configuredChat.TemporaryNoteCreateAsync("Short", "Text", "right_chat", null, default)).Data!;
        Assert.Equal("configured-chat", trusted.Author.ConversationId);
        Assert.Equal("configured", trusted.Author.ConversationAttribution);

        var read = (await reader.TemporaryNoteReadAsync(created.NoteId, "reader-chat", default)).Data!;
        Assert.Equal("Fixture content", read.Content);
        var history = Assert.Single((await writer.TemporaryNotesAsync("", 5, default)).Data!).Deliveries;
        var recipient = Assert.Single(history).Recipient;
        Assert.Equal(("reader", "cursor", "reader-chat"), (recipient.Actor, recipient.Client, recipient.ConversationId));
    }

    /// <summary>Removes only the isolated store created by the test.</summary>
    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true); }

    private sealed class TestClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
