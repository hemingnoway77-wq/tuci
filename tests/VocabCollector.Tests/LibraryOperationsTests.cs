using System.Diagnostics;
using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public class LibraryOperationsTests
{
    [Theory]
    [InlineData("save")]
    [InlineData("create")]
    [InlineData("folder")]
    [InlineData("move")]
    [InlineData("trash")]
    [InlineData("restore")]
    [InlineData("import")]
    public async Task Writes_WaitForTheSharedCaptureAndSyncLockAndCancelWithoutChangingFiles(string action)
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "original");
        var catalog = fixture.Catalog;
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        var importedSource = fixture.External("incoming.md", "imported");
        var trash = action == "restore" ? await catalog.TrashNoteAsync(note) : null;
        var indexPath = Path.Combine(fixture.Metadata, "catalog.json");
        var originalIndex = await File.ReadAllTextAsync(indexPath);
        await using var locked = new FileStream(Path.Combine(fixture.WordRoot, ".vocabcollector.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(180));
        Task Start() => action switch
        {
            "save" => catalog.SaveNoteAsync(note, "edited", timeout.Token),
            "create" => catalog.CreateNoteAsync("new.md", "new", timeout.Token),
            "folder" => catalog.CreateFolderAsync("new-folder", timeout.Token),
            "move" => catalog.MoveNoteAsync(note, "moved.md", timeout.Token),
            "trash" => catalog.TrashNoteAsync(note, timeout.Token),
            "restore" => catalog.RestoreNoteAsync(trash!, timeout.Token),
            "import" => catalog.ImportAsync([importedSource], timeout.Token),
            _ => throw new InvalidOperationException()
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Start);
        Assert.Equal(originalIndex, await File.ReadAllTextAsync(indexPath));
        if (action == "restore")
        {
            Assert.False(File.Exists(note.AbsolutePath));
            Assert.Equal(trash, Assert.Single(await catalog.GetTrashAsync()));
        }
        else Assert.Equal("original", await File.ReadAllTextAsync(note.AbsolutePath));
        Assert.False(File.Exists(Path.Combine(fixture.WordRoot, "new.md")));
        Assert.False(File.Exists(Path.Combine(fixture.WordRoot, "moved.md")));
        Assert.False(File.Exists(Path.Combine(fixture.WordRoot, "incoming.md")));
        Assert.False(Directory.Exists(Path.Combine(fixture.WordRoot, "new-folder")));
    }

    [Fact]
    public async Task Create_CreatesNestedMarkdownAndFolderWithoutOverwriting()
    {
        using var fixture = new LibraryFixture();
        var catalog = fixture.Catalog;
        await catalog.CreateFolderAsync("新目录/子目录");
        Assert.True(Directory.Exists(Path.Combine(fixture.WordRoot, "新目录/子目录")));
        var note = await catalog.CreateNoteAsync("新目录/子目录/学习.md", "# 学习\n手写内容");
        Assert.Equal("新目录/子目录/学习.md", note.RelativePath);
        Assert.Equal(note.Markdown, await File.ReadAllTextAsync(note.AbsolutePath));
        await Assert.ThrowsAnyAsync<IOException>(() => catalog.CreateNoteAsync(note.RelativePath, "overwrite"));
        Assert.Equal("# 学习\n手写内容", await File.ReadAllTextAsync(note.AbsolutePath));
        Assert.Equal(note.Id, Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes).Id);
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("folder/../../outside.md")]
    [InlineData("C:/outside.md")]
    [InlineData(".obsidian/config.md")]
    [InlineData("folder/con.md")]
    [InlineData("folder./word.md")]
    [InlineData("folder/word#section.md")]
    [InlineData("folder/word^block.md")]
    [InlineData("folder/[word].md")]
    [InlineData("folder[hidden]/word.md")]
    public async Task CreateAndMove_RejectUnsafeRelativePaths(string path)
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "# apple");
        var catalog = fixture.Catalog;
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => catalog.CreateNoteAsync(path, "no"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => catalog.CreateFolderAsync(path));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => catalog.MoveNoteAsync(note, path));
        Assert.Equal(note.Markdown, await File.ReadAllTextAsync(note.AbsolutePath));
    }

    [Fact]
    public async Task Save_PreservesIdentityAndBacksUpTheExactPreviousBytes()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Write("apple.md", "# apple\r\n手写原文\r\n");
        var originalBytes = await File.ReadAllBytesAsync(path);
        var catalog = fixture.Catalog;
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        var saved = await catalog.SaveNoteAsync(note, "# apple\n改后的原文\n");
        Assert.Equal(note.Id, saved.Id);
        Assert.Equal(note.AddedAt, saved.AddedAt);
        Assert.Equal("# apple\n改后的原文\n", saved.Markdown);
        Assert.Equal(saved.Markdown, await File.ReadAllTextAsync(path));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(fixture.Metadata, "backups"), "*.md", SearchOption.AllDirectories));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backup));
        Assert.Equal(saved.Id, Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes).Id);
    }

    [Fact]
    public async Task Save_RejectsExternalContentOrIdentityChangesAndRetainsBothVersions()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Write("apple.md", "original");
        var catalog = fixture.Catalog;
        var expected = Assert.Single((await catalog.ScanAsync()).Notes);
        await File.WriteAllTextAsync(path, "outside editor");
        await Assert.ThrowsAsync<LibraryConflictException>(() => catalog.SaveNoteAsync(expected, "local draft"));
        Assert.Equal("outside editor", await File.ReadAllTextAsync(path));
        var current = Assert.Single((await catalog.ScanAsync()).Notes);
        await Assert.ThrowsAsync<LibraryConflictException>(() => catalog.SaveNoteAsync(current with { Id = "another-id" }, "local draft"));
        Assert.Equal("outside editor", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_BackupFailureLeavesTheSourceUntouched()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "original");
        var catalog = fixture.Catalog;
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        File.WriteAllText(Path.Combine(fixture.Metadata, "backups"), "block backups");
        await Assert.ThrowsAnyAsync<IOException>(() => catalog.SaveNoteAsync(note, "edited"));
        Assert.Equal("original", await File.ReadAllTextAsync(note.AbsolutePath));
    }

    [Fact]
    public async Task ConcurrentSaves_OnlyTheFirstExpectedRevisionCanCommit()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "original");
        var catalog = fixture.Catalog;
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        var first = catalog.SaveNoteAsync(note, "first edit");
        var second = catalog.SaveNoteAsync(note, "second edit");
        Assert.Equal("first edit", (await first).Markdown);
        await Assert.ThrowsAsync<LibraryConflictException>(() => second);
        Assert.Equal("first edit", await File.ReadAllTextAsync(note.AbsolutePath));
    }

    [Fact]
    public async Task UnwritableIdentityIndex_PreventsCreateMoveAndTrashFromChangingFiles()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "original");
        var catalog = fixture.Catalog;
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        var index = Path.Combine(fixture.Metadata, "catalog.json");
        await using (var locked = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => catalog.CreateNoteAsync("new.md", "new"));
            await Assert.ThrowsAnyAsync<IOException>(() => catalog.MoveNoteAsync(note, "moved.md"));
            await Assert.ThrowsAnyAsync<IOException>(() => catalog.TrashNoteAsync(note));
        }
        Assert.Equal("original", await File.ReadAllTextAsync(note.AbsolutePath));
        Assert.False(File.Exists(Path.Combine(fixture.WordRoot, "new.md")));
        Assert.False(File.Exists(Path.Combine(fixture.WordRoot, "moved.md")));
        Assert.Equal(note.Id, Assert.Single((await catalog.ScanAsync()).Notes).Id);
    }

    [Fact]
    public async Task Move_PreservesExplicitIdentityEvenWhenContentsHaveDuplicates()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "identical");
        fixture.Write("copy.md", "identical");
        var catalog = fixture.Catalog;
        var original = Assert.Single((await catalog.ScanAsync()).Notes, n => n.Title == "apple");
        var moved = await catalog.MoveNoteAsync(original, "新目录/renamed.md");
        Assert.Equal(original.Id, moved.Id);
        Assert.False(File.Exists(original.AbsolutePath));
        Assert.Equal("identical", await File.ReadAllTextAsync(moved.AbsolutePath));
        Assert.Equal(original.Id, Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes, n => n.Title == "renamed").Id);
        await Assert.ThrowsAnyAsync<IOException>(() => catalog.MoveNoteAsync(moved, "copy.md"));
    }

    [Fact]
    public async Task TrashAndRestore_PreserveIdentityAndNeverOverwriteAReplacement()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "original apple");
        var catalog = fixture.Catalog;
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        var item = await catalog.TrashNoteAsync(note);
        Assert.False(File.Exists(note.AbsolutePath));
        Assert.Empty((await catalog.ScanAsync()).Notes);
        Assert.Equal(item, Assert.Single(await fixture.NewCatalog().GetTrashAsync()));
        fixture.Write("apple.md", "replacement");
        await Assert.ThrowsAnyAsync<IOException>(() => catalog.RestoreNoteAsync(item));
        Assert.Equal("replacement", await File.ReadAllTextAsync(note.AbsolutePath));
        File.Delete(note.AbsolutePath);
        var restored = await fixture.NewCatalog().RestoreNoteAsync(item);
        Assert.Equal(note.Id, restored.Id);
        Assert.Equal(note.AddedAt, restored.AddedAt);
        Assert.Equal(note.Markdown, restored.Markdown);
        Assert.Empty(await fixture.NewCatalog().GetTrashAsync());
        Assert.Equal(note.Id, Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes).Id);
    }

    [Fact]
    public async Task StaleMoveAndTrash_RejectOutsideEditsAndForgedRestorePaths()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "original");
        var catalog = fixture.Catalog;
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        await File.WriteAllTextAsync(note.AbsolutePath, "external edit");
        await Assert.ThrowsAsync<LibraryConflictException>(() => catalog.MoveNoteAsync(note, "moved.md"));
        await Assert.ThrowsAsync<LibraryConflictException>(() => catalog.TrashNoteAsync(note));
        Assert.Equal("external edit", await File.ReadAllTextAsync(note.AbsolutePath));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => catalog.RestoreNoteAsync(new("../../outside", "apple.md", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Mutations_RefuseJunctionTargets()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "original");
        var catalog = fixture.Catalog;
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        var external = Path.Combine(fixture.Base, "external-junction");
        Directory.CreateDirectory(external);
        var junction = Path.Combine(fixture.WordRoot, "linked");
        using (var command = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/c", "mklink", "/J", junction, external }, RedirectStandardOutput = true, RedirectStandardError = true })!)
        { await command.WaitForExitAsync(); Assert.Equal(0, command.ExitCode); }
        try
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => catalog.CreateNoteAsync("linked/new.md", "new"));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => catalog.CreateFolderAsync("linked/folder"));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => catalog.MoveNoteAsync(note, "linked/apple.md"));
            Assert.Empty(Directory.GetFileSystemEntries(external));
        }
        finally { Directory.Delete(junction); }
    }
}
