using AgMemory.Contracts;

namespace AgMemory.Core;

/// <summary>Maintains expiring notes and delivery history in one existing atomic annotation document.</summary>
public sealed class TemporaryNoteService(IKnowledgeAnnotationStore store, string profile, TimeProvider clock)
{
    private const int MaximumActiveNotes = 64;
    private const int MaximumEntries = 256;
    private const int MaximumRecipients = 32;
    private const int MaximumAttempts = 16;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(48);

    /// <summary>Creates a note with an immutable lifetime or replays the same creation within its replay window.</summary>
    public async Task<TemporaryNoteSummary> CreateAsync(string title, string content, TemporaryNoteParticipant author,
        string requestKey, CancellationToken token)
    {
        var key = KnowledgeClaimService.Fingerprint(new { author, requestKey });
        var fingerprint = KnowledgeClaimService.Fingerprint(new { title, content });
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var state = await store.ReadAsync(profile, token).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var notes = Prune(state, now);
            if (notes.TryGetValue(key, out var existing))
            {
                if (existing.Author is null || existing.ExpiresAtUtc <= now) throw new InvalidOperationException("source_unavailable");
                if (existing.Fingerprint != fingerprint) throw new InvalidOperationException("request_conflict");
                return Summarize(existing);
            }
            if (notes.Count >= MaximumEntries || notes.Values.Count(note => note.Author is not null) >= MaximumActiveNotes)
                throw new InvalidOperationException("knowledge_capacity_exceeded");
            var note = new TemporaryNote(Guid.NewGuid().ToString("N"), fingerprint, title, content,
                author, now, now + Lifetime, []);
            notes.Add(key, note);
            if (await SaveAsync(state, notes, token).ConfigureAwait(false)) return Summarize(note);
        }
        throw new InvalidOperationException("knowledge_conflict");
    }

    /// <summary>Returns active metadata and existing issuance history without recording a delivery.</summary>
    public async Task<IReadOnlyList<TemporaryNoteSummary>> ListAsync(string query, int limit, CancellationToken token)
    {
        var state = await store.ReadAsync(profile, token).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var notes = state.TemporaryNotes ?? [];
        return notes.Values
            .Where(note => note.ExpiresAtUtc > now && note.Title is { } title && title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(note => note.CreatedAtUtc).ThenBy(note => note.Id, StringComparer.Ordinal)
            .Take(limit).Select(Summarize).ToArray();
    }

    /// <summary>Commits recipient metadata before returning the body; concurrent deliveries retry without losing history.</summary>
    public async Task<TemporaryNoteRead> ReadAsync(string noteId, TemporaryNoteParticipant recipient, CancellationToken token)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var state = await store.ReadAsync(profile, token).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var notes = Prune(state, now);
            var match = notes.FirstOrDefault(entry => entry.Value.Id == noteId);
            if (match.Value is not { Content: not null } note)
                throw new InvalidOperationException("source_unavailable");
            var updated = note with { Deliveries = RecordDelivery(note.Deliveries, recipient, now) };
            notes[match.Key] = updated;
            if (!await SaveAsync(state, notes, token).ConfigureAwait(false)) continue;
            if (clock.GetUtcNow() >= updated.ExpiresAtUtc) throw new InvalidOperationException("source_unavailable");
            return new(Summarize(updated), updated.Content!);
        }
        throw new InvalidOperationException("knowledge_conflict");
    }

    /// <summary>Removes expired bodies and identities while retaining opaque replay keys for another 48 hours.</summary>
    private static Dictionary<string, TemporaryNote> Prune(KnowledgeAnnotationState state, DateTimeOffset now)
    {
        var notes = new Dictionary<string, TemporaryNote>(StringComparer.Ordinal);
        if (state.TemporaryNotes is null) return notes;
        foreach (var (key, note) in state.TemporaryNotes)
        {
            if (note.ExpiresAtUtc + Lifetime <= now) continue;
            notes.Add(key, note.ExpiresAtUtc > now ? note : note with
            {
                Fingerprint = string.Empty, Title = null, Content = null, Author = null, Deliveries = []
            });
        }
        return notes;
    }

    /// <summary>Coalesces repeated issuance to one identity and rejects a new recipient at capacity before disclosure.</summary>
    private static TemporaryNoteDelivery[] RecordDelivery(TemporaryNoteDelivery[] previous, TemporaryNoteParticipant recipient,
        DateTimeOffset now)
    {
        var index = Array.FindIndex(previous, delivery => delivery.Recipient == recipient);
        if (index < 0)
        {
            if (previous.Length >= MaximumRecipients) throw new InvalidOperationException("knowledge_capacity_exceeded");
            return [.. previous, new(recipient, now, now, 1)];
        }
        var updated = previous.ToArray();
        var delivery = updated[index];
        if (delivery.Count == int.MaxValue) throw new InvalidOperationException("knowledge_capacity_exceeded");
        updated[index] = delivery with
        {
            LastIssuedAtUtc = now > delivery.LastIssuedAtUtc ? now : delivery.LastIssuedAtUtc,
            Count = delivery.Count + 1
        };
        return updated;
    }

    /// <summary>Uses the existing atomic compare-and-exchange and backup lease.</summary>
    private Task<bool> SaveAsync(KnowledgeAnnotationState state, Dictionary<string, TemporaryNote> notes, CancellationToken token) =>
        store.CompareExchangeAsync(profile, state.Version, state with
        {
            Version = state.Version + 1,
            TemporaryNotes = notes,
            TemporaryNoteGeneration = state.TemporaryNoteGeneration ?? Guid.NewGuid()
        }, token);

    /// <summary>Projects only active note metadata and its committed history.</summary>
    private static TemporaryNoteSummary Summarize(TemporaryNote note) =>
        new(note.Id, note.Title!, note.Author!, note.CreatedAtUtc, note.ExpiresAtUtc, note.Deliveries);
}
