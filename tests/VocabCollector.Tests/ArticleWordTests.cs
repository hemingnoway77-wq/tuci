using VocabCollector.Desktop.Reading;

namespace VocabCollector.Tests;

public sealed class ArticleWordTests
{
    [Fact]
    public void RepeatedWordUsesSentenceAtClickedOccurrenceAndPreservesInflection()
    {
        const string text = "Learning takes practice. The children were learning together! They kept going.";
        var words = ArticleWords.Tokenize(text);
        var first = words.Single(word => word.Start == 0);
        var second = words.Single(word => word.Text == "learning");
        Assert.Equal("Learning", first.Text);
        Assert.Equal("Learning takes practice.", first.Context);
        Assert.Equal("The children were learning together!", second.Context);
    }

    [Fact]
    public void HyphenAndApostropheStayWithinWordButPunctuationAndNonWordsDoNot()
    {
        var words = ArticleWords.Tokenize("A well-known reader’s note: don't replace 中文 or 42.");
        Assert.Equal(new[] { "A", "well-known", "reader’s", "note", "don't", "replace", "or" }, words.Select(word => word.Text));
        Assert.All(words, word => Assert.InRange(word.Context.Length, 1, 2000));
        Assert.Empty(ArticleWords.Tokenize(new string('a', 65)));
    }

    [Fact]
    public void LongSentenceContextStillContainsClickedOccurrence()
    {
        var text = new string('x', 2500) + " chosen " + new string('y', 2500);
        var word = Assert.Single(ArticleWords.Tokenize(text));
        Assert.Equal("chosen", word.Text);
        Assert.Contains("chosen", word.Context);
        Assert.InRange(word.Context.Length, 1, 2000);
    }
}
