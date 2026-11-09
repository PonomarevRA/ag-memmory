namespace AgMemory.Core;

/// <summary>Bounded literal text for reader cards; source markup is never executed.</summary>
public static class MemoryReaderCardText
{
    /// <summary>Uses the first nonempty line as a title and a compact plain preview without copying the entire document.</summary>
    public static (string Title, string Preview) Format(string raw, int titleLimit, int previewLimit)
    {
        var preview = new System.Text.StringBuilder(previewLimit + 1);
        var first = raw.AsSpan().TrimStart();
        var lineEnd = first.IndexOfAny('\r', '\n');
        if (lineEnd >= 0) first = first[..lineEnd];
        var sentenceEnd = first.IndexOf(". ", StringComparison.Ordinal);
        if (sentenceEnd >= 20) first = first[..(sentenceEnd + 1)];
        var heading = first[..Math.Min(first.Length, titleLimit + 1)].ToString().Trim().TrimStart('#').Trim();
        var previousSpace = true;
        foreach (var character in raw)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                if (!previousSpace && preview.Length <= previewLimit) preview.Append(' ');
                previousSpace = true;
            }
            else
            {
                if (preview.Length <= previewLimit) preview.Append(character);
                previousSpace = false;
            }
            if (preview.Length > previewLimit) break;
        }
        return (Bound(heading.Length == 0 ? "Запись памяти" : heading, titleLimit), Bound(preview.ToString().Trim(), previewLimit));
    }

    private static string Bound(string value, int limit) => value.Length <= limit ? value : string.Concat(value.AsSpan(0, limit - 1), "…");
}
