using VocabCollector.Core;
using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public class FormLinksTests
{
    [Theory]
    [InlineData(" introduce.md", "introduce")]
    [InlineData("introduce .md", "introduce")]
    [InlineData("can‘t.md", "can't")]
    public async Task Forms_RemoveGeneratedLinksToAcceptedNoncanonicalFileNames(string fileName, string canonicalWord)
    {
        using var vault = new TempVault();
        var store = new VaultStore();
        var target = vault.Note("英语单词/动词/" + fileName);
        var source = "**词形**：" + canonicalWord + "是这里记录的原形。\n";
        var scanned = await store.ScanAsync(vault.Options, default);
        Assert.Single(scanned.Find(canonicalWord));
        var linked = ManagedLinks.RefreshNew(source, "learning", scanned);
        Assert.Contains("[[英语单词/动词/" + fileName[..^3] + "|" + canonicalWord + "]]", linked);
        var state = ManagedLinks.CaptureState(linked);
        File.Delete(target.AbsolutePath);

        var actual = ManagedLinks.Refresh(linked, "learning", await store.ScanAsync(vault.Options, default), state);

        Assert.Equal(source, actual);
    }

    [Fact]
    public async Task Forms_FutureLemmaCreationInvalidatesTheLocalCacheAndDeletionRemovesItsLink()
    {
        using var vault = new TempVault();
        var store = new VaultStore();
        const string source = "**词形**：introduced是introduce的过去式。\n\n#### 例句\n1. She introduced me.\n";
        var saved = await store.SaveNewAsync("introduced", PartOfSpeech.Verb, _ => source, vault.Options, default);
        Assert.Equal(new LinkRefreshResult(0, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        var introduce = vault.Note("英语单词/动词/introduce.md");

        Assert.Equal(new LinkRefreshResult(1, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        Assert.Contains("是[[英语单词/动词/introduce|introduce]]的", await File.ReadAllTextAsync(saved.Note.AbsolutePath));
        File.Delete(introduce.AbsolutePath);
        Assert.Equal(new LinkRefreshResult(1, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        Assert.Equal(source, await File.ReadAllTextAsync(saved.Note.AbsolutePath));
    }

    [Theory]
    [InlineData("#### 例句\n1. A teacher helps.\n")]
    [InlineData("#### 语义关系\n- teacher：教师。\n")]
    [InlineData("**词形**：teacher是原形。\n")]
    public void GeneratedShortLinksCanRebindAndDisappearWithoutTouchingPersonalAliases(string source)
    {
        using var vault = new TempVault();
        var teacher = vault.Note("英语单词/teacher.md") with { ObsidianRelativePath = "teacher.md" };
        var linked = ManagedLinks.RefreshNew(source, "learn", new([teacher]));
        var state = ManagedLinks.CaptureState(linked);
        var moved = teacher with { ObsidianRelativePath = "名词/teacher.md" };

        Assert.Contains("[[teacher|teacher]]", linked);
        Assert.Equal(linked.Replace("[[teacher|teacher]]", "[[名词/teacher|teacher]]"),
            ManagedLinks.Refresh(linked, "learn", new([moved]), state));
        Assert.Equal(source, ManagedLinks.Refresh(linked, "learn", new([]), state));
        const string personal = "#### 例句\n1. [[my-card|teacher]] `[[teacher|teacher]]` \\[\\[teacher\\]\\] teacher.\n";
        Assert.Equal(personal, ManagedLinks.RefreshNew(personal, "learn", new([])));
    }

    [Fact]
    public void GeneratedShortRootLinksCanDisappearAndPersonalAliasesStayIntact()
    {
        using var vault = new TempVault();
        var root = vault.Note("英语单词/词根/duc.md") with { ObsidianRelativePath = "duc.md" };
        const string source = "**词根**：duc / duct（引导）\n";
        var linked = ManagedLinks.RefreshNew(source, "conduct", new([], [root]));

        Assert.Equal("**词根**：[[duc|duc / duct]]（引导）\n", linked);
        Assert.Equal(source, ManagedLinks.Refresh(linked, "conduct", new([]), ManagedLinks.CaptureState(linked)));
        const string personal = "**词根**：[[my-card|duc / duct]]（个人链接）\n";
        Assert.Equal(personal, ManagedLinks.RefreshNew(personal, "conduct", new([])));
    }

    [Fact]
    public void Forms_LinkTheExistingLemmaEvenWhenAdjacentToChineseText()
    {
        using var vault = new TempVault();
        var introduce = vault.Note("英语单词/动词/introduce.md");
        var introduced = vault.Note("英语单词/动词/introduced.md");
        const string source = "**词形**：introduced是introduce的过去式和过去分词。\n\n#### 例句\n1. She introduced me.\n";

        var actual = ManagedLinks.RefreshNew(source, "introduced", new([introduce, introduced]));

        Assert.Contains("**词形**：introduced是[[英语单词/动词/introduce|introduce]]的过去式和过去分词。", actual);
        Assert.DoesNotContain("|introduced]]", actual);
        Assert.Contains(ManagedLinks.CaptureState(actual), region => region.Mode == "forms");
        Assert.DoesNotContain("vocabcollector", actual);
    }

    [Fact]
    public void Forms_AnExistingLemmaCanBeAddedLaterAndIsNotAnInflectionGuess()
    {
        using var vault = new TempVault();
        const string source = "**词形**：introduced是introduce的过去式。introduces未收录。\n";
        var initial = ManagedLinks.RefreshNew(source, "introduced", new([]));
        var state = ManagedLinks.CaptureState(initial);
        var introduce = vault.Note("英语单词/动词/introduce.md");

        var actual = ManagedLinks.Refresh(initial, "introduced", new([introduce]), state);

        Assert.Equal(source, initial);
        Assert.Equal(source.Replace("是introduce的", "是[[英语单词/动词/introduce|introduce]]的"), actual);
    }

    [Theory]
    [InlineData("been", "be")]
    [InlineData("had", "have")]
    [InlineData("done", "do")]
    public void Forms_LinkFunctionWordLemmasThatExamplesNormallySkip(string inflection, string lemma)
    {
        using var vault = new TempVault();
        var target = vault.Note("英语单词/动词/" + lemma + ".md");
        var source = "**词形**：" + inflection + "是" + lemma + "的过去分词。\n";

        var actual = ManagedLinks.RefreshNew(source, inflection, new([target]));

        Assert.Equal(source.Replace("是" + lemma + "的", "是[[英语单词/动词/" + lemma + "|" + lemma + "]]的"), actual);
    }

    [Fact]
    public void Forms_ProtectPersonalSyntaxAndOnlyOwnTheUniqueVisibleParagraph()
    {
        using var vault = new TempVault();
        var introduce = vault.Note("英语单词/introduce.md");
        const string prefix = "```md\n**词形**：introduce\n```\n<!-- 私人注释\n**词形**：introduce\n-->\n\n";
        const string paragraph = "**词形**：`introduce` \\[introduce\\] [introduce](https://example.test) [[私人/别名|introduce]] introduce的过去式。\n";
        const string suffix = "\n我的说明 introduce。\n";
        var source = prefix + paragraph + suffix;

        var actual = ManagedLinks.RefreshNew(source, "introduced", new([introduce]));

        Assert.Equal(prefix + paragraph.Replace(" introduce的", " [[英语单词/introduce|introduce]]的") + suffix, actual);
        var duplicates = source + "\n**词形**：introduce。\n";
        Assert.Equal(duplicates, ManagedLinks.RefreshNew(duplicates, "introduced", new([introduce])));
        Assert.Empty(ManagedLinks.CaptureState(duplicates));
    }

    [Fact]
    public void Forms_ManualEditsAndOldRecordsWithoutFormOwnershipAreNotAdopted()
    {
        using var vault = new TempVault();
        var introduce = vault.Note("英语单词/introduce.md");
        const string original = "**词形**：introduced是introduce的过去式。\n\n#### 例句\n1. A teacher helps.\n";
        var originalState = ManagedLinks.CaptureState(original);
        var oldState = originalState.Where(region => region.Mode != "forms").ToArray();
        var edited = original.Replace("是introduce的", "是我理解的introduce的");

        Assert.Equal(original, ManagedLinks.Refresh(original, "introduced", new([introduce]), oldState));
        var actual = ManagedLinks.Refresh(edited, "introduced", new([introduce]), originalState);
        var advanced = ManagedLinks.AdvanceState(edited, actual, originalState);
        Assert.Equal(edited, actual);
        Assert.Equal(edited, ManagedLinks.Refresh(actual, "introduced", new([introduce]), advanced));
    }
}
