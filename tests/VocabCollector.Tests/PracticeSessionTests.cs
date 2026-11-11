using System.Text.Json;
using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public class PracticeSessionTests
{
    [Fact]
    public async Task RotatesEverySenseAndKindAcrossRestartsWithoutChangingMarkdown()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "practice.json");
        var note = PracticeQuestionBankTests.Note("book", "**释义**：\n> n. 书\n> v. 预订");
        var original = note.Markdown;
        var questions = new List<PracticeQuestion>();
        for (var index = 0; index < 4; index++)
        {
            var session = new PracticeSession(file, () => 0);
            var question = await Prepare(session, note, Guid.NewGuid());
            Assert.NotNull(question); questions.Add(question);
            await session.CompleteAsync(question, question.Kind == PracticeKind.Spelling ? PracticeOutcome.Correct : PracticeOutcome.SelfConfirmed, "book");
        }
        Assert.Equal(4, questions.Select(q => (q.SenseId, q.Kind)).Distinct().Count());
        Assert.Equal(original, note.Markdown);
    }

    [Fact]
    public async Task SameExposureRestoresExactQuestionAndCompletesOnlyOnce()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "practice.json");
        var note = PracticeQuestionBankTests.Note("book", "**释义**：\n> n. 书");
        var id = Guid.NewGuid(); var session = new PracticeSession(file, () => 0);
        var first = (await Prepare(session, note, id))!;
        Assert.NotNull(first);
        Assert.Null(session.GetCompletion(id));
        Assert.Equal(first, await Prepare(new PracticeSession(file, () => 1), note, id));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => session.CompleteAsync(first, PracticeOutcome.Correct, "book")));
        Assert.Equal(new PracticeCompletion(PracticeOutcome.Correct, "book"), session.GetCompletion(id));
        Assert.Equal(session.GetCompletion(id), new PracticeSession(file).GetCompletion(id));
        Assert.Equal(first, await new PracticeSession(file).PrepareAsync(note with { Markdown = "**释义**：\n> v. 预订" }, id));
        var saved = await File.ReadAllTextAsync(file);
        await new PracticeSession(file).CompleteAsync(first, PracticeOutcome.Incorrect, "late answer");
        Assert.Equal(saved, await File.ReadAllTextAsync(file));
        var next = (await Prepare(new PracticeSession(file, () => 0), note, Guid.NewGuid()))!;
        Assert.NotEqual(first.Kind, next.Kind);
    }

    [Fact]
    public async Task WeakItemWinsWhenAllPairsHaveEqualPracticeCounts()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "practice.json");
        var note = PracticeQuestionBankTests.Note("book", "**释义**：\n> n. 书");
        var session = new PracticeSession(file, () => 0);
        var spelling = (await Prepare(session, note, Guid.NewGuid()))!;
        Assert.Equal(PracticeKind.Spelling, spelling.Kind);
        await session.CompleteAsync(spelling, PracticeOutcome.Incorrect, "bok");
        var meaning = (await Prepare(session, note, Guid.NewGuid()))!;
        Assert.Equal(PracticeKind.Meaning, meaning.Kind);
        Assert.Contains("book", meaning.Context);
        Assert.Contains("book", meaning.Prompt);
        Assert.NotEmpty(meaning.ContextTranslation);
        await session.CompleteAsync(meaning, PracticeOutcome.SelfConfirmed, "书籍");
        Assert.Equal(PracticeKind.Spelling, (await Prepare(new PracticeSession(file, () => 1), note, Guid.NewGuid()))!.Kind);
    }

    [Fact]
    public async Task SpellingVisibleFieldsDoNotLeakWordLemmaOrLinkTargets()
    {
        using var fixture = new LibraryFixture();
        var note = PracticeQuestionBankTests.Note("introduced", "---\nlemma: introduce\n---\n**释义**：\n> v. [[introduced|介绍]]，introduce 的变形\n\n**词形**：introduced / introduce\n\n#### 遇见时的原句\n> They INTRODUCED a guest; introduce was the base form.\n");
        var question = (await Prepare(new PracticeSession(Path.Combine(fixture.Metadata, "practice.json"), () => 0), note, Guid.NewGuid()))!;
        Assert.NotNull(question);
        var visible = string.Join("\n", question.Prompt, question.Context, question.Meaning, question.ContextTranslation).ToLowerInvariant();
        Assert.DoesNotContain("introduced", visible);
        Assert.DoesNotContain("introduce", visible);
        Assert.Contains("____", question.Context);
        Assert.NotEmpty(question.ContextTranslation);
        Assert.Contains("introduced", question.FormNote);
    }

    [Fact]
    public async Task EmptyMaterialDoesNotCreateStateAndLibrariesRemainIsolated()
    {
        using var fixture = new LibraryFixture();
        var firstFile = Path.Combine(fixture.Metadata, "one", "practice.json");
        var secondFile = Path.Combine(fixture.Metadata, "two", "practice.json");
        var first = new PracticeSession(firstFile, () => 0);
        Assert.Null(await first.PrepareAsync(PracticeQuestionBankTests.Note("book", "# book"), Guid.NewGuid()));
        Assert.False(File.Exists(firstFile));
        var note = PracticeQuestionBankTests.Note("book", "**释义**：\n> n. 书");
        var question = (await Prepare(first, note, Guid.NewGuid()))!;
        Assert.NotNull(question);
        await first.CompleteAsync(question, PracticeOutcome.Correct, "book");
        Assert.Equal(PracticeKind.Spelling, (await Prepare(new PracticeSession(secondFile, () => 0), note, Guid.NewGuid()))!.Kind);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("null")]
    public void CorruptStateIsNotSilentlyReplaced(string content)
    {
        using var fixture = new LibraryFixture(); Directory.CreateDirectory(fixture.Metadata);
        var file = Path.Combine(fixture.Metadata, "practice.json"); File.WriteAllText(file, content);
        Assert.Throws<IOException>(() => new PracticeSession(file));
        Assert.Equal(content, File.ReadAllText(file));
    }

    [Fact]
    public async Task FailedCompletionDoesNotAdvanceMemoryAndCanRetry()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "practice.json");
        var session = new PracticeSession(file, () => 0);
        var note = PracticeQuestionBankTests.Note("book", "**释义**：\n> n. 书");
        var first = (await Prepare(session, note, Guid.NewGuid()))!; Assert.NotNull(first);
        var saved = await File.ReadAllTextAsync(file); File.Delete(file); Directory.CreateDirectory(file);
        await Assert.ThrowsAnyAsync<IOException>(() => session.CompleteAsync(first, PracticeOutcome.Correct, "book"));
        Directory.Delete(file); await File.WriteAllTextAsync(file, saved);
        Assert.Equal(first, await Prepare(session, note, first.ExposureId));
        await session.CompleteAsync(first, PracticeOutcome.Correct, "book");
        Assert.NotEqual(saved, await File.ReadAllTextAsync(file));
        Assert.Empty(Directory.EnumerateFiles(fixture.Metadata, "*.tmp"));
    }

    [Fact]
    public async Task CancellationAndForgedCompletionCannotWriteResults()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "practice.json"); var session = new PracticeSession(file, () => 0);
        var note = PracticeQuestionBankTests.Note("book", "**释义**：\n> n. 书");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrepareAsync(note, Guid.NewGuid(), cancellationToken: canceled.Token));
        Assert.False(File.Exists(file));
        var question = (await Prepare(session, note, Guid.NewGuid()))!; Assert.NotNull(question);
        var saved = await File.ReadAllTextAsync(file);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CompleteAsync(question, PracticeOutcome.Correct, "book", canceled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAsync(question with { Word = "other" }, PracticeOutcome.Correct, "other"));
        Assert.Equal(saved, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task NoContextNeverFallsBackToAnIsolatedDefinitionOrPartialSenseBank()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "practice.json");
        var session = new PracticeSession(file, () => 0);
        var single = PracticeQuestionBankTests.Note("book", "**释义**：\n> n. 书");
        Assert.Null(await session.PrepareAsync(single, Guid.NewGuid()));
        Assert.Null(await session.PrepareAsync(single with { Markdown = single.Markdown + "\n#### 遇见时的原句\n> She read a book." }, Guid.NewGuid()));
        Assert.False(File.Exists(file));
        var multiple = single with { Markdown = single.Markdown + "\n> v. 预订" };
        var contexts = Contexts(multiple);
        await Assert.ThrowsAsync<ArgumentException>(() => session.PrepareAsync(multiple, Guid.NewGuid(), contexts: [contexts[0]]));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task LegacyPendingWithoutContextIsNotReusedButCompletedReceiptsArePreserved()
    {
        using var fixture = new LibraryFixture();
        var file = Path.Combine(fixture.Metadata, "practice.json");
        var note = PracticeQuestionBankTests.Note("book", "**释义**：\n> n. 书");
        var question = (await Prepare(new PracticeSession(file, () => 0), note, Guid.NewGuid()))!;
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var stored = json["Attempts"]![question.ExposureId.ToString()]!["Question"]!;
        stored["Context"] = ""; stored.AsObject().Remove("ContextTranslation"); stored["Prompt"] = "根据释义拼词";
        await File.WriteAllTextAsync(file, json.ToJsonString());
        var old = new PracticeSession(file, () => 1);
        Assert.Null(await old.PrepareAsync(note, question.ExposureId));
        var refreshed = (await Prepare(old, note, question.ExposureId))!;
        Assert.NotNull(refreshed);
        Assert.Equal(question.Kind, refreshed.Kind);
        Assert.Contains("____", refreshed.Prompt);
        Assert.NotEmpty(refreshed.ContextTranslation);
        await old.CompleteAsync(refreshed, PracticeOutcome.Correct, "book");
        Assert.Equal(refreshed, await new PracticeSession(file).PrepareAsync(note, question.ExposureId));
    }

    private static Task<PracticeQuestion?> Prepare(PracticeSession session, LibraryNote note, Guid exposureId) =>
        session.PrepareAsync(note, exposureId, contexts: Contexts(note));

    private static IReadOnlyList<PracticeContext> Contexts(LibraryNote note) => PracticeQuestionBank.Build(note).Select(sense =>
        new PracticeContext(sense.Id,
            sense.Word == "introduced" ? "They INTRODUCED a guest; introduce was the base form."
                : sense.Meaning.StartsWith("v.", StringComparison.Ordinal) ? "We book a table for dinner." : "She read a book on the train.",
            sense.Word == "introduced" ? "他们介绍了一位客人；introduce 是原形。"
                : sense.Meaning.StartsWith("v.", StringComparison.Ordinal) ? "我们预订一张餐桌。" : "她在火车上读了一本书。" )).ToArray();
}
