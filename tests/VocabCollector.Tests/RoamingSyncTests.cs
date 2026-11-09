using System.Text.Json;
using VocabCollector.Core.Library;
using VocabCollector.Core.Sync;

namespace VocabCollector.Tests;

public sealed class RoamingSyncTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T04:00:00Z");
    private static LibraryNote Note(string id) => new(id, "apple.md", "apple.md", "# apple", Now.AddDays(-30));
    private static string? ToCloud(string local) => local is "local-a" or "local-b" ? "cloud-note" : null;
    private static string? ToLocal(string cloud) => cloud == "cloud-note" ? "local-b" : null;

    [Fact]
    public async Task CrossDeviceRestoreKeepsExposureAndDoesNotRecordAnotherVisit()
    {
        using var fixture = new LibraryFixture();
        var a = new RoamingSession(Path.Combine(fixture.Metadata, "a.json"), new SyncClock(Now), () => 0);
        var first = (await a.NextAsync([Note("local-a")]))!;
        var entries = await a.ExportSyncAsync("device-a", ToCloud);
        Assert.Single(entries, entry => entry.Key.EndsWith("/shown"));
        var b = new RoamingSession(Path.Combine(fixture.Metadata, "b.json"), new SyncClock(Now), () => 0);
        var changes = 0;
        b.StateChanged += (_, _) => changes++;
        await b.MergeSyncAsync(entries, ToLocal);
        var restored = (await b.RestoreCurrentAsync([Note("local-b")]))!;
        Assert.Equal(first.Id, restored.Id);
        Assert.Equal(first.Weight, restored.Weight);
        Assert.Equal(first.WeightLevel, restored.WeightLevel);
        Assert.Equal(0, changes);
        Assert.Equal(1, Today(b, "local-b"));
        await b.MergeSyncAsync(entries, ToLocal);
        Assert.Equal(1, Today(b, "local-b"));
        await b.NextAsync([Note("local-b")], restored.Id, true);
        Assert.Equal(1, changes);
        Assert.Equal(2, Today(b, "local-b"));
        var back = await b.ExportSyncAsync("device-b", ToCloud);
        await a.MergeSyncAsync(back, cloud => cloud == "cloud-note" ? "local-a" : null);
        Assert.Equal(2, Today(a, "local-a"));
        Assert.True((await a.NextAsync([Note("local-a")]))!.RememberedToday);
    }

    [Fact]
    public async Task RestoreCurrentOnAnEmptySessionDoesNotDrawOrCreateState()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "empty.json");
        var session = new RoamingSession(file, new SyncClock(Now), () => 0);
        Assert.Null(await session.RestoreCurrentAsync([Note("local-a")]));
        Assert.False(File.Exists(file));
        Assert.Equal(0, Today(session, "local-a"));
    }

    [Fact]
    public async Task LegacyBaselineIsFrozenOnceAndRepeatedMergesDoNotDoubleCount()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "legacy.json");
        Directory.CreateDirectory(fixture.Metadata);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            Records = new Dictionary<string, ReviewRecord> { ["local-a"] = new(Now, new(2026, 11, 7), 3, 2) },
            CurrentExposure = (Guid?)null, CurrentNoteId = (string?)null,
            Activity = new Dictionary<DateOnly, Dictionary<string, int>> { [new(2026, 11, 7)] = new() { ["local-a"] = 5 } }
        }));
        var a = new RoamingSession(file, new SyncClock(Now), () => 0);
        var original = await a.ExportSyncAsync("device-a", ToCloud);
        var baseline = Assert.Single(original);
        Assert.StartsWith("legacy/device-a/cloud-note/", baseline.Key);
        var b = new RoamingSession(Path.Combine(fixture.Metadata, "b.json"), new SyncClock(Now), () => 0);
        await b.MergeSyncAsync(original, ToLocal);
        await b.MergeSyncAsync(original, ToLocal);
        Assert.Equal(5, Today(b, "local-b"));
        await a.NextAsync([Note("local-a")]);
        var exportedAgain = await a.ExportSyncAsync("replacement-device-id", ToCloud);
        Assert.Single(exportedAgain, entry => entry.Key.StartsWith("legacy/"));
        Assert.Equal(baseline.Value.GetRawText(), exportedAgain.Single(entry => entry.Key.StartsWith("legacy/")).Value.GetRawText());
        await b.MergeSyncAsync(exportedAgain, ToLocal);
        Assert.Equal(6, Today(b, "local-b"));
        var restart = new RoamingSession(file, new SyncClock(Now), () => 0);
        var afterRestart = await restart.ExportSyncAsync("third-device", ToCloud);
        Assert.Single(afterRestart, entry => entry.Key.StartsWith("legacy/device-a/"));
    }

    [Fact]
    public async Task ALocalActionDuringNetworkRoundTripSurvivesMergingAnOlderCurrentPointer()
    {
        using var fixture = new LibraryFixture();
        var clock = new SyncClock(Now);
        var session = new RoamingSession(Path.Combine(fixture.Metadata, "a.json"), clock, () => 0);
        var first = (await session.NextAsync([Note("local-a")]))!;
        var stale = await session.ExportSyncAsync("device-a", ToCloud);
        clock.Now = clock.Now.AddSeconds(1);
        var newer = (await session.NextAsync([Note("local-a")], first.Id, true))!;
        await session.MergeSyncAsync(stale, cloud => cloud == "cloud-note" ? "local-a" : null);
        Assert.Equal(newer.Id, (await session.NextAsync([Note("local-a")]))!.Id);
        Assert.Equal(2, Today(session, "local-a"));
        Assert.Single(await session.ExportSyncAsync("device-a", ToCloud), entry => entry.Key.EndsWith("/completed"));
    }

    [Fact]
    public async Task ServerCompletionIsAuthoritativeAndOneExposureCanOnlyCountOnce()
    {
        using var fixture = new LibraryFixture();
        var session = new RoamingSession(Path.Combine(fixture.Metadata, "a.json"), new SyncClock(Now), () => 0);
        var first = (await session.NextAsync([Note("local-a")]))!;
        await session.NextAsync([Note("local-a")], first.Id, true);
        var server = new SyncStateEntry($"event/{first.Id:D}/completed", JsonSerializer.SerializeToElement(new
        { noteId = "cloud-note", at = Now, day = "2026-11-07", remembered = false }));
        await session.MergeSyncAsync([server, server], cloud => cloud == "cloud-note" ? "local-a" : null);
        Assert.False((await session.NextAsync([Note("local-a")]))!.RememberedToday);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Metadata, "a.json")));
        var record = json.RootElement.GetProperty("Records").GetProperty("local-a");
        Assert.Equal(0, record.GetProperty("Remembered").GetInt32());
        Assert.Equal(1, record.GetProperty("Skipped").GetInt32());
        Assert.Equal(2, Today(session, "local-a"));
    }

    [Fact]
    public async Task FailedLocalSaveDoesNotEmitAnEventOrNotifySync()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "a.json");
        var session = new RoamingSession(file, new SyncClock(Now), () => 0);
        var first = (await session.NextAsync([Note("local-a")]))!;
        var notifications = 0;
        session.StateChanged += (_, _) => notifications++;
        var saved = await File.ReadAllTextAsync(file);
        File.Delete(file); Directory.CreateDirectory(file);
        await Assert.ThrowsAnyAsync<IOException>(() => session.NextAsync([Note("local-a")], first.Id, true));
        Assert.Equal(0, notifications);
        Assert.Equal(first.Id, session.Current?.Id);
        Directory.Delete(file); await File.WriteAllTextAsync(file, saved);
        var events = await session.ExportSyncAsync("device-a", ToCloud);
        Assert.DoesNotContain(events, entry => entry.Key.EndsWith("/completed"));
    }

    private static int Today(RoamingSession session, string localId) =>
        session.GetStatistics([Note(localId)]).DayCounts.Single(day => day.Day == new DateOnly(2026, 11, 7)).Count;
    private sealed class SyncClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    }
}
