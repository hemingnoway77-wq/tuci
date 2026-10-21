using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public class ManagedLinksTests
{
    [Fact]
    public void Examples_LinkOnlyWholeContentWordsAndCapEachSentence()
    {
        using var vault = new TempVault();
        var index = Index(vault, "the", "cat", "teacher", "school", "book", "resilient");
        var source = ManagedLinks.Wrap("examples", "1. The **resilient** cat met a teacher at school with a book and cats.\n   中文里 teacher 不链接。");

        var actual = ManagedLinks.Refresh(source, "resilient", index);

        Assert.Contains("The **resilient** [[英语单词/cat|cat]] met a [[英语单词/teacher|teacher]] at [[英语单词/school|school]] with a book and cats.", actual);
        Assert.Contains("中文里 teacher 不链接。", actual);
        Assert.DoesNotContain("|The]]", actual);
        Assert.DoesNotContain("|resilient]]", actual);
    }

    [Fact]
    public void Relations_LinkOnlyHeadwordAndKeepMissingOrAmbiguousWordsPlain()
    {
        using var vault = new TempVault();
        var notes = new[] { vault.Note("英语单词/动词/introduce.md"), vault.Note("英语单词/名词/record.md"), vault.Note("英语单词/动词/record.md") };
        var source = ManagedLinks.Wrap("relations", "- introduce：解释里 introduce 保持普通文字\n- record：不猜同名目标\n- unknown：暂无卡片");

        var actual = ManagedLinks.Refresh(source, "present", new(notes));

        Assert.Contains("- [[英语单词/动词/introduce|introduce]]：解释里 introduce 保持普通文字", actual);
        Assert.Contains("- record：不猜同名目标", actual);
        Assert.Contains("- unknown：暂无卡片", actual);
    }

    [Fact]
    public void Refresh_AddsFutureLinksRemovesMissingLinksAndRemainsIdempotent()
    {
        using var vault = new TempVault();
        var first = vault.Note("英语单词/teacher.md");
        var source = "Personal text teacher\n" + ManagedLinks.Wrap("examples", "1. A teacher has a book.");
        var beforeBook = ManagedLinks.Refresh(source, "learn", new([first]));
        var book = vault.Note("英语单词/名词/book.md");

        var afterBook = ManagedLinks.Refresh(beforeBook, "learn", new([first, book]));

        Assert.Contains("A [[英语单词/teacher|teacher]] has a [[英语单词/名词/book|book]].", afterBook);
        Assert.Equal(afterBook, ManagedLinks.Refresh(afterBook, "learn", new([first, book])));
        File.Delete(first.AbsolutePath);
        var removed = ManagedLinks.Refresh(afterBook, "learn", new([book]));
        Assert.Contains("A teacher has a [[英语单词/名词/book|book]].", removed);
        Assert.StartsWith("Personal text teacher\n", removed);
    }

    [Fact]
    public void UserEditedBlockAndUnmanagedTextAreNeverRewritten()
    {
        using var vault = new TempVault();
        var source = "1. teacher outside marker\n" + ManagedLinks.Wrap("examples", "1. A teacher.").Replace("A teacher.", "My teacher.");

        Assert.Equal(source, ManagedLinks.Refresh(source, "learn", Index(vault, "teacher")));
    }

    [Fact]
    public void EscapedLinksCodeAndMarkdownLinksAreNotTurnedIntoRealLinks()
    {
        using var vault = new TempVault();
        var source = ManagedLinks.Wrap("examples", @"1. \[\[teacher\]\] `teacher` [teacher](https://example.test) &#91;&#91;teacher&#93;&#93; teacher.");

        var actual = ManagedLinks.Refresh(source, "learn", Index(vault, "teacher"));

        Assert.Contains(@"\[\[teacher\]\] `teacher` [teacher](https://example.test) &#91;&#91;teacher&#93;&#93; [[英语单词/teacher|teacher]].", actual);
    }

    [Fact]
    public void Refresh_DoesNotStripLinkSyntaxShownAsCodeOrEscapedText()
    {
        using var vault = new TempVault();
        var source = ManagedLinks.Wrap("examples", @"1. `[[英语单词/teacher|teacher]]` \[\[teacher\]\] teacher.");

        var actual = ManagedLinks.Refresh(source, "learn", Index(vault, "teacher"));

        Assert.Contains(@"`[[英语单词/teacher|teacher]]` \[\[teacher\]\] [[英语单词/teacher|teacher]].", actual);
    }

    private static VaultIndex Index(TempVault vault, params string[] words) => new(words.Select(w => vault.Note("英语单词/" + w + ".md")).ToArray());
}
