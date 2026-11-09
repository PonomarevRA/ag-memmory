using System.Globalization;

namespace AgMemory.Storage.LanceDb;

public sealed partial class LocalBackupService
{
    /// <summary>Retains two latest successful days and first successful points of current/previous ISO weeks; dates use local calendar time.</summary>
    public static IReadOnlySet<DateOnly> RetainedDates(IEnumerable<DateOnly> availableDates, DateOnly today)
    {
        var dates = availableDates.Where(date => date <= today).Distinct().Order().ToArray();
        var keep = new HashSet<DateOnly>(dates.TakeLast(2));
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        foreach (var week in new[] { monday.AddDays(-7), monday })
        {
            var anchor = dates.FirstOrDefault(date => date >= week && date < week.AddDays(7));
            if (anchor != default) keep.Add(anchor);
        }
        // An offline week must not cause the last older recovery point to vanish just because Monday had no successful run.
        if (!dates.Any(date => date >= monday.AddDays(-7) && date < monday))
        {
            var fallback = dates.LastOrDefault(date => date < monday.AddDays(-7));
            if (fallback != default) keep.Add(fallback);
        }
        return keep;
    }

    /// <summary>Only fully verified managed ZIPs are candidates; corrupt, manual, future, linked and unfamiliar files remain untouched.</summary>
    private async Task<int> PruneAsync(DateOnly today, CancellationToken token)
    {
        var managed = new Dictionary<DateOnly, string>();
        foreach (var path in Directory.EnumerateFiles(_backupDirectory, "*" + Extension))
        {
            if (new FileInfo(path).LinkTarget is not null) continue;
            var name = Path.GetFileName(path);
            if (name.Length != 8 + Extension.Length || !DateOnly.TryParseExact(name[..8], "ddMMyyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) || date > today) continue;
            try
            {
                var manifest = await VerifyArchiveAsync(path, null, token).ConfigureAwait(false);
                if (manifest.LocalDate == date) managed.Add(date, path);
            }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or IOException)
            { /* An unreadable or damaged archive is never authorization to delete other recovery points. */ }
        }
        var keep = RetainedDates(managed.Keys, today);
        var deleted = 0;
        foreach (var pair in managed)
        {
            if (keep.Contains(pair.Key)) continue;
            token.ThrowIfCancellationRequested();
            File.Delete(pair.Value);
            deleted++;
        }
        return deleted;
    }
}
