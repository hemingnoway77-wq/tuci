using System.Text.Json;
using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public sealed class RoamingStatisticsTests
{
    [Fact]
    public async Task EachNewExposureAddsActivityButRestorePreviewAndRepeatedCompletionDoNot()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "roaming.json");
        var clock = Clock("2026-11-06T04:00:00Z");
        var notes = Notes(clock.Now);
        var session = new RoamingSession(path, clock, () => 0);
        var empty = session.GetStatistics(notes);
        Assert.Equal(2, empty.TotalWords);
        Assert.Equal(0, empty.WeekReviewed);
        Assert.All(empty.DayCounts, day => Assert.Equal(0, day.Count));
        var first = (await session.NextAsync(notes))!;
        AssertToday(session, notes, 1, 1);
        Assert.Equal(first.Id, (await session.NextAsync(notes))!.Id);
        AssertToday(session, notes, 1, 1);
        var reloaded = new RoamingSession(path, clock, () => 0);
        Assert.Equal(first.Id, (await reloaded.NextAsync(notes))!.Id);
        AssertToday(reloaded, notes, 1, 1);
        var next = (await reloaded.NextAsync(notes, first.Id, true))!;
        AssertToday(reloaded, notes, 2, 1);
        Assert.Equal(next.Id, (await reloaded.NextAsync(notes, first.Id, true))!.Id);
        AssertToday(reloaded, notes, 2, 1);
        Assert.Equal(1, reloaded.GetStatistics(notes).WeekReviewed);
        Assert.Equal(1, reloaded.GetStatistics(notes).MonthReviewed);
        Assert.Equal(0.5, reloaded.GetStatistics(notes).WeekCoverage);
    }

    [Fact]
    public async Task NaturalWeekAndMonthUseLocalDateAndCoverageOnlyCountsCurrentWordIds()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "roaming.json");
        await Seed(path, new Dictionary<string, object>
        {
            ["2026-12-27"] = new { three = 4 },
            ["2026-12-28"] = new { one = 2 },
            ["2026-12-31"] = new { two = 1, removed = 3 },
            ["2027-01-01"] = new { one = 3, removed = 1 }
        });
        var clock = Clock("2026-12-31T16:00:00Z"); // Friday Jan 1 in Shanghai.
        var session = new RoamingSession(path, clock);
        var notes = Notes(clock.Now).Concat(new[] { Note("three", "cherry.md", clock.Now) }).ToArray();
        var stats = session.GetStatistics(notes);
        Assert.Equal(new DateOnly(2027, 1, 1), stats.Today);
        Assert.Equal(new DateOnly(2026, 12, 28), stats.WeekStart);
        Assert.Equal(new DateOnly(2027, 1, 1), stats.MonthStart);
        Assert.Equal(new DateOnly(2026, 10, 12), stats.GridStart);
        Assert.Equal(3, stats.TotalWords);
        Assert.Equal(2, stats.WeekReviewed);
        Assert.Equal(1, stats.MonthReviewed);
        Assert.Equal(2.0 / 3, stats.WeekCoverage);
        Assert.Equal(84, stats.DayCounts.Count);
        var today = Assert.Single(stats.DayCounts, day => day.Day == stats.Today);
        Assert.Equal(4, today.Count);
        Assert.Equal(2, today.UniqueWords);
        Assert.False(today.IsFuture);
        Assert.Equal(2, stats.DayCounts.Count(day => day.IsFuture));
        Assert.All(stats.DayCounts.Where(day => day.IsFuture), day => Assert.Equal(0, day.Count));
        var afterRemoval = session.GetStatistics(notes.Where(note => note.Id != "one").ToArray());
        Assert.Equal(2, afterRemoval.TotalWords);
        Assert.Equal(1, afterRemoval.WeekReviewed);
        Assert.Equal(0, afterRemoval.MonthReviewed);
        Assert.Equal(4, Assert.Single(afterRemoval.DayCounts, day => day.Day == stats.Today).Count);
    }

    [Fact]
    public async Task LegacyLastShownDoesNotInventHistoricalActivityAndGapSurvivesMigration()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "roaming.json");
        var clock = Clock("2026-11-06T04:00:00Z");
        Directory.CreateDirectory(fixture.Metadata);
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Records = new { one = new ReviewRecord(clock.Now.AddDays(-1), new(2026, 11, 5), 5, 2) },
            CurrentExposure = id, CurrentNoteId = "one"
        }));
        var original = await File.ReadAllTextAsync(path);
        var session = new RoamingSession(path, clock, () => 0);
        var notes = Notes(clock.Now);
        var stats = session.GetStatistics(notes);
        Assert.True(stats.HasLegacyHistory);
        Assert.Equal(0, stats.WeekReviewed);
        Assert.All(stats.DayCounts, day => Assert.Equal(0, day.Count));
        Assert.Equal(id, (await session.NextAsync(notes))!.Id);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        await session.NextAsync(notes, id, false);
        AssertToday(session, notes, 1, 1);
        Assert.Equal(new DateOnly(2026, 11, 6), session.GetStatistics(notes).TrackingStartedOn);
        Assert.True(new RoamingSession(path, clock).GetStatistics(notes).HasLegacyHistory);
    }

    [Fact]
    public async Task FailedAtomicSaveDoesNotRecordAVisitOrCompleteTheCurrentExposure()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "roaming.json");
        var clock = Clock("2026-11-06T04:00:00Z");
        var session = new RoamingSession(path, clock, () => 0);
        var notes = Notes(clock.Now);
        var first = (await session.NextAsync(notes))!;
        var original = await File.ReadAllTextAsync(path);
        File.Delete(path); Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<IOException>(() => session.NextAsync(notes, first.Id, true));
        Assert.Equal(first, session.Current);
        AssertToday(session, notes, 1, 1);
        Directory.Delete(path); await File.WriteAllTextAsync(path, original);
        var simultaneous = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => session.NextAsync(notes, first.Id, true)));
        Assert.Single(simultaneous.Select(exposure => exposure!.Id).Distinct());
        AssertToday(session, notes, 2, 1);
        AssertToday(new RoamingSession(path, clock), notes, 2, 1);
    }

    [Fact]
    public async Task ActivityMovesAcrossMidnightAndReadOnlyStatisticsNeverCreateState()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "roaming.json");
        var clock = Clock("2026-11-06T15:59:00Z");
        var session = new RoamingSession(path, clock, () => 0);
        var notes = Notes(clock.Now);
        Assert.All(session.GetStatistics([]).DayCounts, day => Assert.Equal(0, day.Count));
        Assert.False(File.Exists(path));
        var first = (await session.NextAsync(notes))!;
        clock.Now = clock.Now.AddMinutes(2);
        await session.NextAsync(notes, first.Id, false);
        var stats = session.GetStatistics(notes);
        Assert.Equal(new DateOnly(2026, 11, 7), stats.Today);
        Assert.Equal(1, Assert.Single(stats.DayCounts, day => day.Day == new DateOnly(2026, 11, 6)).Count);
        AssertToday(session, notes, 1, 1);
        Assert.Equal(1, stats.WeekReviewed);
    }

    [Fact]
    public async Task RelativeWeightIsFixedAtDrawAndRestoredWithoutRecalculationOrAnotherVisit()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "roaming.json");
        var clock = Clock("2026-11-06T04:00:00Z");
        Directory.CreateDirectory(fixture.Metadata);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Records = new { two = new ReviewRecord(clock.Now, new(2026, 11, 6), 0, 3) }, CurrentExposure = (Guid?)null, CurrentNoteId = (string?)null
        }));
        var notes = Notes(clock.Now);
        var first = (await new RoamingSession(path, clock, () => 0).NextAsync(notes))!;
        Assert.Equal(2.5, first.Weight, 12);
        Assert.Equal(0.6097560975609756, first.WeightLevel, 12); // 2.5 / 4.1
        clock.Now = clock.Now.AddDays(1);
        var restored = new RoamingSession(path, clock, () => 1);
        var same = (await restored.NextAsync(notes))!;
        Assert.Equal(first.Id, same.Id);
        Assert.Equal(first.Weight, same.Weight);
        Assert.Equal(first.WeightLevel, same.WeightLevel);
        AssertToday(restored, notes, 0, 0);
    }

    [Fact]
    public async Task ActivityRetentionDropsDatesOlderThan400DaysDuringTheNextCommit()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "roaming.json");
        var clock = Clock("2026-11-06T04:00:00Z");
        var today = new DateOnly(2026, 11, 6);
        var activity = Enumerable.Range(0, 410).ToDictionary(index => today.AddDays(-index).ToString("yyyy-MM-dd"), _ => (object)new { one = 1 });
        await Seed(path, activity);
        await new RoamingSession(path, clock, () => 0).NextAsync(Notes(clock.Now));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var saved = json.RootElement.GetProperty("Activity");
        Assert.Equal(400, saved.EnumerateObject().Count());
        Assert.True(saved.TryGetProperty(today.AddDays(-399).ToString("yyyy-MM-dd"), out _));
        Assert.False(saved.TryGetProperty(today.AddDays(-400).ToString("yyyy-MM-dd"), out _));
    }

    [Theory]
    [InlineData("{\"2026-11-06\":{\"one\":-1}}")]
    [InlineData("{\"2026-11-06\":{\"one\":0}}")]
    [InlineData("{\"2026-11-06\":{\"one\":1.5}}")]
    [InlineData("{\"not-a-date\":{\"one\":1}}")]
    [InlineData("{\"2026-11-06\":null}")]
    [InlineData("{\"2026-11-06\":{\"\":1}}")]
    [InlineData("{\"2026-11-06\":{\"one\":2147483647,\"two\":1}}")]
    public async Task InvalidActivityIsReportedWithoutOverwritingTheHistory(string activity)
    {
        using var fixture = new LibraryFixture();
        Directory.CreateDirectory(fixture.Metadata);
        var path = Path.Combine(fixture.Metadata, "roaming.json");
        var original = "{\"Records\":{},\"CurrentExposure\":null,\"CurrentNoteId\":null,\"Activity\":" + activity + "}";
        await File.WriteAllTextAsync(path, original);
        Assert.Throws<IOException>(() => new RoamingSession(path));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    private static async Task Seed(string path, Dictionary<string, object> activity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Records = new { }, CurrentExposure = (Guid?)null, CurrentNoteId = (string?)null, Activity = activity }));
    }
    private static void AssertToday(RoamingSession session, IReadOnlyList<LibraryNote> notes, int count, int unique)
    {
        var stats = session.GetStatistics(notes);
        var day = Assert.Single(stats.DayCounts, item => item.Day == stats.Today);
        Assert.Equal(count, day.Count); Assert.Equal(unique, day.UniqueWords);
    }
    private static LibraryNote[] Notes(DateTimeOffset added) =>
        [Note("one", "apple.md", added), Note("two", "banana.md", added), Note("root", "词根/act.md", added), Note("guide", "学习方法.md", added)];
    private static LibraryNote Note(string id, string path, DateTimeOffset added) => new(id, path, path, "# note", added);
    private static StatisticsClock Clock(string utc) => new(DateTimeOffset.Parse(utc));
    private sealed class StatisticsClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    }
}
