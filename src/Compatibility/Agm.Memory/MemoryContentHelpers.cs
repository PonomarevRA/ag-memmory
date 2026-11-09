using System.Text.RegularExpressions;

namespace Agm.Memory;

public static class MemoryContentFitter
{
    public static string Fit(string content, int maximumCharacters)
    {
        if (maximumCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        if (string.IsNullOrEmpty(content) || content.Length <= maximumCharacters) return content ?? string.Empty;
        var lines = content.Split('\n', StringSplitOptions.None).ToList();
        while (lines.Count > 1 && string.Join('\n', lines).Length > maximumCharacters)
            lines.RemoveAt(0);
        var fitted = string.Join('\n', lines);
        return fitted.Length <= maximumCharacters ? fitted : fitted[^maximumCharacters..];
    }
}

public static partial class MemorySecretRedactor
{
    public static string Redact(string value) =>
        SecretPattern().Replace(BearerPattern().Replace(value, "${prefix}[REDACTED]"), "${prefix}[REDACTED]");

    [GeneratedRegex("(?i)(?<prefix>bearer\\s+)[^\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(?i)(?<prefix>[\\\"']?(?:access[_-]?token|token|password|secret|api[_-]?key|authorization)[\\\"']?\\s*[=:]\\s*)(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();
}
