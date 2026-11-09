using System.Text.RegularExpressions;
using Agm.Memory.Abstractions;

namespace Agm.Memory;

public sealed partial class LocalWorkspaceMemoryExtractor : IWorkspaceMemoryExtractor
{
    private const int MaximumCandidateCharacters = 4_000;

    public string? Extract(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var safe = AuthorizationPattern().Replace(output, "Authorization: [redacted]");
        safe = SecretPattern().Replace(safe, "$1=[redacted]");
        safe = BareCredentialPattern().Replace(safe, "[redacted]");
        safe = PrivateKeyPattern().Replace(safe, "[redacted private key]");
        safe = ApprovalPattern().Replace(safe, "$1[redacted]");
        var lines = safe.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length <= 600)
            .Distinct(StringComparer.Ordinal)
            .Take(20);
        var candidate = string.Join('\n', lines).Trim();
        return candidate.Length == 0 ? null : candidate[..Math.Min(candidate.Length, MaximumCandidateCharacters)];
    }

    [GeneratedRegex("(?im)\\b(api[-_ ]?key|access[-_ ]?token|refresh[-_ ]?token|token|secret|password)\\b\\s*[:=]\\s*(?:bearer\\s+)?[^\\s,;]+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex("(?im)\\bauthorization\\s*:\\s*bearer\\s+[^\\s,;]+")]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex("(?i)\\b(?:bearer\\s+)?(?:sk-[a-z0-9_-]{16,}|gh[pousr]_[a-z0-9_]{16,}|eyJ[a-z0-9_-]{8,}\\.[a-z0-9_-]{8,}\\.[a-z0-9_-]{8,})\\b")]
    private static partial Regex BareCredentialPattern();

    [GeneratedRegex("(?is)-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----.*?-----END (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex("(?im)(user[_ -]?code|verification[_ -]?(?:uri|url)|approval[_ -]?payload)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex ApprovalPattern();
}

public sealed class LocalWorkspaceMemoryConsolidator : IWorkspaceMemoryConsolidator
{
    public string Consolidate(string? existing, string candidate, int maximumCharacters)
    {
        var lines = $"{existing}\n{candidate}"
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        while (lines.Count > 0 && string.Join('\n', lines).Length > maximumCharacters)
            lines.RemoveAt(0);
        return string.Join('\n', lines);
    }
}
