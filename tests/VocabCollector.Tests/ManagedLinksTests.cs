using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public class ManagedLinksTests
{
    [Fact]
    public void ExternalState_LinksCleanSectionsWithoutTechnicalMarkup()
    {
        using var vault = new TempVault();
        var source = "# learn\n\n#### 例句\n1. A teacher has a book.\n\n#### 我的笔记\n1. A teacher stays plain.\n";
        var state = ManagedLinks.CaptureState(source);

        var actual = ManagedLinks.Refresh(source, "learn", Index(vault, "teacher"), state);

        Assert.Single(state);
        Assert.Contains("A [[英语单词/teacher|teacher]] has a book.", actual);
        Assert.EndsWith("#### 我的笔记\n1. A teacher stays plain.\n", actual);
        Assert.DoesNotContain("vocabcollector", actual);
        Assert.Equal(actual, ManagedLinks.RefreshNew(source, "learn", Index(vault, "teacher")));
    }

    [Fact]
    public void AdvanceState_DoesNotAdoptManuallyEditedRegionWhenAnotherRegionChanges()
    {
        using var vault = new TempVault();
        var original = "#### 例句\n1. A teacher helps.\n\n#### 语义关系\n##### 近义词\n- tutor：帮助学习的人\n\n#### 我的笔记\n";
        var state = ManagedLinks.CaptureState(original);
        var edited = original.Replace("A teacher helps.", "My teacher helps.");
        var linked = ManagedLinks.Refresh(edited, "learn", Index(vault, "tutor"), state);
        var advanced = ManagedLinks.AdvanceState(edited, linked, state);

        var refreshed = ManagedLinks.Refresh(linked, "learn", Index(vault, "tutor", "teacher"), advanced);

        Assert.Contains("- [[英语单词/tutor|tutor]]：", linked);
        Assert.Equal(linked, refreshed);
        Assert.Contains("My teacher helps.", refreshed);
        Assert.DoesNotContain("|teacher]]", refreshed);
    }

    [Fact]
    public void CaptureState_PreservesMissingDuplicateAndFencedHeadings()
    {
        using var vault = new TempVault();
        var source = "```md\n#### 例句\n1. A teacher.\n```\n\n#### 例句\n1. A teacher.\n\n#### 例句\n1. Another teacher.\n";

        Assert.Empty(ManagedLinks.CaptureState(source));
        Assert.Equal(source, ManagedLinks.RefreshNew(source, "learn", Index(vault, "teacher")));
    }

    [Fact]
    public void ExternalState_HandlesCrLfWithoutChangingUnmanagedBytes()
    {
        using var vault = new TempVault();
        var source = "个人\r\n\r\n#### 例句\r\n1. A teacher.\r\n\r\n#### 我的笔记\r\n保持空白  \r\n";

        var actual = ManagedLinks.Refresh(source, "learn", Index(vault, "teacher"), ManagedLinks.CaptureState(source));

        Assert.Equal(source.Replace("A teacher.", "A [[英语单词/teacher|teacher]]."), actual);
    }

    [Fact]
    public void ImportLegacy_RemovesOnlyOwnPairedMarkersAndKeepsOriginalProtectionHash()
    {
        using var vault = new TempVault();
        var legacy = "<!-- keep me -->\r\n" + LegacyWrap("examples", "#### 例句\n1. A teacher.\n").Replace("\n", "\r\n") + "\r\n\r\n#### 我的笔记\r\n";
        legacy = legacy.Replace("A teacher.", "My teacher.");

        var imported = ManagedLinks.ImportLegacy(legacy);

        Assert.NotNull(imported);
        Assert.DoesNotContain("vocabcollector", imported.Markdown);
        Assert.StartsWith("<!-- keep me -->\r\n#### 例句\r\n1. My teacher.\r\n", imported.Markdown);
        Assert.Equal(imported.Markdown, ManagedLinks.Refresh(imported.Markdown, "learn", Index(vault, "teacher"), imported.Regions));
    }

    [Fact]
    public void ImportLegacy_DoesNotRemoveMarkersInCodeFencesOrMalformedComments()
    {
        var valid = LegacyWrap("examples", "#### 例句\n1. A teacher.");
        var fenced = "```md\n" + valid + "\n```\n";
        var malformed = valid.Replace("<!-- /vocabcollector:links -->", "<!-- /vocabcollector:links broken -->");

        Assert.Null(ManagedLinks.ImportLegacy(fenced));
        Assert.Null(ManagedLinks.ImportLegacy(malformed));
        Assert.Null(ManagedLinks.ImportLegacy("<!-- vocabcollector:links:v1 looks similar -->"));
    }

    [Fact]
    public void Refresh_PreservesFencedSentencesAndUnrelatedMultilineComments()
    {
        using var vault = new TempVault();
        var source = "#### 例句\n1. A teacher.\n\n```md\n1. A teacher has [[英语单词/book|book]].\n```\n<!-- personal example\n1. A teacher has [[英语单词/book|book]].\n-->\n";

        var actual = ManagedLinks.RefreshNew(source, "learn", Index(vault, "teacher"));

        Assert.Equal(source.Replace("#### 例句\n1. A teacher.", "#### 例句\n1. A [[英语单词/teacher|teacher]]."), actual);
    }

    [Fact]
    public void ImportLegacy_RejectsDuplicatedRegionModesInsteadOfCreatingInvalidExternalState()
    {
        var first = LegacyWrap("examples", "#### 例句\n1. A teacher.");
        var second = LegacyWrap("examples", "#### 例句\n1. Another teacher.");

        Assert.Null(ManagedLinks.ImportLegacy(first + "\n\n" + second));
    }

    [Fact]
    public void Examples_LinkOnlyWholeContentWordsAndCapEachSentence()
    {
        using var vault = new TempVault();
        var index = Index(vault, "the", "cat", "teacher", "school", "book", "resilient");
        var source = Section("examples", "1. The **resilient** cat met a teacher at school with a book and cats.\n   中文里 teacher 不链接。");

        var actual = ManagedLinks.RefreshNew(source, "resilient", index);

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
        var source = Section("relations", "- introduce：解释里 introduce 保持普通文字\n- record：不猜同名目标\n- unknown：暂无卡片");

        var actual = ManagedLinks.RefreshNew(source, "present", new(notes));

        Assert.Contains("- [[英语单词/动词/introduce|introduce]]：解释里 introduce 保持普通文字", actual);
        Assert.Contains("- record：不猜同名目标", actual);
        Assert.Contains("- unknown：暂无卡片", actual);
    }

    [Fact]
    public void Refresh_AddsFutureLinksRemovesMissingLinksAndRemainsIdempotent()
    {
        using var vault = new TempVault();
        var first = vault.Note("英语单词/teacher.md");
        var source = "Personal text teacher\n" + Section("examples", "1. A teacher has a book.");
        var beforeBook = ManagedLinks.RefreshNew(source, "learn", new([first]));
        var firstState = ManagedLinks.CaptureState(beforeBook);
        var book = vault.Note("英语单词/名词/book.md");

        var afterBook = ManagedLinks.Refresh(beforeBook, "learn", new([first, book]), firstState);
        var nextState = ManagedLinks.AdvanceState(beforeBook, afterBook, firstState);

        Assert.Contains("A [[英语单词/teacher|teacher]] has a [[英语单词/名词/book|book]].", afterBook);
        Assert.Equal(afterBook, ManagedLinks.Refresh(afterBook, "learn", new([first, book]), nextState));
        File.Delete(first.AbsolutePath);
        var removed = ManagedLinks.Refresh(afterBook, "learn", new([book]), nextState);
        Assert.Contains("A teacher has a [[英语单词/名词/book|book]].", removed);
        Assert.StartsWith("Personal text teacher\n", removed);
    }

    [Fact]
    public void UserEditedBlockAndUnmanagedTextAreNeverRewritten()
    {
        using var vault = new TempVault();
        var original = "1. teacher outside section\n" + Section("examples", "1. A teacher.");
        var source = original.Replace("A teacher.", "My teacher.");

        Assert.Equal(source, ManagedLinks.Refresh(source, "learn", Index(vault, "teacher"), ManagedLinks.CaptureState(original)));
    }

    [Fact]
    public void EscapedLinksCodeAndMarkdownLinksAreNotTurnedIntoRealLinks()
    {
        using var vault = new TempVault();
        var source = Section("examples", @"1. \[\[teacher\]\] `teacher` [teacher](https://example.test) &#91;&#91;teacher&#93;&#93; teacher.");

        var actual = ManagedLinks.RefreshNew(source, "learn", Index(vault, "teacher"));

        Assert.Contains(@"\[\[teacher\]\] `teacher` [teacher](https://example.test) &#91;&#91;teacher&#93;&#93; [[英语单词/teacher|teacher]].", actual);
    }

    [Fact]
    public void Refresh_DoesNotStripLinkSyntaxShownAsCodeOrEscapedText()
    {
        using var vault = new TempVault();
        var source = Section("examples", @"1. `[[英语单词/teacher|teacher]]` \[\[teacher\]\] teacher.");

        var actual = ManagedLinks.RefreshNew(source, "learn", Index(vault, "teacher"));

        Assert.Contains(@"`[[英语单词/teacher|teacher]]` \[\[teacher\]\] [[英语单词/teacher|teacher]].", actual);
    }

    private static VaultIndex Index(TempVault vault, params string[] words) => new(words.Select(w => vault.Note("英语单词/" + w + ".md")).ToArray());

    private static string Section(string mode, string body) => $"#### {(mode == "examples" ? "例句" : "语义关系")}\n{body}\n";

    private static string LegacyWrap(string mode, string body)
    {
        var normalized = body.Replace("\r\n", "\n").TrimEnd('\n');
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        return $"<!-- vocabcollector:links:v1 mode={mode} sha256={hash} -->\n{normalized}\n<!-- /vocabcollector:links -->";
    }
}
