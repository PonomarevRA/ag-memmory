using System.ComponentModel;
using System.Text.Json;
using AgMemory.Contracts;
using AgMemory.Core;
using AgMemory.Storage.LanceDb;
using ModelContextProtocol.Server;

namespace AgMemory.McpServer;

/// <summary>Creates, lists and issues short-lived notes inside the server-configured exact scope.</summary>
[McpServerToolType]
public sealed class TemporaryNoteMemoryTools(LocalAgMemoryMcpRuntime memory)
{
    /// <summary>Creates a 48-hour note and returns its author, identifier and expiry.</summary>
    [McpServerTool(Name = "memory_temporary_note_create", Idempotent = true, OpenWorld = false)]
    [Description("Create a local note for 48 hours. Reuse requestKey on retries. conversationId is optional attribution and never selects scope.")]
    public async Task<string> CreateAsync(string title, string content, string requestKey, string? conversationId = null,
        CancellationToken cancellationToken = default) => JsonSerializer.Serialize(
            await memory.TemporaryNoteCreateAsync(title, content, requestKey, conversationId, cancellationToken).ConfigureAwait(false));

    /// <summary>Shows active note titles, authors and delivery history without returning their bodies.</summary>
    [McpServerTool(Name = "memory_temporary_notes", ReadOnly = true, OpenWorld = false)]
    [Description("List active temporary note metadata and recipient history. The optional query matches titles. Listing does not issue the note.")]
    public async Task<string> ListAsync(string query = "", int limit = 5, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await memory.TemporaryNotesAsync(query, limit, cancellationToken).ConfigureAwait(false));

    /// <summary>Records the configured recipient before returning a temporary note's content.</summary>
    [McpServerTool(Name = "memory_temporary_note_read", OpenWorld = false)]
    [Description("Return an active temporary note and record a server-side issuance to this recipient. Issuance does not prove a human read the note.")]
    public async Task<string> ReadAsync(string noteId, string? conversationId = null, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await memory.TemporaryNoteReadAsync(noteId, conversationId, cancellationToken).ConfigureAwait(false));
}

public sealed partial class LocalAgMemoryMcpRuntime
{
    /// <summary>Opens the exact scope's separate note document under the existing annotation backup lease.</summary>
    private TemporaryNoteService TemporaryNotes()
    {
        // Purpose-separate documents let old claim/procedure writers continue without seeing the new state.
        var exactScope = KnowledgeProfileIdentity.GetKnowledge(_configuration.StoragePath, _configuration.Actor,
            _configuration.Scope, "agmemory-temporary-notes");
        return new(KnowledgeAnnotationStore.ForTemporaryNotes(_configuration.StoragePath), "temporary-notes-v1:" + exactScope, TimeProvider.System);
    }

    /// <summary>Validates user data and creates a note bound to the exact configured authority.</summary>
    internal Task<AgentResponse<TemporaryNoteSummary>> TemporaryNoteCreateAsync(string title, string content, string requestKey,
        string? conversationId, CancellationToken cancellationToken) => SafeKnowledgeAsync(() =>
    {
        ValidateAgentText(title, 160);
        ValidateAgentText(content, 4000);
        ValidateRequestKey(requestKey);
        var author = TemporaryParticipant(conversationId);
        return TemporaryNotes().CreateAsync(title.Trim(), content, author, requestKey, cancellationToken);
    });

    /// <summary>Lists bounded metadata without returning note bodies or creating an issuance.</summary>
    internal Task<AgentResponse<IReadOnlyList<TemporaryNoteSummary>>> TemporaryNotesAsync(string query, int limit,
        CancellationToken cancellationToken) => SafeKnowledgeAsync(() =>
    {
        if (query is null || limit is < 1 or > 10) throw new AgentRequestException("invalid_argument");
        if (query.Length > 0) ValidateAgentText(query, 160);
        return TemporaryNotes().ListAsync(query.Trim(), limit, cancellationToken);
    });

    /// <summary>Resolves an opaque note ID only within this scope and commits the issuance.</summary>
    internal Task<AgentResponse<TemporaryNoteRead>> TemporaryNoteReadAsync(string noteId, string? conversationId,
        CancellationToken cancellationToken) => SafeKnowledgeAsync(() =>
    {
        if (!Guid.TryParseExact(noteId, "N", out var id)) throw new AgentRequestException("reference_unavailable");
        var recipient = TemporaryParticipant(conversationId);
        return TemporaryNotes().ReadAsync(id.ToString("N"), recipient, cancellationToken);
    });

    /// <summary>Distinguishes a configured chat from optional caller attribution; neither selects authorization.</summary>
    private TemporaryNoteParticipant TemporaryParticipant(string? conversationId)
    {
        if (conversationId is not null) ValidateRequestKey(conversationId);
        var configured = _configuration.Scope.ChatId?.Value;
        if (configured is not null && conversationId is not null && configured != conversationId)
            throw new AgentRequestException("invalid_argument");
        var attribution = configured is not null ? "configured" : conversationId is null ? "unknown" : "caller_reported";
        return new(_configuration.Actor.Value, _configuration.OriginClient, configured ?? conversationId,
            attribution, _configuration.Scope.RunId?.Value);
    }
}
