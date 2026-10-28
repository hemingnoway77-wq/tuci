using VocabCollector.Core;
using VocabCollector.Core.Ai;

namespace VocabCollector.Tests;

public class SpellingValidatorTests
{
    [Fact] public void ResolveWord_RequiresSpellingAssessmentForAcceptedAnalysis() =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("word", Accepted("word") with { Spelling = null }));

    [Theory]
    [InlineData("  RESILIENT  ", "resilient")]
    [InlineData("introduced", "introduced")]
    [InlineData("CAN’T", "can't")]
    public void ResolveWord_PreservesValidSurfaceSpelling(string input, string expected)
    {
        var analysis = Accepted(expected) with { Lemma = expected == "introduced" ? "introduce" : "" };
        Assert.Equal(expected, SpellingValidator.ResolveWord(input, analysis));
    }

    [Theory]
    [InlineData("recieve", "receive")]
    [InlineData("recieved", "received")]
    [InlineData("teh", "the")]
    [InlineData("beutiful", "beautiful")]
    [InlineData("enviroment", "environment")]
    [InlineData("receve", "receive")]
    [InlineData("beutifull", "beautiful")]
    [InlineData("accommadete", "accommodate")]
    [InlineData("busines", "business")]
    [InlineData("clas", "class")]
    [InlineData("gras", "grass")]
    [InlineData("glas", "glass")]
    [InlineData("proces", "process")]
    [InlineData("worl", "world")]
    [InlineData("frien", "friend")]
    public void ResolveWord_ResolvesNearbyTypoInOneAnalysis(string input, string expected) =>
        Assert.Equal(expected, SpellingValidator.ResolveWord(input, Corrected(input, expected)));

    [Fact] public void ResolveWord_NormalizesAssessmentAndCurlyApostrophes()
    {
        var analysis = Accepted("can't") with
        {
            Spelling = new(" CAN’T ", " CAN'T ", SpellingStatus.Valid, []),
            Examples = [new("I can’t do it today.", "我今天做不了。"), new("We CAN’T go there now.", "我们现在不能去。")]
        };
        Assert.Equal("can't", SpellingValidator.ResolveWord("Can’t", analysis));
    }

    [Theory]
    [InlineData("cat", "cater")]
    [InlineData("abcde", "axcyeq")]
    [InlineData("abcdefghij", "axcyefzhqj")]
    [InlineData("recieve", "banana")]
    public void ResolveWord_RejectsCorrectionBeyondLengthBound(string input, string word) =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord(input, Corrected(input, word)));

    [Theory]
    [InlineData("introduced", "introduce")]
    [InlineData("running", "run")]
    [InlineData("studied", "study")]
    [InlineData("talked", "talk")]
    [InlineData("makes", "make")]
    [InlineData("studies", "study")]
    public void ResolveWord_RejectsInflectionReplacedByLemma(string input, string lemma) =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord(input, Corrected(input, lemma) with { Lemma = lemma }));

    [Theory]
    [InlineData("smaller", "small")]
    [InlineData("smallest", "small")]
    [InlineData("nicer", "nice")]
    [InlineData("nicest", "nice")]
    [InlineData("happier", "happy")]
    [InlineData("happiest", "happy")]
    [InlineData("bigger", "big")]
    [InlineData("biggest", "big")]
    public void ResolveWord_RejectsComparativeOrSuperlativeReplacedByLemma(string input, string lemma) =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord(input, Corrected(input, lemma)));

    [Theory]
    [InlineData("introduce", "introduced")]
    [InlineData("walk", "walks")]
    [InlineData("make", "makes")]
    [InlineData("study", "studied")]
    [InlineData("small", "smaller")]
    [InlineData("small", "smallest")]
    [InlineData("nice", "nicer")]
    [InlineData("nice", "nicest")]
    [InlineData("happy", "happier")]
    [InlineData("happy", "happiest")]
    [InlineData("big", "bigger")]
    [InlineData("big", "biggest")]
    public void ResolveWord_RejectsValidBaseChangedToInflection(string input, string inflection) =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord(input, Corrected(input, inflection) with { Lemma = input }));

    [Fact] public void ResolveWord_NormalizesLemmaBeforeReverseInflectionGuard() =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("introduce", Corrected("introduce", "introduced") with { Lemma = " INTRODUCE " }));

    [Theory]
    [InlineData("introdce", "introduce")]
    [InlineData("biger", "bigger")]
    [InlineData("bigest", "biggest")]
    [InlineData("hapiest", "happiest")]
    [InlineData("studys", "studies")]
    public void ResolveWord_CorrectsMisspelledSurfaceInflection(string input, string expected) =>
        Assert.Equal(expected, SpellingValidator.ResolveWord(input, Corrected(input, expected)));

    [Theory]
    [InlineData(true, SpellingStatus.Invalid, "")]
    [InlineData(true, SpellingStatus.Uncertain, "")]
    [InlineData(false, SpellingStatus.Valid, "word")]
    [InlineData(false, SpellingStatus.Corrected, "ward")]
    [InlineData(true, SpellingStatus.Valid, "ward")]
    [InlineData(true, SpellingStatus.Corrected, "word")]
    [InlineData(true, (SpellingStatus)42, "word")]
    public void ResolveWord_RejectsInconsistentStatus(bool valid, SpellingStatus status, string word)
    {
        var analysis = Accepted("word") with { IsValidWord = valid, Spelling = new("word", word, status, []) };
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("word", analysis));
    }

    [Fact] public void ResolveWord_RejectsDifferentInputEcho() =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("word", Accepted("ward")));

    [Theory]
    [InlineData("../word")][InlineData("https://word")][InlineData("two words")][InlineData("")]
    public void ResolveWord_RejectsUnsafeTarget(string target) =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("word", Accepted("word") with { Spelling = new("word", target, SpellingStatus.Corrected, []) }));

    [Theory]
    [InlineData("This is resiliently done.")]
    [InlineData("This is non-resilient material.")]
    [InlineData("It means something else.")]
    [InlineData("This is resilient2 material.")]
    public void ResolveWord_RejectsExamplesWithoutResolvedWholeWord(string example)
    {
        var analysis = Accepted("resilient") with { Examples = [new(example, "例句。"), new("She remained resilient.", "她依然坚韧。你")] };
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("resilient", analysis));
    }

    [Fact] public void ResolveWord_RejectsStaleInputExamplesAfterCorrection()
    {
        var analysis = Corrected("recieve", "receive") with { Examples = Accepted("recieve").Examples };
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("recieve", analysis));
    }

    [Theory][InlineData(SpellingStatus.Invalid)][InlineData(SpellingStatus.Uncertain)]
    public void ResolveWord_ReturnsNoTargetForRejection(SpellingStatus status)
    {
        var analysis = Accepted("word") with { IsValidWord = false, Spelling = new("word", "", status, ["ward", "world"]) };
        Assert.Equal("", SpellingValidator.ResolveWord("word", analysis));
    }

    [Fact] public void ResolveWord_AllowsLegacyRejectionWithoutMetadata() =>
        Assert.Equal("", SpellingValidator.ResolveWord("word", Accepted("word") with { IsValidWord = false, Spelling = null }));

    [Theory]
    [InlineData("../ward")][InlineData("two words")][InlineData("https://example.com")]
    public void ResolveWord_RejectsUnsafeSuggestion(string suggestion)
    {
        var analysis = Accepted("word") with { IsValidWord = false, Spelling = new("word", "", SpellingStatus.Uncertain, [suggestion]) };
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("word", analysis));
    }

    [Fact] public void ResolveWord_RejectsTooManySuggestions()
    {
        var analysis = Accepted("word") with { IsValidWord = false, Spelling = new("word", "", SpellingStatus.Uncertain, ["ward", "world", "wore", "work"]) };
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("word", analysis));
    }

    [Fact] public void ResolveWord_RejectsSuggestionsOnAcceptedWord() =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("word", Accepted("word") with { Spelling = new("word", "word", SpellingStatus.Valid, ["ward"]) }));

    [Fact] public void ResolveWord_RejectsTargetOnUncertainWord() =>
        Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("word", Accepted("word") with { IsValidWord = false, Spelling = new("word", "ward", SpellingStatus.Uncertain, ["ward"]) }));

    private static WordAnalysis Accepted(string word) => new(true, "", "", "", PartOfSpeech.Noun,
        [new(PartOfSpeech.Noun, "释义")], [new($"I know {word} now.", "例句。"), new($"We use {word} here.", "例句。")], [], [])
        { Spelling = new(word, word, SpellingStatus.Valid, []) };

    private static WordAnalysis Corrected(string input, string word) => Accepted(word) with { Lemma = word, Spelling = new(input, word, SpellingStatus.Corrected, []) };
}
