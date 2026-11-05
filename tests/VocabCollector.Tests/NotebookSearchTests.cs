using System.Diagnostics;
using VocabCollector.Core.Library;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

public sealed class NotebookSearchTests
{
    private static readonly LibraryNote[] Notes =
    [
        Note("a", "alpha.md", "The quick brown fox\nred blue"),
        Note("b", "folder/beta.md", "quick fox green"),
        Note("c", "folder/alpha guide.md", "brown fox red green"),
        Note("d", "中文/笔记.md", "中文检索 测试短语")
    ];

    [Theory]
    [InlineData("quick fox", "a,b")]
    [InlineData("\"quick brown\"", "a")]
    [InlineData("blue OR green -brown", "a,b")]
    [InlineData("fox -\"brown fox\"", "b")]
    [InlineData("file:alpha", "a,c")]
    [InlineData("file:\"alpha guide\"", "c")]
    [InlineData("path:folder content:green", "b,c")]
    [InlineData("content:alpha", "")]
    [InlineData("/qu.ck\\s+(brown|fox)/", "a,b")]
    [InlineData("path:/^folder\\// -file:beta", "c")]
    [InlineData("中文 测试短语", "d")]
    [InlineData("", "a,b,c,d")]
    public void SearchCombinesTermsPhrasesGroupsExclusionsFieldsAndRegex(string query, string ids) =>
        Assert.Equal(ids, string.Join(',', NotebookSearch.Search(Notes, query).Select(hit => hit.Note.Id)));

    [Theory]
    [InlineData("/[a-/", "正则")]
    [InlineData("\"unfinished", "引号")]
    [InlineData("alpha OR", "OR")]
    [InlineData("file:", "内容")]
    public void InvalidQueriesReturnFriendlyMessageAndNoPartialResults(string query, string message)
    {
        Assert.False(NotebookSearch.TrySearch(Notes, query, out var results, out var error));
        Assert.Empty(results);
        Assert.Contains(message, error);
        Assert.Throws<ArgumentException>(() => NotebookSearch.Search(Notes, query));
    }

    [Fact]
    public void PathologicalRegexHasBoundedRuntimeAndExplainsTimeout()
    {
        var watch = Stopwatch.StartNew();
        Assert.False(NotebookSearch.TrySearch([Note("large", "large.md", new string('a', 100_000) + "!")], "/(a+)+$/", out var results, out var error));
        Assert.Empty(results);
        Assert.Contains("超时", error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ExcerptIncludesLateMatchAndRemainsCompact()
    {
        var text = new string('a', 1200) + " the matched passage " + new string('b', 1200);
        var hit = Assert.Single(NotebookSearch.Search([Note("long", "long.md", text)], "matched passage"));
        Assert.Contains("matched passage", hit.Excerpt);
        Assert.True(hit.Excerpt.Length <= 260);
    }

    [Theory]
    [InlineData("resilient.md", "rslnt", true)]
    [InlineData("Nested/Resilient.md", "RSL", true)]
    [InlineData("中文学习.md", "中学", true)]
    [InlineData("alpha.md", "ahp", false)]
    [InlineData("alpha.md", "", true)]
    public void QuickOpenUsesOrderedCaseInsensitiveSubsequence(string name, string query, bool expected) =>
        Assert.Equal(expected, NotebookSearch.MatchFileName(name, query));

    private static LibraryNote Note(string id, string path, string body) => new(id, path, Path.Combine("C:\\test-vault", path), body, DateTimeOffset.UnixEpoch);
}
