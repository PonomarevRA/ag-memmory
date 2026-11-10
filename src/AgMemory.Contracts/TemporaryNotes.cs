namespace AgMemory.Contracts;

/// <summary>Configured actor/client and the available chat attribution for a local note operation.</summary>
public sealed record TemporaryNoteParticipant(string Actor, string Client, string? ConversationId,
    string ConversationAttribution, string? RunId);

/// <summary>Server-side issuances to one participant, coalesced without losing first/last time or count.</summary>
public sealed record TemporaryNoteDelivery(TemporaryNoteParticipant Recipient, DateTimeOffset FirstIssuedAtUtc,
    DateTimeOffset LastIssuedAtUtc, int Count);

/// <summary>Persistent note state; an expired entry can retain only an opaque replay tombstone.</summary>
public sealed record TemporaryNote(string Id, string Fingerprint, string? Title, string? Content,
    TemporaryNoteParticipant? Author, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
    TemporaryNoteDelivery[] Deliveries);

/// <summary>Lists note provenance and delivery history without exposing the note's body.</summary>
public sealed record TemporaryNoteSummary(string NoteId, string Title, TemporaryNoteParticipant Author,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, TemporaryNoteDelivery[] Deliveries);

/// <summary>One committed server-side issuance of a temporary note.</summary>
public sealed record TemporaryNoteRead(TemporaryNoteSummary Note, string Content);
