namespace AgMemory.Web.Hosting;

/// <summary>Resolves user-owned data outside a replaceable application bundle.</summary>
public static class LocalApplicationPaths
{
    public const string DataDirectoryEnvironmentVariable = "AGMEMORY_DATA_DIR";
    public const string ApplicationDirectoryName = "AgMemory";

    public static string ResolveDataDirectory(string? configuredDirectory = null)
    {
        var overrideDirectory = configuredDirectory ?? Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(overrideDirectory);

        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
            applicationData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(applicationData, ApplicationDirectoryName);
    }

    public static string PersistentSettingsPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "appsettings.local.json");
}
