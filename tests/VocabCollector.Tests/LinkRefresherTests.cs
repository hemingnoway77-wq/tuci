using VocabCollector.Core;
using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public class LinkRefresherTests
{
    [Fact]
    public async Task Refresh_UpdatesManagedNotesWithBackupAndIgnoresPersonalNotes()
    {
        using var vault = new TempVault();
        var original = "My notes stay here\n" + ManagedLinks.Wrap("examples", "1. A teacher can help.");
        var source = vault.Note("英语单词/learn.md", original);
        var teacher = vault.Note("英语单词/teacher.md");
        var personal = vault.Note("英语单词/personal.md", "1. A teacher can help.");

        var result = await new LinkRefresher().RefreshAsync(vault.Options, new([source, teacher, personal]), default);

        Assert.Equal(new LinkRefreshResult(1, 0, 0), result);
        Assert.Contains("[[英语单词/teacher|teacher]]", await File.ReadAllTextAsync(source.AbsolutePath));
        Assert.Equal("1. A teacher can help.", await File.ReadAllTextAsync(personal.AbsolutePath));
        Assert.Equal(original, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "backups"), "*.bak"))));
    }

    [Fact]
    public async Task Refresh_LaterTargetCreationAndDeletionRevisitUnchangedHistoricalSource()
    {
        using var vault = new TempVault();
        var source = vault.Note("英语单词/learn.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
        var refresher = new LinkRefresher();
        Assert.Equal(new LinkRefreshResult(0, 0, 0), await refresher.RefreshAsync(vault.Options, new([source]), default));
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
        var source = vault.Note("英语单词/learn.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
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
        var source = vault.Note("英语单词/learn.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
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
        var source = vault.Note("英语单词/learn.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
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
        var source = vault.Note("英语单词/learn.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
        var other = vault.Note("英语单词/study.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
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
        var source = vault.Note("英语单词/learn.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
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
    }

    [Fact]
    public async Task Refresh_RejectsOutsideNoteAndContinuesWithOtherManagedNotes()
    {
        using var vault = new TempVault(); using var outside = new TempVault();
        var source = vault.Note("英语单词/learn.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
        var teacher = vault.Note("英语单词/teacher.md");
        var other = outside.Note("outside.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
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
        var source = vault.Note("英语单词/learn.md", ManagedLinks.Wrap("examples", "1. A teacher helps."));
        var original = await File.ReadAllTextAsync(source.AbsolutePath);
        using var cts = new CancellationTokenSource(); cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LinkRefresher().RefreshAsync(vault.Options, new([source]), cts.Token));
        Assert.Equal(original, await File.ReadAllTextAsync(source.AbsolutePath));
    }
}
