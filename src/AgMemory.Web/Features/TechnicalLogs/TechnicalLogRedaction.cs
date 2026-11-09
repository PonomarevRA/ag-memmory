using System.Text;
using System.Text.RegularExpressions;

namespace AgMemory.Web.Features.TechnicalLogs;

/// <summary>Bounds text and masks common credential syntax; arbitrary logger objects are never inspected or stringified.</summary>
internal static class TechnicalLogRedaction
{
    private static readonly Regex Secrets = new(@"(?i)(\b(?:password|passwd|secret|token|api[_-]?key|authorization|connectionstring)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)|\bBearer\s+[^\s,;]+|(?<=://)[^/\s:@]+:[^/\s@]+@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(25));
    private static readonly string[] Sensitive = ["password", "passwd", "secret", "token", "key", "authorization", "cookie", "credential", "connectionstring", "actor", "scope", "tenant", "memory", "body", "payload", "headers", "request", "response", "prompt", "recordid", "storagepath"];

    /// <summary>Returns bounded sanitized text or a fixed omission marker if redaction itself fails.</summary>
    public static string Text(string? value, int maximum)
    {
        if (value is null) return "";
        try
        {
            var input = value.AsSpan(0, Math.Min(value.Length, 8192));
            var clean = new StringBuilder(input.Length);
            foreach (var character in input)
                if (!char.IsControl(character) || character is '\n' or '\t') clean.Append(character);
            var redacted = Secrets.Replace(clean.ToString(), match => match.Groups[1].Success ? match.Groups[1].Value + "[redacted]" : "[redacted]");
            var length = Math.Min(redacted.Length, maximum);
            if (length > 0 && char.IsHighSurrogate(redacted[length - 1])) length--;
            return redacted[..length];
        }
        catch { return "[omitted]"; }
    }

    public static bool IsSensitive(string key) => Sensitive.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    /// <summary>Copies only finite diagnostic scalars and bounded text; unknown objects are omitted without ToString.</summary>
    public static bool TryScalar(string key, object? value, out object? scalar)
    {
        scalar = null;
        if (IsSensitive(key)) { scalar = "[redacted]"; return true; }
        switch (value)
        {
            case null: return true;
            case string text: scalar = Text(text, 128); return true;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal: scalar = value; return true;
            case double number when double.IsFinite(number): scalar = number; return true;
            case float number when float.IsFinite(number): scalar = number; return true;
            default: return false;
        }
    }
}
