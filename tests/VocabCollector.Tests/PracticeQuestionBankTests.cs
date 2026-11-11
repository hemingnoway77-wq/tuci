using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public class PracticeQuestionBankTests
{
    [Fact]
    public void BuildsEveryQuoteLineWithoutSplittingSynonymsAndIdsSurviveReordering()
    {
        var first = PracticeQuestionBank.Build(Note("book", "**释义**：\n\n> n. 书，书籍；著作  \n> v. 预订，预约\n\n#### 例句\n1. A book."));
        var reversed = PracticeQuestionBank.Build(Note("book", "**释义**：\n\n> v. 预订，预约\n> n. 书，书籍；著作\n"));
        Assert.Equal(2, first.Count);
        Assert.Equal("n. 书，书籍；著作", first[0].Meaning);
        Assert.Equal(first[0].Id, reversed[1].Id);
        Assert.Equal(first[1].Id, reversed[0].Id);
        Assert.NotEqual(first[0].Id, first[1].Id);
        Assert.Equal(new[] { 1, 2 }, first.Select(sense => sense.Number));
        Assert.All(first, sense => Assert.Equal(2, sense.Count));
    }

    [Theory]
    [InlineData("普通笔记", "**释义**：\n> 学习说明")]
    [InlineData("book", "# book\n没有释义")]
    [InlineData("book", "**释义**：\n> 待补充")]
    [InlineData("book", "```markdown\n**释义**：\n> 代码不是题源\n```")]
    public void InsufficientMaterialDoesNotInventQuestions(string title, string markdown) =>
        Assert.Empty(PracticeQuestionBank.Build(Note(title, markdown)));

    [Fact]
    public void ExtractsTrustedLemmaAndFormNoteWithoutGuessingInflections()
    {
        var senses = PracticeQuestionBank.Build(Note("introduced", "---\nlemma: \"introduce\"\n---\n**释义**：\n> v. 介绍\n\n**词形**：introduced 是 introduce 的过去式。\n"));
        var sense = Assert.Single(senses);
        Assert.Equal("introduce", sense.Lemma);
        Assert.Contains("过去式", sense.FormNote);
        Assert.Null(Assert.Single(PracticeQuestionBank.Build(Note("introduced", "**释义**：\n> v. 介绍"))).Lemma);
    }

    [Theory]
    [InlineData("lemma: [introduce]")]
    [InlineData("lemma: introduce\nlemma: intro")]
    [InlineData("lemma: |\n  introduce")]
    [InlineData("lemma: \"introduce extra\"")]
    [InlineData("lemma: \"introduce\" trailing")]
    public void InvalidOrAmbiguousFrontmatterFallsBackOnlyToExplicitDictionaryLemma(string front)
    {
        var note = Note("introduced", "---\n" + front + "\n---\n**释义**：\n> v. 介绍");
        Assert.Equal("introduce", Assert.Single(PracticeQuestionBank.Build(note, "Introduce")).Lemma);
        Assert.Null(Assert.Single(PracticeQuestionBank.Build(note, "not a word")).Lemma);
    }

    [Fact]
    public void ContextRequiresOneSenseAndAWholeTargetWordNotAnExampleIndex()
    {
        var single = "**释义**：\n> v. 预订\n\n#### 遇见时的原句\n> Please **book** a table.\n\n#### 我的笔记\n私人笔记";
        Assert.Equal("Please book a table.", Assert.Single(PracticeQuestionBank.Build(Note("book", single))).Context);
        Assert.Empty(Assert.Single(PracticeQuestionBank.Build(Note("book", single.Replace("**book**", "notebook")))).Context);
        var multiple = single.Replace("> v. 预订", "> n. 书\n> v. 预订") + "\n#### 例句\n1. Read a book.\n2. Book a table.";
        Assert.All(PracticeQuestionBank.Build(Note("book", multiple)), sense => Assert.Empty(sense.Context));
        Assert.Empty(Assert.Single(PracticeQuestionBank.Build(Note("book", "**释义**：\n> n. 书\n\n#### 例句\n1. Read a book."))).Context);
    }

    [Fact]
    public void BindsEveryContextBySenseIdEvenWhenInputOrderDiffers()
    {
        var senses = PracticeQuestionBank.Build(Note("book", "**释义**：\n> n. 书\n> v. 预订"));
        var bound = PracticeQuestionBank.WithContexts(senses,
        [new(senses[1].Id, "We book a table for dinner.", "我们预订一张餐桌。"),
         new(senses[0].Id, "She read a book on the train.", "她在火车上读了一本书。")]);
        Assert.Equal(2, bound.Count);
        Assert.Equal(senses.Select(sense => sense.Id), bound.Select(sense => sense.Id));
        Assert.Equal("She read a book on the train.", bound[0].Context);
        Assert.Equal("我们预订一张餐桌。", bound[1].ContextTranslation);
    }

    [Fact]
    public void MissingUnknownOrDuplicateSenseIdsCannotSilentlyDropAMeaning()
    {
        var senses = PracticeQuestionBank.Build(Note("book", "**释义**：\n> n. 书\n> v. 预订"));
        var noun = new PracticeContext(senses[0].Id, "She read a book on the train.", "她在火车上读了一本书。");
        var verb = new PracticeContext(senses[1].Id, "We book a table for dinner.", "我们预订一张餐桌。");
        Assert.Throws<ArgumentException>(() => PracticeQuestionBank.WithContexts(senses, [noun]));
        Assert.Throws<ArgumentException>(() => PracticeQuestionBank.WithContexts(senses, [noun, noun]));
        Assert.Throws<ArgumentException>(() => PracticeQuestionBank.WithContexts(senses, [noun, verb with { SenseId = "unknown" }]));
        Assert.Throws<ArgumentException>(() => PracticeQuestionBank.WithContexts(senses, [noun, verb with { SenseId = " " + senses[1].Id }]));
    }

    [Theory]
    [InlineData("A notebook is on the table.", "笔记本在桌上。")]
    [InlineData("She read a book ____ on the train.", "她读了一本书。")]
    [InlineData("She read a [[book]] on the train.", "她读了一本书。")]
    [InlineData("She read a [book](https://example.test) on the train.", "她读了一本书。")]
    [InlineData("She read a <b>book</b> on the train.", "她读了一本书。")]
    [InlineData("She read a book on the train.\n", "她读了一本书。")]
    [InlineData("She read a book\u202E on the train.", "她读了一本书。")]
    [InlineData("She read a book on the train.", " ")]
    [InlineData("She read a book on the train.", "She read a book on the train.")]
    [InlineData("She read a book on the train.", "她读了一本书。 https://example.test")]
    public void RejectsUnusableOrNonPlainContext(string english, string chinese)
    {
        var senses = PracticeQuestionBank.Build(Note("book", "**释义**：\n> n. 书"));
        Assert.Throws<ArgumentException>(() => PracticeQuestionBank.WithContexts(senses, [new(senses[0].Id, english, chinese)]));
    }

    [Theory]
    [InlineData(" Introduced ", PracticeOutcome.Correct)]
    [InlineData("INTRODUCE", PracticeOutcome.LemmaCorrect)]
    [InlineData("introducd", PracticeOutcome.Incorrect)]
    [InlineData("intro", PracticeOutcome.Incorrect)]
    [InlineData("introduced extra", PracticeOutcome.Incorrect)]
    public void SpellingGradesExactWordAndTrustedLemmaOnly(string answer, PracticeOutcome expected) =>
        Assert.Equal(expected, PracticeQuestionBank.Evaluate(Question("introduced", "introduce"), answer));

    [Fact]
    public void EvaluationNormalizesApostrophesButNeverGradesChineseAutomatically()
    {
        Assert.Equal(PracticeOutcome.Correct, PracticeQuestionBank.Evaluate(Question("can't", null), " CAN’T "));
        Assert.Equal(PracticeOutcome.Incorrect, PracticeQuestionBank.Evaluate(Question("introduced", null), "introduce"));
        Assert.Equal(PracticeOutcome.TimedOut, PracticeQuestionBank.Evaluate(Question("book", null), "book", timedOut: true));
        Assert.Throws<InvalidOperationException>(() => PracticeQuestionBank.Evaluate(Question("book", null) with { Kind = PracticeKind.Meaning }, "书籍"));
    }

    internal static LibraryNote Note(string word, string markdown) => new("note-" + word, word + ".md", Path.Combine(Path.GetTempPath(), word + ".md"), markdown, DateTimeOffset.UtcNow);
    private static PracticeQuestion Question(string word, string? lemma) => new(Guid.NewGuid(), "note", "sense", PracticeKind.Spelling, word, lemma, "释义", "提示", "", "", 1, 1);
}
