using System.Buffers;
using System.Globalization;
using System.Text;

namespace AgMemory.Web.Features.AgentActivity;

/// <summary>Parses only the portable HTTP query grammar; old mini-chat parsing remains unchanged.</summary>
internal static class ActivityHttpQueryParser
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    { "limit", "journalEpoch", "afterSequence", "beforeSequence", "sessionId", "operation", "q", "projectId" };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Rejects duplicate/unknown keys, noncanonical integers and malformed Unicode before any event read.</summary>
    public static bool TryParse(HttpRequest request, out ActivityHttpQuery? result)
    {
        result = null;
        if (!ValidWireUnicode(request.QueryString.Value ?? "")) return false;
        var query = request.Query;
        foreach (var pair in query)
            if (!Keys.Contains(pair.Key) || pair.Value.Count != 1) return false;
        var limit = 100;
        if (query.TryGetValue("limit", out var limitText) &&
            (!int.TryParse(limitText.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
             limit is < 1 or > 100 || limit.ToString(CultureInfo.InvariantCulture) != limitText.ToString())) return false;
        var hasAfter = query.ContainsKey("afterSequence");
        var hasBefore = query.ContainsKey("beforeSequence");
        var hasEpoch = query.ContainsKey("journalEpoch");
        if (hasAfter && hasBefore || hasEpoch != (hasAfter || hasBefore)) return false;
        long? after = null;
        long? before = null;
        if (hasAfter) { if (!TrySequence(query["afterSequence"].ToString(), out var value)) return false; after = value; }
        if (hasBefore) { if (!TrySequence(query["beforeSequence"].ToString(), out var value)) return false; before = value; }
        var epoch = query["journalEpoch"].FirstOrDefault();
        if (hasEpoch && (string.IsNullOrEmpty(epoch) || !ValidScalars(epoch, 128))) return false;
        var session = Filter(query["sessionId"].FirstOrDefault());
        var operation = Filter(query["operation"].FirstOrDefault());
        var text = Filter(query["q"].FirstOrDefault());
        var project = Filter(query["projectId"].FirstOrDefault());
        if (!ValidScalars(session, 128) || !ValidScalars(operation, 128) || !ValidScalars(project, 128) || !ValidScalars(text, 512)) return false;
        result = new(limit, epoch, after, before, session, operation, text, project);
        return true;
    }

    private static string? Filter(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Keeps sequences exact and bounded to signed Int64 in both runtimes.</summary>
    internal static bool TrySequence(string value, out long sequence) => long.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out sequence) && sequence >= 0 && sequence.ToString(CultureInfo.InvariantCulture) == value;

    /// <summary>Counts Unicode scalars rather than UTF-16 code units and rejects unpaired surrogates.</summary>
    internal static bool ValidScalars(string? value, int maximum)
    {
        if (value is null) return true;
        var position = 0;
        var count = 0;
        while (position < value.Length)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(position), out _, out var consumed) != OperationStatus.Done || ++count > maximum) return false;
            position += consumed;
        }
        return true;
    }

    /// <summary>Checks percent-decoded bytes before ASP.NET's permissive query decoder can replace invalid UTF-8.</summary>
    private static bool ValidWireUnicode(string raw)
    {
        var bytes = new List<byte>(raw.Length);
        Span<byte> encoded = stackalloc byte[4];
        for (var position = 0; position < raw.Length; position++)
        {
            if (raw[position] == '%')
            {
                if (position + 2 >= raw.Length || !byte.TryParse(raw.AsSpan(position + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var escaped)) return false;
                bytes.Add(escaped); position += 2;
            }
            else if (raw[position] <= 127) bytes.Add((byte)raw[position]);
            else
            {
                if (Rune.DecodeFromUtf16(raw.AsSpan(position), out var rune, out var consumed) != OperationStatus.Done) return false;
                var length = rune.EncodeToUtf8(encoded);
                for (var index = 0; index < length; index++) bytes.Add(encoded[index]);
                position += consumed - 1;
            }
        }
        try { StrictUtf8.GetCharCount(bytes.ToArray()); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}
