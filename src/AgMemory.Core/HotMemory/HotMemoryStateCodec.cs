using System.Text.Json;
using AgMemory.Contracts;

namespace AgMemory.Core;

internal static class HotMemoryStateCodec
{
    private const string Prefix = "agmemory-hot-v1:";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Encode(HotMemoryState state)
    {
        state.Validate();
        return Prefix + JsonSerializer.Serialize(state, JsonOptions);
    }

    public static bool TryDecode(string content, out HotMemoryState? state)
    {
        state = null;
        if (!content.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<HotMemoryState>(content[Prefix.Length..], JsonOptions);
            if (parsed is null) return false;
            parsed.Validate();
            state = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool TryRender(string content, out string rendered)
    {
        rendered = content;
        if (!TryDecode(content, out var state) || state is null) return false;
        rendered = string.Join("\n", state.Entries
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(RenderEntry));
        return true;
    }

    public static string RenderEntry(HotMemoryEntry entry) => $"{Label(entry.Kind)}: {entry.Content}";

    private static string Label(HotMemoryEntryKind kind) => kind switch
    {
        HotMemoryEntryKind.CurrentGoal => "Current goal",
        HotMemoryEntryKind.ActiveEntity => "Active entity",
        HotMemoryEntryKind.RecentDecision => "Recent decision",
        HotMemoryEntryKind.OpenQuestion => "Open question",
        HotMemoryEntryKind.WorkingFact => "Working fact",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
