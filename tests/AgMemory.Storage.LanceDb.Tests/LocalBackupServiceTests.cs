using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace AgMemory.Storage.LanceDb.Tests;

/// <summary>Exercises real ZIP round trips, failed publication and bounded retention in temporary stores only.</summary>
public sealed class LocalBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "agmemory-backup-tests-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Backups => Path.Combine(_root, "backups");

    /// <summary>Creates minimal local data including the private citation key and shared knowledge sidecar.</summary>
    private LocalBackupService Service()
    {
        Directory.CreateDirectory(Path.Combine(Data, "memory-graph.lancedb", ".agmemory-knowledge"));
        File.WriteAllText(Path.Combine(Data, "memory-graph.lancedb", "fixture.lance"), "durable memory");
        File.WriteAllBytes(Path.Combine(Data, "memory-graph.lancedb", ".agmemory-agent-key"), Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        File.WriteAllText(Path.Combine(Data, "memory-graph.lancedb", ".agmemory-knowledge", "team.json"), "team context");
        File.WriteAllText(Path.Combine(Data, "appsettings.local.json"), "{}");
        return new(Data, Backups);
    }

    /// <summary>A verified daily ZIP restores all bytes including hidden authority files, and same-day delivery never overwrites the recovery point.</summary>
    [Fact]
    public async Task DailyRoundTripPreservesDataAndDoesNotReplaceSameDayArchive()
    {
        var service = Service();
        var date = new DateTimeOffset(2026, 10, 21, 3, 0, 0, TimeSpan.FromHours(3));
        var first = await service.CreateAsync(date);
        Assert.True(first.Created);
        Assert.Equal("21102026.agmemmory.zip", Path.GetFileName(first.ArchivePath));
        var bytes = await File.ReadAllBytesAsync(first.ArchivePath);
        File.WriteAllText(Path.Combine(Data, "memory-graph.lancedb", "fixture.lance"), "newer live state");
        var replay = await service.CreateAsync(date.AddHours(10));
        Assert.False(replay.Created);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.ArchivePath));
        var restored = Path.Combine(_root, "restored");
        var manifest = await LocalBackupService.RestoreAsync(first.ArchivePath, restored);
        Assert.Equal(4, manifest.Files.Count);
        Assert.Equal("durable memory", File.ReadAllText(Path.Combine(restored, "memory-graph.lancedb", "fixture.lance")));
        Assert.Equal("team context", File.ReadAllText(Path.Combine(restored, "memory-graph.lancedb", ".agmemory-knowledge", "team.json")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Data, "memory-graph.lancedb", ".agmemory-agent-key")), File.ReadAllBytes(Path.Combine(restored, "memory-graph.lancedb", ".agmemory-agent-key")));
        Assert.False(File.Exists(Path.Combine(restored, "memory-graph.lancedb", ".agmemory-store.lock")));
        Assert.Equal("newer live state", File.ReadAllText(Path.Combine(Data, "memory-graph.lancedb", "fixture.lance")));
        await Assert.ThrowsAsync<IOException>(() => LocalBackupService.RestoreAsync(first.ArchivePath, Data));
    }

    /// <summary>A complete three-week sequence retains the two last days and both weekly anchors, while manual/corrupt files survive cleanup.</summary>
    [Fact]
    public async Task RetentionKeepsDailyAndWeeklyRecoveryPointsAndPreservesUnmanagedFiles()
    {
        var service = Service();
        Directory.CreateDirectory(Backups);
        var manual = Path.Combine(Backups, "manual.zip");
        var corrupt = Path.Combine(Backups, "01012026.agmemmory.zip");
        File.WriteAllText(manual, "manual"); File.WriteAllText(corrupt, "damaged");
        for (var day = 1; day <= 21; day++) await service.CreateAsync(new(2026, 10, day, 3, 0, 0, TimeSpan.Zero));
        var names = Directory.GetFiles(Backups, "*" + LocalBackupService.Extension).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "01012026.agmemmory.zip", "12102026.agmemmory.zip", "19102026.agmemmory.zip", "20102026.agmemmory.zip", "21102026.agmemmory.zip" }, names);
        Assert.True(File.Exists(manual));
        Assert.Empty(Directory.GetDirectories(Backups, ".snapshot-*"));
        Assert.Empty(Directory.GetFiles(Backups, "*.tmp"));
    }

    /// <summary>Week boundaries use Monday through Sunday, preserve a fallback after offline weeks and never invent missing historical backups.</summary>
    [Fact]
    public void RetentionHandlesRolloverAndOfflineGaps()
    {
        var dates = new[] { new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26) };
        Assert.True(dates.Skip(1).ToHashSet().SetEquals(LocalBackupService.RetainedDates(dates, new(2026, 10, 26))));
        var gap = new[] { new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 21) };
        Assert.True(gap.ToHashSet().SetEquals(LocalBackupService.RetainedDates(gap, new(2026, 10, 21))));
        Assert.Empty(LocalBackupService.RetainedDates([], new(2026, 10, 21)));
    }

    /// <summary>Damaged payloads, ZIP slip, duplicate names, cancelled work and linked source roots never publish restored or replacement data.</summary>
    [Theory]
    [InlineData("checksum")]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    public async Task UnsafeArchiveLeavesDestinationAbsent(string fault)
    {
        var result = await Service().CreateAsync(new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero));
        using (var zip = ZipFile.Open(result.ArchivePath, ZipArchiveMode.Update))
        {
            if (fault == "checksum")
            {
                zip.GetEntry("data/memory-graph.lancedb/fixture.lance")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("data/memory-graph.lancedb/fixture.lance").Open());
                writer.Write("broken memory!");
            }
            else if (fault == "traversal") zip.CreateEntry("../escape");
            else zip.CreateEntry("manifest.json");
        }
        var target = Path.Combine(_root, "unsafe-restore");
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => LocalBackupService.RestoreAsync(result.ArchivePath, target));
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(_root, "escape")));
        Assert.Empty(Directory.GetDirectories(_root, ".agmemory-restore-*"));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => new LocalBackupService(Data, Backups).CreateAsync(new(2026, 10, 21, 4, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>Cancellation and unsafe source layout leave earlier archives intact, and concurrent same-day attempts publish exactly once.</summary>
    [Fact]
    public async Task FailedOrConcurrentPublicationCannotDestroyEarlierRecoveryPoint()
    {
        var service = Service();
        var day = new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);
        var attempts = await Task.WhenAll(service.CreateAsync(day), new LocalBackupService(Data, Backups).CreateAsync(day));
        Assert.Single(attempts, item => item.Created);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(day.AddDays(1), cancelled.Token));
        Assert.Single(Directory.GetFiles(Backups, "*" + LocalBackupService.Extension));
        Assert.Throws<ArgumentException>(() => new LocalBackupService(Data, Path.Combine(Data, "nested")));
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(Data, "linked"), Path.Combine(Data, "appsettings.local.json"));
            await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(day.AddDays(1)));
            Assert.Single(Directory.GetFiles(Backups, "*" + LocalBackupService.Extension));
        }
    }

    /// <summary>A cooperating live store lease delays snapshot publication until the writer's critical section has finished.</summary>
    [Fact]
    public async Task SnapshotWaitsForLiveStoreLease()
    {
        var service = Service();
        var lease = new FileStream(Path.Combine(Data, "memory-graph.lancedb", ".agmemory-store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task<LocalBackupResult>? pending = null;
        try
        {
            pending = service.CreateAsync(new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero));
            await Task.Delay(100);
            Assert.False(pending.IsCompleted);
            File.WriteAllText(Path.Combine(Data, "memory-graph.lancedb", "fixture.lance"), "committed state");
        }
        finally { lease.Dispose(); }
        Assert.NotNull(pending);
        var archive = await pending;
        var destination = Path.Combine(_root, "after-writer");
        await LocalBackupService.RestoreAsync(archive.ArchivePath, destination);
        Assert.Equal("committed state", File.ReadAllText(Path.Combine(destination, "memory-graph.lancedb", "fixture.lance")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
