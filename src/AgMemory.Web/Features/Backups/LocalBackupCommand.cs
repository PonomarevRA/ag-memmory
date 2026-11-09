using System.Text.Json;
using AgMemory.Storage.LanceDb;
using AgMemory.Web.Hosting;

namespace AgMemory.Web.Features.Backups;

/// <summary>Offline backup commands run before desktop/Web startup and emit status without memory contents.</summary>
public static class LocalBackupCommand
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "--backup" or "--restore-backup";

    /// <summary>Creates the daily backup or extracts a verified archive into a new destination; no live data is replaced here.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken token = default)
    {
        try
        {
            var options = ParseOptions(args);
            if (args[0] == "--restore-backup")
            {
                if (!options.TryGetValue("--restore-to", out var target)) throw new ArgumentException("--restore-to is required.");
                var manifest = await LocalBackupService.RestoreAsync(args[1], target, token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new { Restored = true, manifest.LocalDate, FileCount = manifest.Files.Count, Destination = target }));
            }
            else
            {
                var data = LocalApplicationPaths.ResolveDataDirectory(options.GetValueOrDefault("--data-dir"));
                ValidateStoreSettings(data);
                var backup = LocalApplicationPaths.ResolveBackupDirectory(data, options.GetValueOrDefault("--backup-dir"));
                var result = await new LocalBackupService(data, backup).CreateAsync(DateTimeOffset.Now, token).ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(result));
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            // Archive contents and native exception diagnostics may contain private memory or configured paths.
            Console.Error.WriteLine("AgMemory backup command failed (" + error.GetType().Name + "). Existing data and archives were not replaced.");
            return 1;
        }
    }

    /// <summary>Accepts only explicit, nonrepeated path options; restoring always requires an archive and a separate destination.</summary>
    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = args[0] == "--restore-backup" ? 2 : 1;
        if (index == 2 && (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal)))
            throw new ArgumentException("A ZIP archive is required.");
        for (; index < args.Length; index += 2)
        {
            var name = args[index];
            var allowed = args[0] == "--backup" ? name is "--data-dir" or "--backup-dir" : name == "--restore-to";
            if (!allowed || index + 1 >= args.Length || !result.TryAdd(name, args[index + 1]))
                throw new ArgumentException("Invalid backup command options.");
        }
        return result;
    }

    /// <summary>Refuses to claim a complete backup when local Web settings point at a store outside the supported data layout.</summary>
    private static void ValidateStoreSettings(string data)
    {
        var path = LocalApplicationPaths.PersistentSettingsPath(data);
        if (!File.Exists(path)) return;
        using var settings = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var section in new[] { "MemoryGraph", "MemoryReader" })
        {
            if (!settings.RootElement.TryGetProperty(section, out var value) || !value.TryGetProperty("StoragePath", out var configured)) continue;
            var store = configured.GetString();
            if (string.IsNullOrWhiteSpace(store)) continue;
            var full = Path.GetFullPath(Path.IsPathRooted(store) ? store : Path.Combine(data, store));
            if (full != Path.Combine(data, "memory-graph.lancedb"))
                throw new InvalidOperationException("Custom external stores need a separate backup policy.");
        }
    }
}
