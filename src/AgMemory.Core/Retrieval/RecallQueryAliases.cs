using System.Reflection;
using System.Text.Json;

namespace AgMemory.Core;

/// <summary>One reviewed lexical expansion; it supplies search terms, never a fact, authority or target record identity.</summary>
public sealed record RecallAliasQuery(string Concept, string Query, double Weight);

/// <summary>Bounded RU/EN phrase and case-sensitive acronym lookup. Unknown formulations retain ordinary lexical search.</summary>
public static class RecallQueryAliases
{
    public const string Version = "recall-aliases/v1";
    private const string Separators = " \t\r\n.,:;!?()[]{}\"'«»“”‘’„_/\\-=+|<>";
    private static readonly Lazy<Concept[]> Concepts = new(ReadConcepts);

    /// <summary>Returns at most three distinct expansions in stable longest-match/key order; full query text is never rewritten in storage.</summary>
    public static IReadOnlyList<RecallAliasQuery> Expand(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Legacy recall accepts longer text. Skip optional expansion instead of narrowing that contract.
        if (query.Length > 1000) return [];
        var words = Tokens(query);
        var matches = new List<(Concept Concept, int Length, bool Acronym)>();
        foreach (var concept in Concepts.Value)
        {
            var length = 0;
            var acronym = false;
            foreach (var token in concept.Acronyms)
                if (words.Contains(token, StringComparer.Ordinal)) { length = Math.Max(length, 1); acronym = true; }
            foreach (var alias in concept.Aliases)
            {
                var phrase = Tokens(alias);
                if (ContainsPhrase(words, phrase)) length = Math.Max(length, phrase.Length);
            }
            if (length > 0 && !string.Equals(query.Trim(), concept.Query, StringComparison.OrdinalIgnoreCase))
                matches.Add((concept, length, acronym));
        }
        matches.Sort((left, right) =>
        {
            var lengthOrder = right.Length.CompareTo(left.Length);
            return lengthOrder != 0 ? lengthOrder : string.CompareOrdinal(left.Concept.Key, right.Concept.Key);
        });
        var result = new List<RecallAliasQuery>(3);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { query.Trim() };
        foreach (var match in matches)
        {
            if (seen.Add(match.Concept.Query))
                result.Add(new(match.Concept.Key, match.Concept.Query, match.Acronym ? 2d : 1d));
            if (result.Count == 3) break;
        }
        return result;
    }

    /// <summary>Matches complete contiguous tokens, preserving identifier boundaries while accepting ordinary letter case.</summary>
    private static bool ContainsPhrase(string[] words, string[] phrase)
    {
        if (phrase.Length == 0 || phrase.Length > words.Length) return false;
        for (var start = 0; start <= words.Length - phrase.Length; start++)
        {
            var matches = true;
            for (var offset = 0; offset < phrase.Length; offset++)
            {
                if (string.Equals(words[start + offset], phrase[offset], StringComparison.OrdinalIgnoreCase)) continue;
                matches = false;
                break;
            }
            if (matches) return true;
        }
        return false;
    }

    private static string[] Tokens(string text) => text.Split(Separators.ToCharArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Loads the packaged reviewable dictionary once and rejects malformed or unbounded rules at startup use.</summary>
    private static Concept[] ReadConcepts()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgMemory.Core.recall-aliases.v1.json")
            ?? throw new InvalidDataException("Recall alias dictionary is missing.");
        var document = JsonSerializer.Deserialize<DictionaryDocument>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Recall alias dictionary is empty.");
        if (document.Schema != Version || document.Concepts is null || document.Concepts.Length is < 1 or > 64)
            throw new InvalidDataException("Unsupported recall alias dictionary.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var concept in document.Concepts)
        {
            if (concept is null || string.IsNullOrWhiteSpace(concept.Key) || !keys.Add(concept.Key) || concept.Key.Length > 80 ||
                string.IsNullOrWhiteSpace(concept.Query) || concept.Query.Length > 120 ||
                string.IsNullOrWhiteSpace(concept.Source) || concept.Source.Length > 200 ||
                concept.Aliases is null || concept.Acronyms is null || concept.Aliases.Length > 24 || concept.Acronyms.Length > 8)
                throw new InvalidDataException("Invalid recall alias concept.");
            foreach (var phrase in concept.Aliases)
                if (string.IsNullOrWhiteSpace(phrase) || phrase.Length > 120 || Tokens(phrase).Length > 12)
                    throw new InvalidDataException("Invalid recall alias phrase.");
            foreach (var acronym in concept.Acronyms)
                if (acronym is null || acronym.Length is < 2 or > 12 || acronym.Any(character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character)))
                    throw new InvalidDataException("Invalid recall acronym.");
        }
        return document.Concepts;
    }

    private sealed record DictionaryDocument(string Schema, Concept[] Concepts);
    private sealed record Concept(string Key, string Query, string[] Aliases, string[] Acronyms, string Source);
}
