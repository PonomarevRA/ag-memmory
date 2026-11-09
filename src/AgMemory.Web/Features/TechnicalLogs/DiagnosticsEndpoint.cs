using System.Diagnostics;

namespace AgMemory.Web.Features.TechnicalLogs;

/// <summary>Current native process usage and bounded owned-journal disk measurement, independent of consumers.</summary>
public sealed record ServiceDiagnostics(string SchemaVersion, string Application, DateTimeOffset CollectedAt,
    ProcessDiagnostics Process, StorageDiagnostics Storage, DiagnosticsHealth Health);
public sealed record ProcessDiagnostics(int ProcessId, long? ResidentBytes, long? PeakResidentBytes, double UptimeSeconds);
public sealed record StorageDiagnostics(long? JournalBytes, long? DataBytes, string Measurement);
public sealed record DiagnosticsHealth(string Status, string? ErrorCode);

/// <summary>Reads only this service's two journal directories; the bounded disk walk is cached for five seconds.</summary>
public sealed class LocalServiceDiagnostics(string dataDirectory, string? ownedDataDirectory = null)
{
    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly double _initialUptime = InitialUptime();
    private long _diskMeasured;
    private long? _diskBytes;
    private string? _diskError;
    private long _dataMeasured;
    private long? _dataBytes;
    private string? _dataError;

    /// <summary>Returns live process memory and cached journal bytes; unknown data and failed gauges remain null.</summary>
    public ServiceDiagnostics Read()
    {
        long? resident = null, peak = null;
        string? processError = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            var current = process.WorkingSet64;
            var maximum = process.PeakWorkingSet64;
            resident = current > 0 ? current : null;
            peak = maximum > 0 ? maximum : null;
            if (resident is null || peak is null) processError = "process_gauge_unavailable";
        }
        catch { processError = "process_measurement_failed"; }
        lock (_gate)
        {
            if (_diskMeasured == 0 || Stopwatch.GetElapsedTime(_diskMeasured) >= TimeSpan.FromSeconds(5)) MeasureDisk();
            if (_dataMeasured == 0 || Stopwatch.GetElapsedTime(_dataMeasured) >= TimeSpan.FromSeconds(60)) MeasureOwnedData();
            var error = _diskError ?? _dataError ?? processError;
            return new("service-diagnostics/v1", "agmemory", DateTimeOffset.UtcNow,
                new(Environment.ProcessId, resident, peak, _initialUptime + Stopwatch.GetElapsedTime(_started).TotalSeconds),
                new(_diskBytes, _dataBytes, _dataBytes is null ? "journals-only" : "owned-data"), new(error is null ? "ok" : "degraded", error));
        }
    }

    /// <summary>Limits filesystem metadata work to 4,096 top-level files in known directories; no database or recursion is involved.</summary>
    private void MeasureDisk()
    {
        _diskMeasured = Stopwatch.GetTimestamp();
        try
        {
            long bytes = 0;
            var count = 0;
            foreach (var name in new[] { "agent-activity", "technical-logs" })
            {
                var directory = Path.Combine(dataDirectory, name);
                if (File.Exists(directory)) throw new IOException("invalid_journal_directory");
                if (!Directory.Exists(directory)) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("journal_symlink");
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (++count > 4096) throw new IOException("measurement_limit");
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("journal_symlink");
                    bytes = checked(bytes + new FileInfo(file).Length);
                }
            }
            _diskBytes = bytes; _diskError = null;
        }
        catch { _diskBytes = null; _diskError = "journal_measurement_failed"; }
    }

    /// <summary>Stats only the configured graph root, cached for sixty seconds; no database is opened and links are rejected.</summary>
    private void MeasureOwnedData()
    {
        _dataMeasured = Stopwatch.GetTimestamp(); _dataBytes = null; _dataError = null;
        if (string.IsNullOrEmpty(ownedDataDirectory)) return;
        try
        {
            var root = Path.GetFullPath(ownedDataDirectory);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(root); }
            catch (DirectoryNotFoundException) { return; }
            catch (FileNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) throw new IOException("invalid_data_directory");
            var journalRoots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            { Path.GetFullPath(Path.Combine(dataDirectory, "agent-activity")), Path.GetFullPath(Path.Combine(dataDirectory, "technical-logs")) };
            if (journalRoots.Contains(root)) return;
            var pending = new Stack<(string Directory, int Depth)>(); pending.Push((root, 0));
            long bytes = 0;
            var inspected = 0;
            while (pending.TryPop(out var current))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current.Directory))
                {
                    if (++inspected > 10000) throw new IOException("data_measurement_limit");
                    var item = File.GetAttributes(entry);
                    if ((item & FileAttributes.ReparsePoint) != 0) throw new IOException("data_symlink");
                    if ((item & FileAttributes.Directory) != 0)
                    {
                        if (journalRoots.Contains(Path.GetFullPath(entry))) continue;
                        if (current.Depth >= 8) throw new IOException("data_measurement_depth");
                        pending.Push((entry, current.Depth + 1));
                    }
                    else bytes = checked(bytes + new FileInfo(entry).Length);
                }
            }
            _dataBytes = bytes;
        }
        catch { _dataError = "data_measurement_failed"; }
    }

    /// <summary>Anchors process age once, then uses a monotonic elapsed clock for subsequent samples.</summary>
    private static double InitialUptime()
    {
        try { using var process = Process.GetCurrentProcess(); return Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds); }
        catch { return 0; }
    }
}

/// <summary>Exposes native diagnostics under the existing local read boundary, with no collector configuration.</summary>
public static class DiagnosticsEndpoint
{
    public const string Route = "/api/diagnostics/v1/metrics";

    public static void Map(WebApplication app) => app.MapGet(Route, Handle).Produces<ServiceDiagnostics>();

    /// <summary>Rejects nonlocal or cross-origin callers and query parameters before measuring service resources.</summary>
    private static IResult Handle(HttpContext context, IHostEnvironment environment, LocalServiceDiagnostics diagnostics)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!TechnicalLogEndpoint.Allowed(context, environment)) return Error(403, "forbidden", "Access forbidden.");
        if (context.Request.Query.Count != 0) return Error(400, "invalid_query", "Invalid query parameters.");
        return Results.Json(diagnostics.Read(), TechnicalLogJson.Options);
    }

    private static IResult Error(int status, string code, string message) => Results.Json(new TechnicalLogError(
        "service-diagnostics/v1", "agmemory", new(code, message, false), null), TechnicalLogJson.Options, statusCode: status);
}
