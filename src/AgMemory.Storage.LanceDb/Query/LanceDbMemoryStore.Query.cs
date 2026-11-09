using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Types;
using AgMemory.Contracts;
using lancedb;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LanceDbMemoryStore
{
    private static string BuildAuthorizedPredicate(AuthorizedScopeSet scopes) =>
        $"({string.Join(" OR ", scopes.Selectors.Select(BuildSelectorPredicate))})";

    private static string BuildEligibilityPredicate(MemorySearchEligibility eligibility)
    {
        var clauses = new List<string>
        {
            BuildAuthorizedPredicate(eligibility.AuthorizedScopes),
            $"status = {Literal(eligibility.RequiredLifecycleStatus.ToString())}",
            $"(expires_at_utc IS NULL OR expires_at_utc > {Literal(Utc(eligibility.AsOfUtc))})"
        };
        if (eligibility.Types is { Count: > 0 })
            clauses.Add($"record_type IN ({string.Join(", ", eligibility.Types.OrderBy(type => type).Select(type => Literal(type.ToString())))})");
        return string.Join(" AND ", clauses.Select(clause => $"({clause})"));
    }

    private static string BuildSelectorPredicate(ScopeSelector selector) => string.Join(" AND ",
    [
        $"tenant_id = {Literal(selector.Scope.TenantId.Value)}",
        NullableScopePredicate("project_id", selector.Scope.ProjectId),
        NullableScopePredicate("workspace_id", selector.Scope.WorkspaceId),
        NullableScopePredicate("chat_id", selector.Scope.ChatId),
        NullableScopePredicate("run_id", selector.Scope.RunId)
    ]);

    private static string NullableScopePredicate(string column, ScopeId? value) => value is { } supplied
        ? $"{column} = {Literal(supplied.Value)}"
        : $"{column} IS NULL";

    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string ScopeKey(MemoryScope scope) => string.Concat(
        ScopedValue(scope.TenantId.Value), ScopedValue(scope.ProjectId?.Value), ScopedValue(scope.WorkspaceId?.Value),
        ScopedValue(scope.ChatId?.Value), ScopedValue(scope.RunId?.Value));

    private static string ScopedValue(string? value) => value is null ? "-1:" : string.Create(CultureInfo.InvariantCulture, $"{value.Length}:{value}");

    private static string ReceiptKey(string kind, ScopeSelector scope, string key) => Hash($"{kind}\u001f{ScopeKey(scope.Scope)}\u001f{key}");

    private static string VectorTableName(EmbeddingContract contract) => VectorTableName(
        new EmbeddingReference(contract.Provider, contract.Model, contract.ModelVersion, contract.Dimension, contract.Normalization, "contract"));

    private static string VectorTableName(EmbeddingReference embedding) => $"memory_vectors_{Hash(EmbeddingKey(embedding)!)[..24]}";

    private static string? EmbeddingKey(EmbeddingReference? embedding) => embedding is null
        ? null
        : string.Concat(embedding.Provider, "\u001f", embedding.Model, "\u001f", embedding.ModelVersion, "\u001f",
            embedding.Dimension.ToString(CultureInfo.InvariantCulture), "\u001f", embedding.Normalization);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value)
    {
        var parsed = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (parsed.Offset != TimeSpan.Zero)
            throw new InvalidDataException("LanceDB timestamp values must be persisted with a UTC offset.");
        return parsed;
    }

    private const string TokenDelimiters = " \t\r\n.,;:!?()[]{}/\\-_";

    /// <summary>Returns distinct invariant-uppercase tokens using the original explicit delimiters and Unicode edge trimming.</summary>
    private static IReadOnlyList<string> Tokenize(string text) => TokenizeSet(text).ToArray();

    /// <summary>Scans spans without Split arrays or LINQ intermediates; the ordinal set deduplicates normalized whole tokens.</summary>
    private static HashSet<string> TokenizeSet(string text)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        var content = text.AsSpan();
        foreach (var range in content.SplitAny(TokenDelimiters))
        {
            var token = content[range].Trim();
            if (!token.IsEmpty) terms.Add(token.ToString().ToUpperInvariant());
        }
        return terms;
    }

    /// <summary>Preserves the whole-token match ratio while reusing the directly constructed candidate set.</summary>
    private static double LexicalScore(string content, IReadOnlyList<string> queryTerms)
    {
        var candidateTerms = TokenizeSet(content);
        var matches = 0;
        foreach (var term in queryTerms)
        {
            if (candidateTerms.Contains(term)) matches++;
        }
        return matches == 0 ? 0d : (double)matches / queryTerms.Count;
    }

    // Vector search still needs one array for the LanceDB API; validate the span first to avoid a second copy.
    private static bool HasNonFiniteValue(ReadOnlySpan<float> values)
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value)) return true;
        }

        return false;
    }

    private static void ValidateMemoryRecordForSchema(MemoryRecord record)
    {
        record.Validate();
        if (!Enum.IsDefined(record.Type))
            throw new ArgumentOutOfRangeException(nameof(record.Type), record.Type, "Memory record type must be a defined contract value.");
        if (!Enum.IsDefined(record.Status))
            throw new ArgumentOutOfRangeException(nameof(record.Status), record.Status, "Memory record status must be a defined contract value.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LanceDbMemoryStore));
    }
}
