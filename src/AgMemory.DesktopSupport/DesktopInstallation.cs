using System.Text.Json;

namespace AgMemory.DesktopSupport;

/// <summary>Installer-owned locations and desktop preferences; reading never moves or creates a store.</summary>
public sealed record DesktopInstallation(string SchemaVersion, string AppId, string DataDirectory,
    string BackupDirectory, string ApplicationPath, bool CloseToTray = true, bool LaunchAtLogin = false, int? PreferredPort = null)
{
    public const string App = "agmemory";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Finds the stable per-user locator independently of the replaceable executable and chosen data root.</summary>
    public static string LocatorPath(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "AGDesktop", App, "installation.json");
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AGDesktop", App, "installation.json");
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(config) ? Path.Combine(home, ".config") : config, "ag-desktop", App, "installation.json");
    }

    /// <summary>Returns null for an absent locator, but rejects malformed configuration instead of silently selecting a new store.</summary>
    public static DesktopInstallation? Read(string? path = null)
    {
        path ??= LocatorPath();
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || new FileInfo(path).Length > 65_536)
                throw new InvalidDataException("Invalid desktop installation configuration.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[65_537];
            var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (length > 65_536) throw new InvalidDataException("Invalid desktop installation configuration.");
            var result = JsonSerializer.Deserialize<DesktopInstallation>(bytes.AsSpan(0, length), Json)
                ?? throw new InvalidDataException("Invalid desktop installation configuration.");
            return result.Validate();
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
        { throw new InvalidDataException("Invalid desktop installation configuration.", error); }
    }

    /// <summary>Canonicalizes absolute roots and rejects app/data/backup overlap, repositories, bundles and invalid ports.</summary>
    public DesktopInstallation Validate()
    {
        if (SchemaVersion != "ag-desktop/install-v1" || AppId != App || PreferredPort is < 1024 or > 65535)
            throw new InvalidDataException("Invalid desktop installation configuration.");
        var data = Canonical(DataDirectory); var backup = Canonical(BackupDirectory); var app = Canonical(ApplicationPath);
        const string invalidRoots = "Desktop storage roots must be separate and outside repositories or application bundles.";
        if (Path.GetPathRoot(data) == data || Path.GetPathRoot(backup) == backup) throw new InvalidDataException(invalidRoots);
        if (File.Exists(data) || File.Exists(backup)) throw new InvalidDataException(invalidRoots);
        if (Overlap(data, backup) || Overlap(data, app) || Overlap(backup, app)) throw new InvalidDataException(invalidRoots);
        if (InReplaceableTree(data) || InReplaceableTree(backup)) throw new InvalidDataException(invalidRoots);
        return this with { DataDirectory = data, BackupDirectory = backup, ApplicationPath = app };
    }

    /// <summary>Resolves existing symlink components before comparing roots; nonexistent trailing components remain absolute.</summary>
    public static string Canonical(string value) => Canonical(value, 32);

    /// <summary>Bounds link expansion and canonicalizes target ancestors as well as the original path.</summary>
    private static string Canonical(string value, int remaining)
    {
        if (remaining == 0) throw new InvalidDataException("Too many desktop path links.");
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new InvalidDataException("Absolute desktop paths are required.");
        var full = Path.GetFullPath(value); var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Path.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo item = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var target = item.ResolveLinkTarget(true)?.FullName ?? throw new InvalidDataException("Unresolved desktop path link.");
            current = Canonical(target, remaining - 1);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>Uses component boundaries and the platform's path comparison, never string-prefix authority.</summary>
    public static bool Overlap(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return first.Equals(second, comparison) || first.StartsWith(second + Path.DirectorySeparatorChar, comparison)
            || second.StartsWith(first + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Rejects every repository or app ancestor, including worktree .git marker files.</summary>
    private static bool InReplaceableTree(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) return true;
        return false;
    }
}
