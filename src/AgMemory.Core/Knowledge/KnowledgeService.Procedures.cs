using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed partial class KnowledgeService
{
    /// <summary>Creates or replaces a logical procedure using CAS while retaining immutable historical revisions.</summary>
    public Task<KnowledgeMutationResult> UpsertProcedureAsync(KnowledgeProcedure procedure, long expectedRevision,
        string requestKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        Text(procedure.Key, 120); Text(procedure.Goal, 1000); Text(procedure.Applicability, 1000);
        Text(procedure.Preconditions, 2000); Text(procedure.Steps, 6000); Text(procedure.Verification, 2000);
        Lines(procedure.Aliases);
        if (procedure.RunbookRef is not null) Text(procedure.RunbookRef, 500);
        return MutateAsync("procedure", requestKey, new { procedure.Key, procedure.Goal, procedure.Aliases, procedure.Applicability, procedure.Preconditions, procedure.Steps, procedure.Verification, procedure.RunbookRef, expectedRevision }, (state, now) =>
        {
            Revision(state.Procedures.TryGetValue(procedure.Key, out var old) ? old.Revision : 0, expectedRevision);
            var current = procedure with { Revision = expectedRevision + 1, UpdatedAtUtc = now };
            state.Procedures[procedure.Key] = current;
            state.ProcedureHistory.Add(HistoryKey(current.Key, current.Revision), current);
            return new("updated", current.Key, current.Revision);
        }, cancellationToken);
    }

    /// <summary>Registers one actual incident occurrence. A family groups candidates and never supplies occurrence identity.</summary>
    public Task<KnowledgeMutationResult> RecordOccurrenceAsync(KnowledgeOccurrence occurrence, string requestKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        Text(occurrence.Key, 160); Text(occurrence.FamilyKey, 160); Text(occurrence.Symptom, 2000); Text(occurrence.Environment, 1000);
        if (!Enum.IsDefined(occurrence.Status) || occurrence.OccurredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("invalid_argument");
        return MutateAsync("occurrence", requestKey, new { occurrence.Key, occurrence.FamilyKey, occurrence.Symptom, occurrence.Environment, occurrence.Status }, (state, now) =>
        {
            if (state.Occurrences.TryGetValue(occurrence.Key, out var old))
            {
                if (old.FamilyKey != occurrence.FamilyKey || old.Symptom != occurrence.Symptom || old.Environment != occurrence.Environment || old.Status != occurrence.Status) throw new InvalidOperationException("occurrence_conflict");
                return new("existing", occurrence.Key, 1);
            }
            state.Occurrences.Add(occurrence.Key, occurrence);
            return new("created", occurrence.Key, 1);
        }, cancellationToken);
    }

    /// <summary>Transitions one reported occurrence status with CAS. A status report does not verify remediation or create another occurrence.</summary>
    public Task<KnowledgeMutationResult> UpdateOccurrenceAsync(string key, ProblemOccurrenceStatus status, long expectedRevision,
        string requestKey, CancellationToken cancellationToken = default)
    {
        Text(key, 160);
        if (!Enum.IsDefined(status)) throw new ArgumentException("invalid_argument");
        return MutateAsync("occurrence_status", requestKey, new { key, status, expectedRevision }, (state, now) =>
        {
            if (!state.Occurrences.TryGetValue(key, out var occurrence)) throw new InvalidOperationException("occurrence_unavailable");
            Revision(occurrence.Revision, expectedRevision);
            var allowed = occurrence.Status switch
            {
                ProblemOccurrenceStatus.Open => status is ProblemOccurrenceStatus.Diagnosing or ProblemOccurrenceStatus.Mitigated or ProblemOccurrenceStatus.Resolved or ProblemOccurrenceStatus.Closed,
                ProblemOccurrenceStatus.Diagnosing => status is ProblemOccurrenceStatus.Mitigated or ProblemOccurrenceStatus.Resolved or ProblemOccurrenceStatus.Closed,
                ProblemOccurrenceStatus.Mitigated => status is ProblemOccurrenceStatus.Diagnosing or ProblemOccurrenceStatus.Resolved or ProblemOccurrenceStatus.Closed,
                ProblemOccurrenceStatus.Resolved => status is ProblemOccurrenceStatus.Diagnosing or ProblemOccurrenceStatus.Closed,
                ProblemOccurrenceStatus.Closed => status == ProblemOccurrenceStatus.Diagnosing,
                _ => false
            };
            if (!allowed) throw new ArgumentException("invalid_occurrence_transition");
            var updated = occurrence with { Status = status, Revision = expectedRevision + 1 };
            state.Occurrences[key] = updated;
            return new("updated", key, updated.Revision);
        }, cancellationToken);
    }

    /// <summary>Records a self-reported application against an existing immutable procedure revision. Verified success requires a separate trusted verifier.</summary>
    public Task<KnowledgeMutationResult> RecordApplicationAsync(KnowledgeApplication application, string requestKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        Text(application.Key, 160); Text(application.ProcedureKey, 120); Text(application.Environment, 1000);
        if (application.Evidence is null || application.Evidence.Length > 2000 || !Enum.IsDefined(application.Outcome) ||
            application.Outcome == ProcedureApplicationOutcome.VerifiedSuccess || application.AppliedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("invalid_argument");
        return MutateAsync("application", requestKey, new { application.Key, application.ProcedureKey, application.ProcedureRevision, application.Environment, application.Outcome, application.Evidence, application.OccurrenceKey }, (state, now) =>
        {
            if (application.OccurrenceKey is not null && !state.Occurrences.ContainsKey(application.OccurrenceKey))
                throw new InvalidOperationException("occurrence_unavailable");
            if (!state.ProcedureHistory.ContainsKey(HistoryKey(application.ProcedureKey, application.ProcedureRevision)))
                throw new InvalidOperationException("procedure_unavailable");
            if (state.Applications.TryGetValue(application.Key, out var old))
            {
                if (old.ProcedureKey != application.ProcedureKey || old.ProcedureRevision != application.ProcedureRevision || old.Environment != application.Environment || old.Outcome != application.Outcome || old.Evidence != application.Evidence || old.OccurrenceKey != application.OccurrenceKey) throw new InvalidOperationException("application_conflict");
                return new("existing", application.Key, application.ProcedureRevision);
            }
            state.Applications.Add(application.Key, application);
            return new("created", application.Key, application.ProcedureRevision);
        }, cancellationToken);
    }

    /// <summary>Finds bounded text candidates. Environment is an exact applicability label; absence never implies compatibility.</summary>
    public static IReadOnlyList<KnowledgeProcedure> FindProcedures(KnowledgeState state, string query, string environment, int limit = 10)
    {
        Text(query, 1000); Text(environment, 1000);
        if (limit is < 1 or > 25) throw new ArgumentException("invalid_argument");
        var results = new List<KnowledgeProcedure>();
        foreach (var procedure in state.Procedures.Values)
        {
            if (!string.Equals(procedure.Applicability, environment, StringComparison.OrdinalIgnoreCase)) continue;
            var matches = procedure.Goal.Contains(query, StringComparison.OrdinalIgnoreCase) || procedure.Key.Contains(query, StringComparison.OrdinalIgnoreCase);
            foreach (var alias in procedure.Aliases)
                if (alias.Contains(query, StringComparison.OrdinalIgnoreCase)) matches = true;
            if (matches) results.Add(procedure);
        }
        results.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
        return results.Take(limit).ToArray();
    }
}
