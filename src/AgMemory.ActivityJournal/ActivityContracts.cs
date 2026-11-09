using System.Globalization;
using AgMemory.DesktopSupport;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgMemory.ActivityJournal;

/// <summary>Portable activity metadata. Authority and target locators are retained only in the server envelope.</summary>
public sealed record ActivityEvent
{
    public string SchemaVersion { get; init; } = "agent-activity/v1";
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
    public string JournalEpoch { get; init; } = "";
    public string Sequence { get; init; } = "0";
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RecordedAt { get; init; }
    public string Application { get; init; } = "agmemory";
    public string Source { get; init; } = "mcp";
    public string ClientKind { get; init; } = "unknown";
    public string SessionId { get; init; } = "";
    public string? AgentId { get; init; }
    public string IdentityQuality { get; init; } = "process";
    public string OperationId { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Phase { get; init; } = "started";
    public string? Outcome { get; init; }
    public double? DurationMs { get; init; }
    public int? ResultCount { get; init; }
    public string? ErrorCode { get; init; }
    public ActivityQuery Query { get; init; } = new("omitted");
    public ActivityTarget? Target { get; init; }
    public ActivityRequest? Request { get; init; }
    public ActivityResponse? Response { get; init; }
}

public sealed record ActivityRequest(string Tool, string Summary);
public sealed record ActivityResponse(string Summary, int? ResultCount = null, bool Truncated = false, string? ErrorCode = null);
public sealed record ActivityQuery(string Mode, string? Fingerprint = null, bool Truncated = false, string? Preview = null);
public sealed record ActivityTarget(string Kind = "memory", string Status = "unavailable", string? NodeKey = null, string? Generation = null, string? Label = null);
public sealed record ActivityCursor(string JournalEpoch, string Sequence);
public sealed record ActivityHealth(string Status, long Dropped, int QueueItems, long QueueBytes, string? ErrorCode);
public sealed record ActivityStatus(string SchemaVersion, string JournalEpoch, long MaxSizeBytes, long UsedBytes,
    string OldestSequence, string LatestSequence, ActivityHealth Health, ActivityCursor Cursor);
public sealed record ActivityHistory(IReadOnlyList<ActivityEvent> Events, ActivityCursor Cursor, string OldestSequence, bool HasMore, bool ResetRequired = false);

/// <summary>Internal partition and target tokens never leave the server. No memory body or durable ID is stored; explicitly enabled search previews are bounded and sanitized.</summary>
public sealed record StoredActivity(string Partition, ActivityEvent Event, string? TargetToken = null);
public sealed record ActivityRead(IReadOnlyList<StoredActivity> Events, ActivityStatus Status, bool HasMore);

/// <summary>Service settings shared by Web and MCP. Invalid limits are rejected rather than silently replaced.</summary>
public sealed record ActivityJournalOptions
{
    public const string SectionName = "ActivityJournal";
    public long MaxSizeBytes { get; init; } = 50_000_000;
    public int QueueCapacity { get; init; } = 1024;
    public long QueueMaxBytes { get; init; } = 1_048_576;
    public int BatchBytes { get; init; } = 65_536;
    public int BatchDelayMs { get; init; } = 250;
    public bool CaptureQueryPreview { get; init; } = true;

    /// <summary>Rejects quotas, queue bounds and batches that cannot honor the event reservation.</summary>
    public void Validate()
    {
        if (MaxSizeBytes is < 65_536 or > 10_000_000_000L || QueueCapacity is < 2 or > 100_000 ||
            QueueMaxBytes is < 32_768 or > 100_000_000 || BatchBytes is < 8192 or > 65_536 || BatchDelayMs is < 1 or > 1000)
            throw new ArgumentException("Invalid ActivityJournal settings.");
    }

    /// <summary>Reads the same persistent service section even when only a stdio MCP process is running.</summary>
    public static ActivityJournalOptions Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "appsettings.local.json");
        var result = new ActivityJournalOptions();
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.TryGetProperty(SectionName, out var section))
                result = section.Deserialize<ActivityJournalOptions>(ActivityJson.Options) ?? throw new ArgumentException("Invalid ActivityJournal section.");
        }
        var configured = Environment.GetEnvironmentVariable("ActivityJournal__MaxSizeBytes");
        if (configured is not null)
            result = result with { MaxSizeBytes = long.Parse(configured, CultureInfo.InvariantCulture) };
        result.Validate();
        return result;
    }
}

public static class ActivityJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static long Sequence(string value) => long.Parse(value, CultureInfo.InvariantCulture);
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Computes opaque server selectors from structured identities; fingerprints never authorize access.</summary>
public static class ActivityIdentity
{
    /// <summary>Shares the installer locator with the Web host while retaining explicit environment and existing platform defaults.</summary>
    public static string DataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("AGMEMORY_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var installation = DesktopInstallation.Read();
        if (installation is not null) return installation.DataDirectory;
        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData)) applicationData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(applicationData, "AgMemory");
    }

    public static string Partition(object actor, object scope, string storagePath) => Hash(JsonSerializer.Serialize(new { actor, scope, storagePath = Path.GetFullPath(storagePath) }));
    public static string Target(string partition, string durableId) => Convert.ToHexString(HMACSHA256.HashData(
        Convert.FromHexString(partition), Encoding.UTF8.GetBytes(durableId))).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
