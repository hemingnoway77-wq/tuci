using System.Text.Json;
using VocabCollector.Core.Library;
using VocabCollector.Core.Sync;

namespace VocabCollector.Tests;

public sealed class RoamingPracticeWeightTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T04:00:00Z");
    private static LibraryNote Note(string id, string word) => new(id, word + ".md", word + ".md", "# " + word, Now.AddDays(-30));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GradedPracticeUsesTheExistingRememberedAndSkippedWeightFormula(bool remembered)
    {
        using var fixture = new LibraryFixture();
        var clock = new PracticeClock(Now);
        var path = Path.Combine(fixture.Metadata, "practice.json");
        var practice = new RoamingSession(path, clock, () => 0);
        var standard = new RoamingSession(Path.Combine(fixture.Metadata, "standard.json"), clock, () => 0);
        var note = Note("one", "apple");
        var shown = (await practice.NextAsync([note]))!;
        var standardShown = (await standard.NextAsync([note]))!;
        var next = (await practice.NextPracticeAsync([note], shown.Id, remembered))!;
        var standardNext = (await standard.NextAsync([note], standardShown.Id, remembered))!;
        var record = await ReadRecord(path, note.Id);
        Assert.Equal(remembered ? (1, 0) : (0, 1), record.Counts(Now, TimeZoneInfo.Utc));
        Assert.Equal(standardNext.Weight, next.Weight, 12);
        Assert.Equal(record.Weight(note.AddedAt, Now, TimeZoneInfo.Utc), next.Weight, 12);
        Assert.Equal(remembered, next.RememberedToday);
        Assert.NotEqual(shown.Id, next.Id);
        Assert.Equal(2, Today(practice, [note]));
        var completion = Assert.Single(await practice.ExportSyncAsync("practice", _ => "cloud-one"), entry => entry.Key.EndsWith("/completed"));
        Assert.Equal(remembered, completion.Value.GetProperty("remembered").GetBoolean());
        AccountStateSync.Validate(completion);
    }

    [Fact]
    public async Task NeutralPracticeDoesNotLowerWeightOrChangeEitherGradeAndStillRecordsTheNextAppearance()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "neutral.json");
        var clock = new PracticeClock(Now);
        var session = new RoamingSession(path, clock, () => 0);
        var note = Note("one", "apple");
        var shown = (await session.NextAsync([note]))!;
        var before = await ReadRecord(path, note.Id);
        var changes = 0; session.StateChanged += (_, _) => changes++;
        var next = (await session.NextPracticeAsync([note], shown.Id, remembered: null))!;
        Assert.Equal(before, await ReadRecord(path, note.Id));
        Assert.Equal((0, 0), (await ReadRecord(path, note.Id)).Counts(Now, TimeZoneInfo.Utc));
        Assert.Equal(before.Weight(note.AddedAt, Now, TimeZoneInfo.Utc), next.Weight, 12);
        Assert.False(next.RememberedToday);
        Assert.Equal(2, Today(session, [note])); Assert.Equal(1, changes);
        var exported = await session.ExportSyncAsync("practice", _ => "cloud-one");
        Assert.Equal(2, exported.Count(entry => entry.Key.EndsWith("/shown")));
        Assert.DoesNotContain(exported, entry => entry.Key.EndsWith("/completed"));
        Assert.All(exported, AccountStateSync.Validate);
    }

    [Fact]
    public async Task NeutralCompletionPreservesPreviouslyEarnedGradesAndDrawsAnotherWord()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "existing-grades.json");
        var session = new RoamingSession(path, new PracticeClock(Now), () => 0);
        var a = Note("one", "apple"); var b = Note("two", "banana");
        var first = (await session.NextAsync([a]))!;
        var remembered = (await session.NextAsync([a], first.Id, true))!;
        var skipped = (await session.NextAsync([a], remembered.Id, false))!;
        var before = await ReadRecord(path, a.Id);
        var next = (await session.NextPracticeAsync([a, b], skipped.Id, null))!;
        Assert.Equal(b.Id, next.Note.Id);
        Assert.Equal(before, await ReadRecord(path, a.Id));
        Assert.Equal((1, 1), before.Counts(Now, TimeZoneInfo.Utc));
        Assert.Equal(4, Today(session, [a, b]));
        Assert.Equal(2, (await session.ExportSyncAsync("practice", id => "cloud-" + id)).Count(entry => entry.Key.EndsWith("/completed")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task PracticeAvoidsTheJustCompletedWordWithoutChangingLegacyDraws(bool? remembered)
    {
        using var fixture = new LibraryFixture();
        var session = new RoamingSession(Path.Combine(fixture.Metadata, "interleave.json"), new PracticeClock(Now), () => 0);
        var notes = new[] { Note("one", "apple"), Note("two", "banana"), Note("guide", "阅读方法") };
        var first = (await session.NextAsync(notes))!;
        Assert.Equal("one", first.Note.Id);
        var second = (await session.NextPracticeAsync(notes, first.Id, remembered))!;
        var third = (await session.NextPracticeAsync(notes, second.Id, remembered))!;
        Assert.Equal("two", second.Note.Id); Assert.Equal("one", third.Note.Id);
        Assert.Equal(3, Today(session, notes));
        var legacy = new RoamingSession(Path.Combine(fixture.Metadata, "legacy.json"), new PracticeClock(Now), () => 0);
        var legacyFirst = (await legacy.NextAsync(notes))!;
        Assert.Equal(legacyFirst.Note.Id, (await legacy.NextAsync(notes, legacyFirst.Id, true))!.Note.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ConcurrentAndStalePracticeCompletionNeverGradesOrAdvancesTwice(bool? remembered)
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "idempotent.json");
        var session = new RoamingSession(path, new PracticeClock(Now), () => 0);
        var notes = new[] { Note("one", "apple"), Note("two", "banana") };
        var first = (await session.NextAsync(notes))!;
        var changes = 0; session.StateChanged += (_, _) => changes++;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => session.NextPracticeAsync(notes, first.Id, remembered)));
        Assert.Single(results.Select(result => result!.Id).Distinct()); Assert.Equal(1, changes);
        Assert.Equal(2, Today(session, notes));
        var record = await ReadRecord(path, first.Note.Id);
        Assert.Equal(remembered == true ? 1 : 0, record.Remembered);
        Assert.Equal(remembered == false ? 1 : 0, record.Skipped);
        var saved = await File.ReadAllTextAsync(path);
        var current = results[0]!;
        Assert.Equal(current, await session.NextPracticeAsync(notes, first.Id, remembered == true ? false : true));
        Assert.Equal(current, await session.NextPracticeAsync(notes, Guid.NewGuid(), true));
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task FailedAtomicSaveLeavesCurrentCountsAndSyncEventsUnchangedUntilSuccessfulRetry(bool? remembered)
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "atomic.json");
        var session = new RoamingSession(path, new PracticeClock(Now), () => 0);
        var notes = new[] { Note("one", "apple"), Note("two", "banana") };
        var first = (await session.NextAsync(notes))!;
        var saved = await File.ReadAllTextAsync(path);
        var changes = 0; session.StateChanged += (_, _) => changes++;
        File.Delete(path); Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<IOException>(() => session.NextPracticeAsync(notes, first.Id, remembered));
        Assert.Equal(first, session.Current); Assert.Equal(1, Today(session, notes)); Assert.Equal(0, changes);
        Directory.Delete(path); await File.WriteAllTextAsync(path, saved);
        var beforeRetry = await session.ExportSyncAsync("practice", id => "cloud-" + id);
        Assert.Single(beforeRetry, entry => entry.Key.EndsWith("/shown"));
        Assert.DoesNotContain(beforeRetry, entry => entry.Key.EndsWith("/completed"));
        var next = (await session.NextPracticeAsync(notes, first.Id, remembered))!;
        Assert.Equal("two", next.Note.Id); Assert.Equal(2, Today(session, notes)); Assert.Equal(1, changes);
        var record = await ReadRecord(path, first.Note.Id);
        Assert.Equal(remembered == true ? 1 : 0, record.Remembered);
        Assert.Equal(remembered == false ? 1 : 0, record.Skipped);
    }

    [Fact]
    public async Task NeutralSurvivesRestartAndRepeatedCloudReplayWithoutInventingAGradeOrAnotherAppearance()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "source.json");
        var targetPath = Path.Combine(fixture.Metadata, "target.json");
        var clock = new PracticeClock(Now);
        var local = Note("local", "apple"); var remote = Note("remote", "apple");
        var session = new RoamingSession(path, clock, () => 0);
        var first = (await session.NextAsync([local]))!;
        var next = (await session.NextPracticeAsync([local], first.Id, null))!;
        session = new RoamingSession(path, clock, () => 0);
        Assert.Equal(next.Id, (await session.NextAsync([local]))!.Id);
        var bytes = await File.ReadAllTextAsync(path);
        Assert.Equal(next.Id, (await session.NextPracticeAsync([local], first.Id, false))!.Id);
        Assert.Equal(bytes, await File.ReadAllTextAsync(path));
        var events = await session.ExportSyncAsync("source", _ => "cloud");
        Assert.DoesNotContain(events, entry => entry.Key.EndsWith("/completed"));
        var target = new RoamingSession(targetPath, clock, () => 0);
        for (var repeat = 0; repeat < 3; repeat++) await target.MergeSyncAsync(events, _ => remote.Id);
        Assert.Equal(next.Id, (await target.RestoreCurrentAsync([remote]))!.Id);
        Assert.Equal((0, 0), (await ReadRecord(targetPath, remote.Id)).Counts(Now, TimeZoneInfo.Utc));
        Assert.Equal(2, Today(target, [remote]));
        var replay = await target.ExportSyncAsync("target", _ => "cloud");
        await session.MergeSyncAsync(replay, _ => local.Id);
        Assert.Equal((0, 0), (await ReadRecord(path, local.Id)).Counts(Now, TimeZoneInfo.Utc));
        Assert.Equal(next.Id, (await session.RestoreCurrentAsync([local]))!.Id);
        Assert.Equal(next.Weight, target.Current!.Weight, 12);
        Assert.Equal(2, Today(session, [local]));
    }

    [Fact]
    public async Task AlreadyCancelledPracticeDoesNotGradeOrAdvanceTheExistingCard()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "cancel.json");
        var session = new RoamingSession(path, new PracticeClock(Now), () => 0);
        var notes = new[] { Note("one", "apple") };
        var first = (await session.NextAsync(notes))!; var saved = await File.ReadAllTextAsync(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.NextPracticeAsync(notes, first.Id, null, cancellation.Token));
        Assert.Equal(first, session.Current); Assert.Equal(saved, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task NeutralWithNoNextWordCannotBeRestoredByAnOldCurrentPointerAfterRestart()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "removed.json");
        var clock = new PracticeClock(Now); var note = Note("one", "apple");
        var session = new RoamingSession(path, clock, () => 0);
        var first = (await session.NextAsync([note]))!;
        var stale = await session.ExportSyncAsync("source", _ => "cloud");
        Assert.Null(await session.NextPracticeAsync([], first.Id, null));
        Assert.Null(session.Current); Assert.Equal(1, Today(session, [note]));
        session = new RoamingSession(path, clock, () => 0);
        await session.MergeSyncAsync(stale, _ => note.Id);
        Assert.Null(await session.RestoreCurrentAsync([note]));
        Assert.Null(await session.NextPracticeAsync([note], first.Id, false));
        Assert.Equal((0, 0), (await ReadRecord(path, note.Id)).Counts(Now, TimeZoneInfo.Utc));
        Assert.Equal(1, Today(session, [note]));
        var events = await session.ExportSyncAsync("source", _ => "cloud");
        Assert.DoesNotContain(events, entry => entry.Key.EndsWith("/completed"));
        Assert.DoesNotContain(events, entry => entry.Key == "roaming/current");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALocallyNeutralExposureRemainsNeutralWhenItsOldPointerOrConflictingGradeIsReplayed(bool remoteRemembered)
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "neutral-replay.json");
        var clock = new PracticeClock(Now); var note = Note("one", "apple");
        var session = new RoamingSession(path, clock, () => 0);
        var first = (await session.NextAsync([note]))!;
        var stale = await session.ExportSyncAsync("source", _ => "cloud");
        var next = (await session.NextPracticeAsync([note], first.Id, null))!;
        var conflictingGrade = new SyncStateEntry($"event/{first.Id:D}/completed", JsonSerializer.SerializeToElement(new
            { noteId = "cloud", at = Now, day = "2026-11-10", remembered = remoteRemembered }));
        session = new RoamingSession(path, clock, () => 0);
        for (var repeat = 0; repeat < 2; repeat++) await session.MergeSyncAsync(stale.Append(conflictingGrade), _ => note.Id);
        Assert.Equal(next.Id, (await session.RestoreCurrentAsync([note]))!.Id);
        Assert.Equal((0, 0), (await ReadRecord(path, note.Id)).Counts(Now, TimeZoneInfo.Utc));
        Assert.Equal(2, Today(session, [note]));
        Assert.DoesNotContain(await session.ExportSyncAsync("source", _ => "cloud"), entry => entry.Key.EndsWith("/completed"));
    }

    [Fact]
    public async Task NeutralReplayProtectionKeepsOnlyTheMostRecent4096LocalExposureIds()
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "bounded-neutral.json");
        var previous = Enumerable.Range(0, 4096).Select(_ => Guid.NewGuid()).ToArray();
        Directory.CreateDirectory(fixture.Metadata);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Records = new { }, CurrentExposure = (Guid?)null, CurrentNoteId = (string?)null,
            Activity = new { }, NeutralCompletions = previous
        }));
        var session = new RoamingSession(path, new PracticeClock(Now), () => 0);
        var note = Note("one", "apple");
        var first = (await session.NextAsync([note]))!;
        await session.NextPracticeAsync([note], first.Id, null);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var retained = document.RootElement.GetProperty("NeutralCompletions").EnumerateArray().Select(value => value.GetGuid()).ToArray();
        Assert.Equal(4096, retained.Length); Assert.DoesNotContain(previous[0], retained);
        Assert.Contains(previous[^1], retained); Assert.Equal(first.Id, retained[^1]);
        Assert.DoesNotContain(await session.ExportSyncAsync("practice", _ => "cloud"), entry => entry.Value.TryGetProperty("neutral", out _));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    public async Task InvalidLocalNeutralHistoryIsReportedWithoutReplacingTheSavedFile(string damage)
    {
        using var fixture = new LibraryFixture();
        var path = Path.Combine(fixture.Metadata, "bad-neutral.json");
        var id = Guid.NewGuid();
        var neutral = damage switch
        {
            "empty" => new[] { Guid.Empty },
            "duplicate" => new[] { id, id },
            _ => Enumerable.Range(0, 4097).Select(_ => Guid.NewGuid()).ToArray()
        };
        var saved = JsonSerializer.Serialize(new { Records = new { }, CurrentExposure = (Guid?)null, CurrentNoteId = (string?)null, NeutralCompletions = neutral });
        Directory.CreateDirectory(fixture.Metadata); await File.WriteAllTextAsync(path, saved);
        Assert.Throws<IOException>(() => new RoamingSession(path));
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
    }

    private static async Task<ReviewRecord> ReadRecord(string path, string id)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        return document.RootElement.GetProperty("Records").GetProperty(id).Deserialize<ReviewRecord>()!;
    }
    private static int Today(RoamingSession session, IReadOnlyList<LibraryNote> notes)
    {
        var statistics = session.GetStatistics(notes);
        return statistics.DayCounts.Single(day => day.Day == statistics.Today).Count;
    }
    private sealed class PracticeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
