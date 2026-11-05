using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public class RoamingSessionTests
{
    [Fact]
    public void Weight_UsesElapsedSecondsAndIphoneFormula()
    {
        var now = DateTimeOffset.Parse("2026-11-05T16:00:00Z");
        var record = new ReviewRecord(now.AddHours(-12), new DateOnly(2026, 11, 6), 2, 4);
        var shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        // 0.5 + 3 ln(1.5) + exp(-2) + 2(1-3/8) + 1
        Assert.Equal(4.101730607561106, record.Weight(now.AddDays(-6), now, shanghai), 12);
        Assert.Equal((2, 4), record.Counts(now, shanghai));
        Assert.Equal((0, 0), record.Counts(now.AddMinutes(-1), shanghai));
        Assert.Equal(2.5, new ReviewRecord().Weight(now.AddDays(1), now, shanghai), 12);
    }

    [Fact]
    public async Task Next_DrawsOnlyWordsAndKeepsOneExposureUntilItIsFinished()
    {
        using var fixture = new LibraryFixture();
        var stateFile = Path.Combine(fixture.Metadata, "review.json");
        var clock = new ReviewClock(DateTimeOffset.Parse("2026-11-05T15:59:00Z"));
        var notes = new[] { Note("root", "词根/act.md"), Note("guide", "学习方法.md"), Note("one", "apple.md"), Note("two", "banana.md") };
        var session = new RoamingSession(stateFile, clock, () => 0);
        var first = (await session.NextAsync(notes))!;
        Assert.Equal("one", first.Note.Id);
        Assert.False(first.RememberedToday);
        var bytes = await File.ReadAllTextAsync(stateFile);
        Assert.Equal(first, await session.NextAsync(notes));
        Assert.Equal(bytes, await File.ReadAllTextAsync(stateFile));
        var next = (await session.NextAsync(notes, first.Id, remembered: true))!;
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(first.Note.Id, next.Note.Id);
        Assert.True(next.RememberedToday);
        var afterFinish = await File.ReadAllTextAsync(stateFile);
        Assert.Equal(next, await session.NextAsync(notes, first.Id, remembered: true));
        Assert.Equal(afterFinish, await File.ReadAllTextAsync(stateFile));
        var restored = new RoamingSession(stateFile, clock, () => 1);
        Assert.Equal(next.Id, (await restored.NextAsync(notes))!.Id);
        Assert.True(restored.Current!.RememberedToday);
    }

    [Fact]
    public async Task Midnight_ResetsDailyCountsButPreservesAddedAndLastShown()
    {
        using var fixture = new LibraryFixture();
        var stateFile = Path.Combine(fixture.Metadata, "review.json");
        var clock = new ReviewClock(DateTimeOffset.Parse("2026-11-05T15:59:00Z"));
        var note = Note("one", "apple.md");
        var session = new RoamingSession(stateFile, clock, () => 0);
        var first = (await session.NextAsync([note]))!;
        var remembered = (await session.NextAsync([note], first.Id, true))!;
        Assert.True(remembered.RememberedToday);
        clock.Now = clock.Now.AddMinutes(2);
        var next = (await session.NextAsync([note], remembered.Id, false))!;
        Assert.False(next.RememberedToday);
        Assert.Equal(note.AddedAt, next.Note.AddedAt);
        // Last appearance was two minutes ago, rather than the original 30-day admission.
        Assert.InRange(next.Weight, 2.17, 2.18);
    }

    [Fact]
    public async Task PersistFailure_DoesNotCompleteOrAdvanceTheVisibleExposure()
    {
        using var fixture = new LibraryFixture();
        var stateFile = Path.Combine(fixture.Metadata, "review.json");
        var session = new RoamingSession(stateFile, new ReviewClock(DateTimeOffset.Parse("2026-11-05T15:59:00Z")), () => 0);
        var notes = new[] { Note("one", "apple.md") };
        var first = (await session.NextAsync(notes))!;
        var saved = await File.ReadAllTextAsync(stateFile);
        File.Delete(stateFile);
        Directory.CreateDirectory(stateFile);
        await Assert.ThrowsAnyAsync<IOException>(() => session.NextAsync(notes, first.Id, true));
        Assert.Equal(first, session.Current);
        Directory.Delete(stateFile);
        await File.WriteAllTextAsync(stateFile, saved);
        var next = (await session.NextAsync(notes, first.Id, true))!;
        Assert.True(next.RememberedToday);
        Assert.NotEqual(first.Id, next.Id);
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.Metadata), p => p.EndsWith(".tmp"));
    }

    [Fact]
    public async Task EmptyLibrary_DoesNotCreateExposureOrState()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "review.json");
        var session = new RoamingSession(file);
        Assert.Null(await session.NextAsync([Note("root", "词根/act.md")]));
        Assert.Null(session.Current);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData(-1, "one")]
    [InlineData(0.499, "one")]
    [InlineData(0.5, "two")]
    [InlineData(1, "two")]
    [InlineData(2, "two")]
    public async Task WeightedDraw_RespectsIntervalsAndClampsUnits(double unit, string expected)
    {
        using var fixture = new LibraryFixture();
        var session = new RoamingSession(Path.Combine(fixture.Metadata, "review.json"), new ReviewClock(DateTimeOffset.Parse("2026-11-05T16:00:00Z")), () => unit);
        var exposure = await session.NextAsync([Note("one", "apple.md"), Note("two", "banana.md")]);
        Assert.Equal(expected, exposure!.Note.Id);
    }

    [Fact]
    public async Task ConcurrentCompletionOfOneExposure_CountsOnlyOnce()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "review.json");
        var session = new RoamingSession(file, new ReviewClock(DateTimeOffset.Parse("2026-11-05T16:00:00Z")), () => 0);
        var notes = new[] { Note("one", "apple.md") };
        var first = (await session.NextAsync(notes))!;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => session.NextAsync(notes, first.Id, true)));
        Assert.Single(results.Select(result => result!.Id).Distinct());
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal(1, json.RootElement.GetProperty("Records").GetProperty("one").GetProperty("Remembered").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("Records").GetProperty("one").GetProperty("Skipped").GetInt32());
    }

    [Fact]
    public async Task CorruptHistory_IsReportedWithoutReplacingIt()
    {
        using var fixture = new LibraryFixture();
        Directory.CreateDirectory(fixture.Metadata);
        var file = Path.Combine(fixture.Metadata, "review.json");
        await File.WriteAllTextAsync(file, "broken json");
        Assert.Throws<IOException>(() => new RoamingSession(file));
        Assert.Equal("broken json", await File.ReadAllTextAsync(file));
    }

    private static LibraryNote Note(string id, string path) => new(id, path, path, "# word", DateTimeOffset.Parse("2026-10-06T16:00:00Z"));

    private sealed class ReviewClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    }
}
