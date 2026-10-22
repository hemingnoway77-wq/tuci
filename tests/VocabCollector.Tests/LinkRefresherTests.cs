using VocabCollector.Core;
using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public class LinkRefresherTests
{
    [Fact]
    public async Task Refresh_LaterRootVariantCreationMoveDuplicateAndDeletionRevisitUnchangedSource()
    {
        using var vault = new TempVault(); var store = new VaultStore();
        const string clean = "# conduct\n\n英 /kənˈdʌkt/\n\n**词根**：duc / duct（引导）\n\n#### 例句\n1. We conduct a study.\n\n#### 我的笔记\nKeep my duc notes.\n";
        var source = await store.SaveNewAsync("conduct", PartOfSpeech.Verb, _ => clean, vault.Options, default);
        Assert.Equal(new LinkRefreshResult(0, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        vault.Note("英语单词/duc.md");
        Assert.Equal(new LinkRefreshResult(0, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        Assert.Equal(clean, await File.ReadAllTextAsync(source.Note.AbsolutePath));
        var variant = vault.Note("英语单词/词根/duct.md");

        Assert.Equal(new LinkRefreshResult(1, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        Assert.Contains("[[英语单词/词根/duct|", await File.ReadAllTextAsync(source.Note.AbsolutePath));
        var cache = await File.ReadAllTextAsync(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "link-index.json"));
        Assert.Contains("root:duct", cache);
        var moved = Path.Combine(vault.Root, "英语单词", "词根", "拉丁", "duct.md");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(variant.AbsolutePath, moved);

        Assert.Equal(new LinkRefreshResult(1, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        Assert.Contains("[[英语单词/词根/拉丁/duct|", await File.ReadAllTextAsync(source.Note.AbsolutePath));
        var duplicate = vault.Note("英语单词/词根/old#notes/duct.md");
        Assert.Equal(new LinkRefreshResult(1, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        Assert.Equal(clean, await File.ReadAllTextAsync(source.Note.AbsolutePath));
        File.Delete(duplicate.AbsolutePath);

        Assert.Equal(new LinkRefreshResult(1, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        Assert.Contains("[[英语单词/词根/拉丁/duct|", await File.ReadAllTextAsync(source.Note.AbsolutePath));
        File.Delete(moved);
        Assert.Equal(new LinkRefreshResult(1, 0, 0), await store.RefreshLinksAsync(vault.Options, default));
        Assert.Equal(clean, await File.ReadAllTextAsync(source.Note.AbsolutePath));
        Assert.DoesNotContain("vocabcollector", await File.ReadAllTextAsync(source.Note.AbsolutePath));
        Assert.Equal(5, Directory.GetFiles(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "backups"), "*.bak").Length);
    }

    [Fact]
    public async Task Refresh_EditedRootParagraphStaysProtectedWhileExampleLinksAdvance()
    {
        using var vault = new TempVault(); var store = new VaultStore();
        const string clean = "**词根**：duc / duct（引导）\n\n#### 例句\n1. A teacher sees a bird.\n\n#### 语义关系\n##### 近义词\n- study：学习。\n\n#### 我的笔记\n";
        var source = await store.SaveNewAsync("conduct", PartOfSpeech.Verb, _ => clean, vault.Options, default);
        var edited = clean.Replace("（引导）", "（我的词根解释）");
        await File.WriteAllTextAsync(source.Note.AbsolutePath, edited);
        vault.Note("英语单词/词根/duc.md");
        vault.Note("英语单词/teacher.md");

        Assert.Equal(new LinkRefreshResult(1, 1, 0), await store.RefreshLinksAsync(vault.Options, default));
        var changed = await File.ReadAllTextAsync(source.Note.AbsolutePath);
        Assert.Contains("**词根**：duc / duct（我的词根解释）", changed);
        Assert.Contains("[[英语单词/teacher|teacher]]", changed);
        Assert.DoesNotContain("[[英语单词/词根", changed);
        vault.Note("英语单词/bird.md");

        Assert.Equal(new LinkRefreshResult(1, 1, 0), await new VaultStore().RefreshLinksAsync(vault.Options, default));
        changed = await File.ReadAllTextAsync(source.Note.AbsolutePath);
        Assert.Contains("**词根**：duc / duct（我的词根解释）", changed);
        Assert.Contains("[[英语单词/bird|bird]]", changed);
    }

    [Fact]
    public async Task Refresh_V1OwnershipWithoutRootsRemainsReadableAndNeverAdoptsRootParagraph()
    {
        using var vault = new TempVault();
        const string oldClean = "#### 例句\n1. A teacher helps.\n\n#### 语义关系\n##### 近义词\n- study：学习。\n";
        var source = vault.Note("英语单词/learn.md", oldClean);
        var refresher = new LinkRefresher();
        await refresher.RegisterAsync(vault.Options, source, oldClean, default);
        var metadataPath = Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json");
        using (var metadata = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath)))
        {
            Assert.Equal(1, metadata.RootElement.GetProperty("Version").GetInt32());
            Assert.Equal(2, metadata.RootElement.GetProperty("Notes").GetProperty(source.VaultRelativePath).GetProperty("Regions").GetArrayLength());
        }
        const string userRoot = "**词根**：duc / duct（用户添加）\n\n";
        await File.WriteAllTextAsync(source.AbsolutePath, userRoot + oldClean);
        var teacher = vault.Note("英语单词/teacher.md");
        var root = vault.Note("英语单词/词根/duc.md");

        Assert.Equal(new LinkRefreshResult(1, 0, 0), await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher], [root]), default));

        var markdown = await File.ReadAllTextAsync(source.AbsolutePath);
        Assert.StartsWith(userRoot, markdown);
        Assert.Contains("[[英语单词/teacher|teacher]]", markdown);
        Assert.DoesNotContain("[[英语单词/词根", markdown);
    }

    [Fact]
    public async Task Refresh_UnrelatedRootCreationDoesNotReadUnchangedCachedSource()
    {
        using var vault = new TempVault();
        const string clean = "**词根**：duc / duct（引导）\n\n#### 例句\n1. A teacher helps.\n";
        var source = vault.Note("英语单词/conduct.md", clean);
        var refresher = new LinkRefresher();
        await refresher.RegisterAsync(vault.Options, source, clean, default);
        await refresher.RefreshAsync(vault.Options, new([source]), default);
        var unrelated = vault.Note("英语单词/词根/mit.md");
        await using var locked = new FileStream(source.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(new LinkRefreshResult(0, 0, 0), await new LinkRefresher().RefreshAsync(vault.Options, new([source], [unrelated]), default));
    }

    private static string LegacyWrap(string mode, string body)
    {
        var normalized = body.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        return $"<!-- vocabcollector:links:v1 mode={mode} sha256={hash} -->\n{normalized}\n<!-- /vocabcollector:links -->";
    }

    [Fact]
    public async Task Refresh_MetadataFailureAfterReplacementKeepsConservativeOwnership()
    {
        using var vault = new TempVault();
        const string clean = "#### 例句\n1. A teacher sees a bird.\n";
        var source = vault.Note("英语单词/learn.md", clean);
        var refresher = new LinkRefresher();
        await refresher.RegisterAsync(vault.Options, source, clean, default);
        var teacher = vault.Note("英语单词/teacher.md");
        var statePath = Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json");
        await using (var locked = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(new LinkRefreshResult(1, 0, 1), await refresher.RefreshAsync(vault.Options, new([source, teacher]), default));
        }
        var changed = await File.ReadAllTextAsync(source.AbsolutePath);
        Assert.Contains("[[英语单词/teacher|teacher]]", changed);
        var bird = vault.Note("英语单词/bird.md");

        Assert.Equal(new LinkRefreshResult(0, 1, 0), await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher, bird]), default));
        Assert.Equal(changed, await File.ReadAllTextAsync(source.AbsolutePath));
    }

    [Fact]
    public async Task Refresh_RegisteredCleanNoteSurvivesCacheLossWithoutAdoptingPersonalNote()
    {
        using var vault = new TempVault();
        const string clean = "#### 例句\n1. A teacher helps.\n\n#### 我的笔记\nMy teacher.\n";
        var source = vault.Note("英语单词/learn.md", clean);
        var personal = vault.Note("英语单词/personal.md", clean);
        var refresher = new LinkRefresher();
        await refresher.RegisterAsync(vault.Options, source, clean, default);
        await refresher.RefreshAsync(vault.Options, new([source, personal]), default);
        File.WriteAllText(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "link-index.json"), "corrupted");
        var teacher = vault.Note("英语单词/teacher.md");

        var result = await new LinkRefresher().RefreshAsync(vault.Options, new([source, personal, teacher]), default);

        Assert.Equal(new LinkRefreshResult(1, 0, 0), result);
        Assert.Contains("A [[英语单词/teacher|teacher]] helps.", await File.ReadAllTextAsync(source.AbsolutePath));
        Assert.DoesNotContain("vocabcollector", await File.ReadAllTextAsync(source.AbsolutePath));
        Assert.Equal(clean, await File.ReadAllTextAsync(personal.AbsolutePath));
        Assert.Contains("My teacher.", await File.ReadAllTextAsync(source.AbsolutePath));
        File.Delete(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "link-index.json"));
        File.Delete(teacher.AbsolutePath);
        Assert.Equal(1, (await new LinkRefresher().RefreshAsync(vault.Options, new([source, personal]), default)).Updated);
        Assert.Equal(clean, await File.ReadAllTextAsync(source.AbsolutePath));
    }

    [Fact]
    public async Task Refresh_UpdatingValidRelationNeverAdoptsEditedExampleOnLaterRefresh()
    {
        using var vault = new TempVault();
        const string clean = "#### 例句\n1. A teacher helps.\n\n#### 语义关系\n##### 近义词\n- study：学习。\n\n#### 我的笔记\n";
        var source = vault.Note("英语单词/learn.md", clean);
        var refresher = new LinkRefresher();
        await refresher.RegisterAsync(vault.Options, source, clean, default);
        await File.WriteAllTextAsync(source.AbsolutePath, clean.Replace("helps.", "helps me."));
        var study = vault.Note("英语单词/study.md");

        Assert.Equal(new LinkRefreshResult(1, 1, 0), await refresher.RefreshAsync(vault.Options, new([source, study]), default));
        Assert.Contains("[[英语单词/study|study]]", await File.ReadAllTextAsync(source.AbsolutePath));
        var teacher = vault.Note("英语单词/teacher.md");
        Assert.Equal(new LinkRefreshResult(0, 1, 0), await new LinkRefresher().RefreshAsync(vault.Options, new([source, study, teacher]), default));
        var markdown = await File.ReadAllTextAsync(source.AbsolutePath);
        Assert.Contains("A teacher helps me.", markdown);
        Assert.DoesNotContain("[[英语单词/teacher", markdown);
    }

    [Fact]
    public async Task Refresh_CorruptOwnershipCannotBeRebuiltByAdoptingPlainNotes()
    {
        using var vault = new TempVault();
        const string clean = "#### 例句\n1. A teacher helps.";
        var source = vault.Note("英语单词/learn.md", clean);
        var refresher = new LinkRefresher();
        await refresher.RegisterAsync(vault.Options, source, clean, default);
        var path = Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json");
        await File.WriteAllTextAsync(path, "broken");
        var teacher = vault.Note("英语单词/teacher.md");

        var result = await refresher.RefreshAsync(vault.Options, new([source, teacher]), default);

        Assert.Equal(0, result.Updated);
        Assert.True(result.Failed > 0);
        Assert.Equal(clean, await File.ReadAllTextAsync(source.AbsolutePath));
        Assert.Equal("broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Refresh_MetadataWriteFailureLeavesLegacyCommentsAndBodyUntouched()
    {
        using var vault = new TempVault();
        var legacy = LegacyWrap("examples", "#### 例句\n1. A teacher helps.");
        var source = vault.Note("英语单词/learn.md", legacy);
        var teacher = vault.Note("英语单词/teacher.md");
        var path = Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json");
        Directory.CreateDirectory(path);

        var result = await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher]), default);

        Assert.Equal(0, result.Updated);
        Assert.Equal(1, result.Failed);
        Assert.Equal(legacy, await File.ReadAllTextAsync(source.AbsolutePath));
    }

    [Fact]
    public async Task Refresh_UpdatesManagedNotesWithBackupAndIgnoresPersonalNotes()
    {
        using var vault = new TempVault();
        var original = "My notes stay here\n" + LegacyWrap("examples", "#### 例句\n1. A teacher can help.");
        var source = vault.Note("英语单词/learn.md", original);
        var teacher = vault.Note("英语单词/teacher.md");
        var personal = vault.Note("英语单词/personal.md", "1. A teacher can help.");

        var result = await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher, personal]), default);

        Assert.Equal(new LinkRefreshResult(1, 0, 0), result);
        Assert.Contains("[[英语单词/teacher|teacher]]", await File.ReadAllTextAsync(source.AbsolutePath));
        Assert.DoesNotContain("vocabcollector:links", await File.ReadAllTextAsync(source.AbsolutePath));
        Assert.Equal("1. A teacher can help.", await File.ReadAllTextAsync(personal.AbsolutePath));
        Assert.Equal(original, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "backups"), "*.bak"))));
    }

    [Fact]
    public async Task Refresh_LegacyEditedRegionIsCleanedWithoutAdoptingUserChanges()
    {
        using var vault = new TempVault();
        var legacy = LegacyWrap("examples", "#### 例句\n1. A teacher helps.");
        var edited = legacy.Replace("helps.", "helps me.");
        var source = vault.Note("英语单词/learn.md", edited);
        var teacher = vault.Note("英语单词/teacher.md");

        var result = await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher]), default);

        Assert.Equal(new LinkRefreshResult(1, 1, 0), result);
        var clean = await File.ReadAllTextAsync(source.AbsolutePath);
        Assert.DoesNotContain("vocabcollector:links", clean);
        Assert.Contains("A teacher helps me.", clean);
        Assert.DoesNotContain("[[", clean);
        File.Delete(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "link-index.json"));
        Assert.Equal(new LinkRefreshResult(0, 1, 0), await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher]), default));
        Assert.Equal(clean, await File.ReadAllTextAsync(source.AbsolutePath));
    }

    [Fact]
    public async Task Refresh_LaterTargetCreationAndDeletionRevisitUnchangedHistoricalSource()
    {
        using var vault = new TempVault();
        var source = vault.Note("英语单词/learn.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        var refresher = new LinkRefresher();
        Assert.Equal(new LinkRefreshResult(1, 0, 0), await refresher.RefreshAsync(vault.Options, new([source]), default));
        var teacher = vault.Note("英语单词/teacher.md");

        Assert.Equal(1, (await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher]), default)).Updated);
        Assert.Contains("[[英语单词/teacher|teacher]]", await File.ReadAllTextAsync(source.AbsolutePath));
        File.Delete(teacher.AbsolutePath);
        Assert.Equal(1, (await new LinkRefresher().RefreshAsync(vault.Options, new([source]), default)).Updated);
        Assert.Contains("A teacher helps.", await File.ReadAllTextAsync(source.AbsolutePath));
    }

    [Fact]
    public async Task Refresh_UnchangedAndUserModifiedBlocksAreSkippedWithoutExtraBackups()
    {
        using var vault = new TempVault();
        var source = vault.Note("英语单词/learn.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        var teacher = vault.Note("英语单词/teacher.md");
        var index = new VaultIndex([source, teacher]);
        var refresher = new LinkRefresher();
        await refresher.RefreshAsync(vault.Options, index, default);
        var generated = await File.ReadAllTextAsync(source.AbsolutePath);
        Assert.Equal(new LinkRefreshResult(0, 0, 0), await refresher.RefreshAsync(vault.Options, index, default));
        var edited = generated.Replace("helps.", "helps me.");
        await File.WriteAllTextAsync(source.AbsolutePath, edited);
        Assert.Equal(new LinkRefreshResult(0, 1, 0), await refresher.RefreshAsync(vault.Options, index, default));
        Assert.Equal(edited, await File.ReadAllTextAsync(source.AbsolutePath));
        Assert.Single(Directory.GetFiles(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "backups"), "*.bak"));
    }

    [Fact]
    public async Task Refresh_NewUnlinkableDuplicateInvalidatesExistingLink()
    {
        using var vault = new TempVault();
        var source = vault.Note("英语单词/learn.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        var teacher = vault.Note("英语单词/teacher.md");
        await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher]), default);
        var duplicate = vault.Note("英语单词/old#notes/teacher.md");

        var result = await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher, duplicate]), default);

        Assert.Equal(1, result.Updated);
        Assert.Contains("A teacher helps.", await File.ReadAllTextAsync(source.AbsolutePath));
    }

    [Fact]
    public async Task Refresh_UnrelatedTargetCreationDoesNotReadUnchangedCachedSource()
    {
        using var vault = new TempVault();
        var source = vault.Note("英语单词/learn.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        await new LinkRefresher().RefreshAsync(vault.Options, new([source]), default);
        var unrelated = vault.Note("英语单词/book.md");
        await using var locked = new FileStream(source.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await new LinkRefresher().RefreshAsync(vault.Options, new([source, unrelated]), default);

        Assert.Equal(new LinkRefreshResult(0, 0, 0), result);
    }

    [Fact]
    public async Task Refresh_LockedFileFailureDoesNotBlockAnotherFile()
    {
        using var vault = new TempVault();
        var source = vault.Note("英语单词/learn.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        var other = vault.Note("英语单词/study.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        var teacher = vault.Note("英语单词/teacher.md");
        await using var locked = new FileStream(source.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await new LinkRefresher().RefreshAsync(vault.Options, new([source, other, teacher]), default);

        Assert.Equal(new LinkRefreshResult(1, 0, 1), result);
        Assert.Contains("[[英语单词/teacher|teacher]]", await File.ReadAllTextAsync(other.AbsolutePath));
    }

    [Fact]
    public async Task Refresh_RejectsBackupDirectoryJunction()
    {
        using var vault = new TempVault(); using var outside = new TempVault();
        var source = vault.Note("英语单词/learn.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        var teacher = vault.Note("英语单词/teacher.md");
        var original = await File.ReadAllTextAsync(source.AbsolutePath);
        var state = Path.Combine(vault.Root, "英语单词", ".vocabcollector");
        Directory.CreateDirectory(state);
        var link = Path.Combine(state, "backups");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, outside.Root }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var result = await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher]), default);
            Assert.Equal(new LinkRefreshResult(0, 0, 1), result);
            Assert.Equal(original, await File.ReadAllTextAsync(source.AbsolutePath));
            Assert.Empty(Directory.GetFiles(outside.Root));
        }
        finally { Directory.Delete(link); }
        Assert.Equal(1, (await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher]), default)).Updated);
        Assert.Contains("[[英语单词/teacher|teacher]]", await File.ReadAllTextAsync(source.AbsolutePath));
    }

    [Fact]
    public async Task Refresh_RejectsOutsideNoteAndContinuesWithOtherManagedNotes()
    {
        using var vault = new TempVault(); using var outside = new TempVault();
        var source = vault.Note("英语单词/learn.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        var teacher = vault.Note("英语单词/teacher.md");
        var other = outside.Note("outside.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        var original = await File.ReadAllTextAsync(other.AbsolutePath);

        var result = await new LinkRefresher().RefreshAsync(vault.Options, new([other, source, teacher]), default);

        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Updated);
        Assert.Equal(original, await File.ReadAllTextAsync(other.AbsolutePath));
    }

    [Fact]
    public async Task Refresh_CancellationDoesNotModifyFiles()
    {
        using var vault = new TempVault();
        var source = vault.Note("英语单词/learn.md", LegacyWrap("examples", "#### 例句\n1. A teacher helps."));
        var original = await File.ReadAllTextAsync(source.AbsolutePath);
        using var cts = new CancellationTokenSource(); cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LinkRefresher().RefreshAsync(vault.Options, new([source]), cts.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(source.AbsolutePath));
    }
}
