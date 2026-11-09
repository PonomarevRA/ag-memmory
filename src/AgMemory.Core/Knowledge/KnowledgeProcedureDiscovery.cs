using System.Text.RegularExpressions;
using AgMemory.Contracts;

namespace AgMemory.Core;

public sealed record KnowledgeProcedureCandidate(KnowledgeProcedure Procedure, KnowledgeProcedureFacets? Facets,
    string ApplicabilityStatus, double Score);

/// <summary>Lexical discovery broadens recall only. Exact revision facets and caller-declared capabilities never verify transfer or prerequisites.</summary>
public static class KnowledgeProcedureDiscovery
{
    /// <summary>Finds exact-label cards and explicit same-family transfer candidates, excluding missing capabilities and unrelated environments.</summary>
    public static IReadOnlyList<KnowledgeProcedureCandidate> Find(KnowledgeState knowledge, KnowledgeAnnotationState annotations,
        string query, string environment, string? family, IReadOnlyCollection<string> capabilities, int limit)
    {
        var queries = new List<string> { query };
        foreach (var alias in RecallQueryAliases.Expand(query)) queries.Add(alias.Query);
        var queryTerms = Terms(string.Join(" ", queries));
        var supplied = new HashSet<string>(capabilities, StringComparer.OrdinalIgnoreCase);
        var results = new List<KnowledgeProcedureCandidate>();
        foreach (var procedure in knowledge.Procedures.Values)
        {
            annotations.ProcedureFacets.TryGetValue(procedure.Key + ":" + procedure.Revision, out var facets);
            var exact = string.Equals(procedure.Applicability, environment, StringComparison.OrdinalIgnoreCase);
            var sameFamily = facets is not null && family is not null && string.Equals(facets.EnvironmentFamily, family, StringComparison.OrdinalIgnoreCase);
            if (!exact && !sameFamily) continue;
            if (facets is not null && facets.RequiredCapabilities.Any(required => !supplied.Contains(required))) continue;
            var candidateTerms = Terms(procedure.Goal + " " + string.Join(" ", procedure.Aliases));
            var matched = 0;
            foreach (var term in queryTerms) if (candidateTerms.Contains(term)) matched++;
            if (matched == 0) continue;
            var status = !exact ? "TransferCandidateNeedsReview" : facets is null ? "ExactLabelOnlyNeedsReview" : "ExactRevisionNeedsPreconditionCheck";
            results.Add(new(procedure, facets, status, (double)matched / Math.Max(1, queryTerms.Count)));
        }
        results.Sort((left, right) =>
        {
            var order = right.Score.CompareTo(left.Score);
            return order != 0 ? order : string.CompareOrdinal(left.Procedure.Key, right.Procedure.Key);
        });
        return results.Take(limit).ToArray();
    }

    private static HashSet<string> Terms(string text)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, @"[\p{L}\p{N}]+")) terms.Add(match.Value);
        return terms;
    }
}
