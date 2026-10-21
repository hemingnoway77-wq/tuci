using VocabCollector.Core;
namespace VocabCollector.Tests;
public class WordInputTests
{
    [Theory]
    [InlineData(" Resilient ", "resilient")]
    [InlineData("can’t", "can't")]
    [InlineData("co-operate", "co-operate")]
    public void Normalize_TrimsCaseAndPunctuation(string raw, string expected) => Assert.Equal(expected, WordInput.Normalize(raw));

    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("two words")][InlineData("../word")][InlineData("wo\0rd")][InlineData("a/b")][InlineData("-word")]
    public void Normalize_RejectsInvalidInput(string raw) => Assert.Throws<ArgumentException>(() => WordInput.Normalize(raw));

    [Fact] public void Normalize_EnforcesLengthBoundary()
    {
        Assert.Equal(new string('a', 64), WordInput.Normalize(new string('a', 64)));
        Assert.Throws<ArgumentException>(() => WordInput.Normalize(new string('a', 65)));
    }
    [Fact] public void Normalize_PreservesInflections()
    {
        Assert.NotEqual(WordInput.Normalize("saw"), WordInput.Normalize("see"));
        Assert.NotEqual(WordInput.Normalize("running"), WordInput.Normalize("run"));
    }
    [Theory][InlineData("con")][InlineData("aux")][InlineData("prn")][InlineData("nul")]
    public void FileName_ReservedNamesRoundTrip(string word)
    {
        Assert.Equal("_" + word + ".md", WordInput.ToFileName(word));
        Assert.Equal(word, WordInput.FromFileStem("_" + word));
        Assert.Equal("resilient.md", WordInput.ToFileName("resilient"));
        Assert.Equal("_other", WordInput.FromFileStem("_other"));
    }
}
