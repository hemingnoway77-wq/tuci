using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using VocabCollector.Core;
using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public class LibraryCatalogTests
{
    [Fact]
    public async Task Scan_ExcludesApplicationMetadataAndHiddenMarkdownFromLearning()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "# apple");
        fixture.Write(".obsidian/private.md", "private workspace state");
        fixture.Write(".vocabcollector/backups/old.md", "historical managed source");
        fixture.Write(".git/example.md", "not a learning note");
        fixture.Write(".hidden.md", "hidden file");
        var note = Assert.Single((await fixture.Catalog.ScanAsync()).Notes);
        Assert.Equal("apple", note.Title);
    }

    [Fact]
    public async Task Scan_ReadsOrdinaryMarkdownAndCreationDateWithoutChangingSources()
    {
        using var fixture = new LibraryFixture();
        var original = "---\ncreated: '2024-02-03T10:20:30.123+08:00'\n---\n# apple\n**释义**：\n> n. 苹果\n";
        var file = fixture.Write("名词/apple.md", original);
        fixture.Write("词根/act.md", "# act");
        fixture.Write("说明/学习方法.MD", "# 学习方法\n普通中文笔记。");
        fixture.Write("ignored.txt", "ignored");

        var snapshot = await fixture.Catalog.ScanAsync();

        Assert.Equal(3, snapshot.Notes.Count);
        var word = Assert.Single(snapshot.Notes, n => n.Title == "apple");
        Assert.Equal(DateTimeOffset.Parse("2024-02-03T10:20:30.123+08:00"), word.AddedAt);
        Assert.Single(snapshot.Notes, n => n.IsWord);
        Assert.Equal(original, await File.ReadAllTextAsync(file));
        Assert.Empty(snapshot.Warnings);
        Assert.DoesNotContain(Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories), p => p.EndsWith(".json"));
    }

    [Fact]
    public async Task StableIdentity_SurvivesContentEditsAndUniqueUnchangedRenameAcrossInstances()
    {
        using var fixture = new LibraryFixture();
        var file = fixture.Write("apple.md", "# apple");
        var before = Assert.Single((await fixture.Catalog.ScanAsync()).Notes);
        await File.WriteAllTextAsync(file, "# apple\nmanual edit");
        var edited = Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes);
        Assert.Equal(before.Id, edited.Id);
        Assert.Equal(before.AddedAt, edited.AddedAt);
        Directory.CreateDirectory(Path.Combine(fixture.WordRoot, "名词"));
        File.Move(file, Path.Combine(fixture.WordRoot, "名词", "renamed.md"));
        var renamed = Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes);
        Assert.Equal(before.Id, renamed.Id);
        Assert.Equal(before.AddedAt, renamed.AddedAt);
        fixture.Write("copy.md", renamed.Markdown);
        var copied = (await fixture.NewCatalog().ScanAsync()).Notes;
        Assert.Equal(2, copied.Select(n => n.Id).Distinct().Count());
    }

    [Fact]
    public async Task LockedNote_PreservesOldIdentityAndNewReadableNotesStayStableAcrossPartialScans()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Write("apple.md", "original apple");
        var original = Assert.Single((await fixture.Catalog.ScanAsync()).Notes);
        LibraryNote fresh;
        await using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            fixture.Write("banana.md", "a readable new note");
            var partial = await fixture.NewCatalog().ScanAsync();
            fresh = Assert.Single(partial.Notes);
            Assert.Equal("banana", fresh.Title);
            Assert.NotEmpty(partial.Warnings);
            Assert.Equal(fresh.Id, Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes).Id);
        }
        var recovered = (await fixture.NewCatalog().ScanAsync()).Notes;
        Assert.Equal(original.Id, Assert.Single(recovered, note => note.Title == "apple").Id);
        Assert.Equal(original.AddedAt, Assert.Single(recovered, note => note.Title == "apple").AddedAt);
        Assert.Equal(fresh.Id, Assert.Single(recovered, note => note.Title == "banana").Id);
    }

    [Fact]
    public async Task LockedOriginalAndReadableCopy_DoNotAssignTheOriginalIdentityToTheCopy()
    {
        using var fixture = new LibraryFixture();
        var path = fixture.Write("apple.md", "identical content");
        var original = Assert.Single((await fixture.Catalog.ScanAsync()).Notes);
        LibraryNote copy;
        await using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            fixture.Write("copy.md", "identical content");
            copy = Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes);
            Assert.NotEqual(original.Id, copy.Id);
        }
        var recovered = (await fixture.NewCatalog().ScanAsync()).Notes;
        Assert.Equal(original.Id, Assert.Single(recovered, note => note.Title == "apple").Id);
        Assert.Equal(copy.Id, Assert.Single(recovered, note => note.Title == "copy").Id);
    }

    [Fact]
    public async Task UnavailableDirectory_PreservesItsNotesIdentityWhileOtherNotesAreIndexed()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("restricted/apple.md", "apple behind temporary directory permissions");
        var original = Assert.Single((await fixture.Catalog.ScanAsync()).Notes);
        var directory = new DirectoryInfo(Path.Combine(fixture.WordRoot, "restricted"));
        var temporarySecurity = directory.GetAccessControl();
        var denyRule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        temporarySecurity.AddAccessRule(denyRule);
        directory.SetAccessControl(temporarySecurity);
        LibraryNote fresh;
        try
        {
            fixture.Write("banana.md", "new note outside restricted directory");
            var partial = await fixture.NewCatalog().ScanAsync();
            fresh = Assert.Single(partial.Notes);
            Assert.Equal("banana", fresh.Title);
            Assert.NotEmpty(partial.Warnings);
        }
        finally
        {
            var restoredSecurity = directory.GetAccessControl();
            restoredSecurity.RemoveAccessRuleSpecific(denyRule);
            directory.SetAccessControl(restoredSecurity);
        }
        var recovered = (await fixture.NewCatalog().ScanAsync()).Notes;
        Assert.Equal(original.Id, Assert.Single(recovered, note => note.Title == "apple").Id);
        Assert.Equal(fresh.Id, Assert.Single(recovered, note => note.Title == "banana").Id);
    }

    [Fact]
    public async Task AmbiguousIdenticalRenames_DoNotGuessIdentity()
    {
        using var fixture = new LibraryFixture();
        var first = fixture.Write("one.md", "same");
        var second = fixture.Write("two.md", "same");
        var old = (await fixture.Catalog.ScanAsync()).Notes.Select(n => n.Id).ToArray();
        File.Move(first, Path.Combine(fixture.WordRoot, "three.md"));
        File.Move(second, Path.Combine(fixture.WordRoot, "four.md"));
        Assert.DoesNotContain((await fixture.NewCatalog().ScanAsync()).Notes, n => old.Contains(n.Id));
    }

    [Fact]
    public async Task ReusingOriginalPathAfterRename_DoesNotReuseTheRenamedNotesIdentity()
    {
        using var fixture = new LibraryFixture();
        var originalPath = fixture.Write("apple.md", "original");
        var original = Assert.Single((await fixture.Catalog.ScanAsync()).Notes);
        File.Move(originalPath, Path.Combine(fixture.WordRoot, "renamed.md"));
        var renamed = Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes);
        Assert.Equal(original.Id, renamed.Id);
        fixture.Write("apple.md", "a different note");
        var notes = (await fixture.NewCatalog().ScanAsync()).Notes;
        Assert.Equal(2, notes.Select(note => note.Id).Distinct().Count());
        Assert.Equal(original.Id, Assert.Single(notes, note => note.Title == "renamed").Id);
    }

    [Fact]
    public async Task RenameIntoDeletedNotesPath_FollowsUniqueContentInsteadOfTheOldTargetIdentity()
    {
        using var fixture = new LibraryFixture();
        var source = fixture.Write("apple.md", "the apple note");
        var target = fixture.Write("banana.md", "the banana note");
        var original = Assert.Single((await fixture.Catalog.ScanAsync()).Notes, note => note.Title == "apple");
        File.Delete(target);
        File.Move(source, target);
        var renamed = Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes);
        Assert.Equal(original.Id, renamed.Id);
        Assert.Equal(original.AddedAt, renamed.AddedAt);
    }

    [Fact]
    public async Task CorruptMetadata_IsReportedAndPreservedWhileNotesRemainReadable()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "manual");
        Directory.CreateDirectory(fixture.Metadata);
        var index = Path.Combine(fixture.Metadata, "catalog.json");
        await File.WriteAllTextAsync(index, "broken json");
        var snapshot = await fixture.Catalog.ScanAsync();
        Assert.Equal("manual", Assert.Single(snapshot.Notes).Markdown);
        Assert.NotEmpty(snapshot.Warnings);
        Assert.Equal("broken json", await File.ReadAllTextAsync(index));
    }

    [Fact]
    public async Task Resolve_PreservesNamesakesAndHonorsExplicitAndRelativePaths()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("名词/record.md", "noun");
        fixture.Write("动词/record.md", "verb");
        fixture.Write("名词/source.md", "source");
        var notes = (await fixture.Catalog.ScanAsync()).Notes;
        var source = Assert.Single(notes, n => n.Title == "source");
        Assert.Equal(2, fixture.Catalog.Resolve("RECORD#meaning|alias", source, notes).Count);
        Assert.Equal("动词/record.md", Assert.Single(fixture.Catalog.Resolve("动词/record", source, notes)).RelativePath);
        Assert.Equal("名词/record.md", Assert.Single(fixture.Catalog.Resolve("英语单词/名词/record.md", source, notes)).RelativePath);
        Assert.Equal("动词/record.md", Assert.Single(fixture.Catalog.Resolve("../动词/record", source, notes)).RelativePath);
        Assert.Empty(fixture.Catalog.Resolve("missing/record", source, notes));
        Assert.Empty(fixture.Catalog.Resolve("../../outside", source, notes));
        Assert.Empty(fixture.Catalog.Resolve("C:/outside/record", source, notes));
        Assert.Empty(fixture.Catalog.Resolve("%2e%2e/%2e%2e/outside", source, notes));
    }

    [Fact]
    public async Task Resolve_AcceptsPathsRelativeToNestedObsidianRootAndLegacyConfiguredRoot()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("vocab/名词/apple.md", "# apple");
        Directory.CreateDirectory(Path.Combine(fixture.WordRoot, ".obsidian"));
        var catalog = new LibraryCatalog(new(fixture.Root, "英语单词/vocab", true), fixture.Metadata);
        var note = Assert.Single((await catalog.ScanAsync()).Notes);
        Assert.Equal(note.Id, Assert.Single(catalog.Resolve("vocab/名词/apple", null, [note])).Id);
        Assert.Equal(note.Id, Assert.Single(catalog.Resolve("英语单词/vocab/名词/apple.md", null, [note])).Id);
        Assert.Equal(note.Id, Assert.Single(catalog.Resolve("名词/apple", null, [note])).Id);
        Assert.Empty(catalog.Resolve("vocab/missing/apple", null, [note]));
        Assert.Empty(catalog.Resolve("other/apple", null, [note]));
    }

    [Fact]
    public async Task Import_RejectsDuplicateBeforeOverwritingAnySource()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "existing manual content");
        var external = fixture.External("apple.md", "incoming");
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Catalog.ImportAsync([external]));
        Assert.Equal("existing manual content", await File.ReadAllTextAsync(Path.Combine(fixture.WordRoot, "apple.md")));
        var imported = Assert.Single(await fixture.Catalog.ImportAsync([fixture.External("学习方法.md", "# 中文笔记")]));
        Assert.Equal("学习方法", imported.Title);
        Assert.False(imported.IsWord);
        Assert.Equal("# 中文笔记", imported.Markdown);
    }

    [Fact]
    public async Task Scan_ReportsUnavailableMetadataButStillReadsUnmodifiedNotes()
    {
        using var fixture = new LibraryFixture();
        var file = fixture.Write("apple.md", "manual content");
        File.WriteAllText(fixture.Metadata, "block directory");
        var catalog = fixture.Catalog;
        var snapshot = await catalog.ScanAsync();
        Assert.Equal("manual content", Assert.Single(snapshot.Notes).Markdown);
        Assert.NotEmpty(snapshot.Warnings);
        Assert.Equal(Assert.Single(snapshot.Notes).Id, Assert.Single((await catalog.ScanAsync()).Notes).Id);
        Assert.Equal("manual content", await File.ReadAllTextAsync(file));
        File.Delete(fixture.Metadata);
        var savedAfterRecovery = await catalog.ScanAsync();
        Assert.Empty(savedAfterRecovery.Warnings);
        Assert.Equal(Assert.Single(snapshot.Notes).Id, Assert.Single(savedAfterRecovery.Notes).Id);
        Assert.Equal(Assert.Single(snapshot.Notes).Id, Assert.Single((await fixture.NewCatalog().ScanAsync()).Notes).Id);
    }

    [Fact]
    public async Task LocalState_IsolatedByWordRootAndOutsideVault()
    {
        using var fixture = new LibraryFixture();
        var first = new LibraryCatalog(new(fixture.Root, "first", true));
        var second = new LibraryCatalog(new(fixture.Root, "second", true));
        Assert.NotEqual(first.LocalStateDirectory, second.LocalStateDirectory);
        Assert.False(first.LocalStateDirectory.StartsWith(fixture.Root, StringComparison.OrdinalIgnoreCase));
        Assert.Empty((await fixture.Catalog.ScanAsync()).Notes);
    }

    [Fact]
    public async Task ScanAndImport_DoNotFollowDirectoryJunctionsOutsideWordRoot()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("safe.md", "safe");
        var outside = Path.Combine(fixture.Base, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.md"), "secret");
        var junction = Path.Combine(fixture.WordRoot, "linked");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/c", "mklink", "/J", junction, outside }, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            var scan = await fixture.Catalog.ScanAsync();
            Assert.Equal("safe", Assert.Single(scan.Notes).Title);
            Assert.NotEmpty(scan.Warnings);
            await Assert.ThrowsAnyAsync<ArgumentException>(() => new LibraryCatalog(new(fixture.Root, "英语单词/linked", true), Path.Combine(fixture.Base, "other-meta")).ImportAsync([fixture.External("incoming.md", "new")]));
            Assert.False(File.Exists(Path.Combine(outside, "incoming.md")));
        }
        finally { Directory.Delete(junction); }
    }
}

internal sealed class LibraryFixture : IDisposable
{
    public string Base { get; } = Path.Combine(Path.GetTempPath(), "tuci-library-tests-" + Guid.NewGuid().ToString("N"));
    public string Root => Path.Combine(Base, "vault");
    public string WordRoot => Path.Combine(Root, "英语单词");
    public string Metadata => Path.Combine(Base, "metadata");
    public LibraryCatalog Catalog => NewCatalog();
    public LibraryFixture() => Directory.CreateDirectory(WordRoot);
    public LibraryCatalog NewCatalog() => new(new(Root, "英语单词", true), Metadata);
    public string Write(string path, string content)
    {
        var absolute = Path.Combine(WordRoot, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        return absolute;
    }
    public string External(string name, string content)
    {
        var folder = Path.Combine(Base, "external");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }
    public void Dispose() { if (Directory.Exists(Base)) Directory.Delete(Base, true); }
}
