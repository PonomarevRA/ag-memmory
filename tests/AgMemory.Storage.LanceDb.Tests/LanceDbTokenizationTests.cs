using System.Globalization;
using System.Reflection;
using AgMemory.Storage.LanceDb;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Independent legacy oracle protects exact separators, Unicode trimming/casing, distinct terms and lexical ratios.</summary>
public sealed class LanceDbTokenizationTests
{
    private static readonly MethodInfo Tokens = typeof(LanceDbMemoryStore).GetMethod("Tokenize", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Score = typeof(LanceDbMemoryStore).GetMethod("LexicalScore", BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>Includes Unicode whitespace that trims only at token edges and punctuation deliberately absent from the separator set.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n.,;:!?()[]{}/\\-_ ")]
    [InlineData("ALPHA/alpha_beta ALPHA-alpha")]
    [InlineData("\u00a0ёжик\u2003 café e\u0301 ЁЖИК")]
    [InlineData("alpha\u00a0beta alpha\u2003beta")]
    [InlineData("a+b=a|b@c#d a—b a'b \u200btoken\u200b")]
    [InlineData("i I ı İ σ ς Σ ß ﬃ \ud800 \udfff 😀")]
    public void TokenizationMatchesOriginalContractInEveryCulture(string text)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "tr-TR", "ru-RU" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Assert.Equal(Legacy(text), (IReadOnlyList<string>)Tokens.Invoke(null, [text])!);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    /// <summary>Deterministic short randomized Unicode inputs compare scores against the pre-change algorithm, not another patched provider.</summary>
    [Fact]
    public void WholeTokenScoresMatchLegacyOracleAcrossMixedUnicode()
    {
        const string alphabet = "aAбЁiıİσςßé\u0301\u00a0\u2003\u200b😀 \t.,;:!?()[]{}/\\-_+—";
        var random = new Random(42017);
        for (var trial = 0; trial < 120; trial++)
        {
            var content = new string(Enumerable.Range(0, 80).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var query = trial % 4 == 0 ? content : new string(Enumerable.Range(0, 15).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var terms = Legacy(query);
            var candidates = Legacy(content).ToHashSet(StringComparer.Ordinal);
            var matches = terms.Count(candidates.Contains);
            var expected = matches == 0 ? 0d : (double)matches / terms.Count;
            Assert.Equal(expected, (double)Score.Invoke(null, [content, terms])!);
        }
        Assert.Equal(0d, (double)Score.Invoke(null, ["alphabet", new[] { "alpha" }])!);
        Assert.Equal(.5d, (double)Score.Invoke(null, ["alpha alpha ALPHA", new[] { "ALPHA", "BETA" }])!);
    }

    /// <summary>Retain the exact original algorithm only in this independent behavior oracle.</summary>
    private static IReadOnlyList<string> Legacy(string text) => text.Split(
        [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(token => token.ToUpperInvariant()).Where(token => token.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
}
