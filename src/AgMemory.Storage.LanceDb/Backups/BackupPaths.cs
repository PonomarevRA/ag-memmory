namespace AgMemory.Storage.LanceDb;

/// <summary>Local archive paths are operator-selected, disjoint from data, and never traversed through symbolic links.</summary>
internal static class BackupPaths
{
    /// <summary>Checks every existing path component, including a parent link that would redirect a private backup.</summary>
    public static string Validate(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) throw new IOException("Backup paths must not contain symbolic links.");
        return full;
    }

    /// <summary>Refuses nested roots so neither a snapshot nor retention can consume application data recursively.</summary>
    public static void RequireDisjoint(string first, string second)
    {
        if (Inside(first, second) || Inside(second, first)) throw new ArgumentException("Data and backup directories must be separate.");
    }

    private static bool Inside(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates owner-only staging and archive directories; existing trusted directories retain their ownership.</summary>
    public static void CreatePrivateDirectory(string path)
    {
        Validate(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Rejects absolute, nonportable and ambiguous archive names before any extraction touches disk.</summary>
    public static void ValidateEntry(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
            path.Split('/').Any(part => part is "" or "." or "..")) throw new InvalidDataException("Unsafe ZIP entry.");
    }
}
