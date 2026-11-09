using System.Reflection;

namespace AgMemory.Web.Features.AppVersion;

/// <summary>Product version for the local Web/macOS host. Does not include paths or secrets.</summary>
public sealed record AppVersionInfo(string Version, string InformationalVersion)
{
    public static AppVersionInfo From(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = Normalize(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        var product = Product(informational) ?? ThreePart(assembly.GetName().Version) ?? "0.0.0";
        return new(product, informational ?? product);
    }

    private static string? Product(string? informational)
    {
        if (informational is null) return null;
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var value = plus >= 0 ? informational[..plus] : informational;
        return IsSafe(value, 32) ? value : null;
    }

    private static string? ThreePart(Version? version) =>
        version is null ? null : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (space >= 0) trimmed = trimmed[..space];
        if (trimmed.Length > 64) trimmed = trimmed[..64];
        return IsSafe(trimmed, 64) ? trimmed : null;
    }

    private static bool IsSafe(string value, int maximum) =>
        value.Length is > 0 && value.Length <= maximum &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-' or '_');
}
