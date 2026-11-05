using System.Windows;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

public sealed class LibraryShellTests
{
    [Fact]
    public void ConfigureOpensRealNoteAndBuildsCombinedTree() => MarkdownPreviewTests.OnSta(() =>
    {
        using var fixture = new LibraryFixture();
        fixture.Write("资料/读书.md", "# 读书\n\n正文。");
        var catalog = fixture.Catalog;
        var snapshot = catalog.ScanAsync().GetAwaiter().GetResult();
        using var shell = new LibraryShell();
        shell.Configure(catalog, snapshot.Notes, fixture.WordRoot);
        shell.Documents.OpenNote(snapshot.Notes.Single());
        Assert.Equal("读书", shell.Documents.Note?.Title);
        var folder = Assert.Single(shell.Tree);
        Assert.True(folder.IsFolder);
        Assert.Equal("读书", Assert.Single(folder.Children).Name);
    });
}
