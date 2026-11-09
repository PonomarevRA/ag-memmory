using AgMemory.ActivityJournal;
using AgMemory.DesktopSupport;

namespace AgMemory.Web.Hosting;

/// <summary>Resolves external user data, preserving explicit authority and installer-owned locations without migration.</summary>
public static class LocalApplicationPaths
{
    public const string DataDirectoryEnvironmentVariable = "AGMEMORY_DATA_DIR";
    public const string ApplicationDirectoryName = "AgMemory";

    /// <summary>Explicit CLI data wins over environment, then the shared installer locator, then the existing platform default.</summary>
    public static string ResolveDataDirectory(string? configuredDirectory = null) =>
        !string.IsNullOrWhiteSpace(configuredDirectory) ? Path.GetFullPath(configuredDirectory) : ActivityIdentity.DataDirectory();

    /// <summary>Explicit backup paths win; an alternate data override never sends its backups into another installation's location.</summary>
    public static string ResolveBackupDirectory(string dataDirectory, string? configuredDirectory = null)
    {
        var configured = configuredDirectory ?? Environment.GetEnvironmentVariable("AGMEMORY_BACKUP_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var installation = DesktopInstallation.Read();
        if (installation is not null && DesktopInstallation.Canonical(dataDirectory) == installation.DataDirectory)
            return installation.BackupDirectory;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataDirectory))!, "AgMemory-Backups", "automatic");
    }

    public static string PersistentSettingsPath(string dataDirectory) => Path.Combine(dataDirectory, "appsettings.local.json");
}
