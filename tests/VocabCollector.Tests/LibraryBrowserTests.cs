using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Core.Library;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Tests;

public sealed class LibraryBrowserTests
{
    [Fact]
    public void MissingCaptureLinkSynchronizesPreloadedSourceSelectionWithoutNavigation() => Run((browser, fixture, notes) =>
    {
        var source = notes.Single(note => note.Title == "apple");
        browser.OpenNote(notes.Single(note => note.Title == "中文摘记"));
        using var capture = new MarkdownPreview { Markdown = source.Markdown };
        var navigations = 0;
        browser.NavigationRequested += (_, _) => navigations++;
        browser.HandleLink(new PreviewLinkEventArgs("missing-target", true), source, capture);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        Assert.Equal(source.Id, browser.Reader.Note?.Id);
        Assert.Equal(source.Id, Assert.IsType<LibraryTreeEntry>(Tree(browser).SelectedItem).Note?.Id);
        Assert.Equal(0, navigations);
    });
    [Fact]
    public void ExplicitOpenAfterRefreshWinsOverQueuedOldTreeSelection() => Run((browser, fixture, notes) =>
    {
        var first = notes.Single(note => note.Title == "apple");
        var target = notes.Single(note => note.Title == "中文摘记");
        browser.OpenNote(first);
        browser.Configure(fixture.Catalog, notes.Select(note => note with { }).ToArray(), fixture.WordRoot);
        browser.OpenNote(target);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(80));
        Assert.Equal(target.Id, browser.Reader.Note?.Id);
        Assert.Equal(target.Id, Assert.IsType<LibraryTreeEntry>(Tree(browser).SelectedItem).Note?.Id);
    });
    [Fact]
    public void CombinedTreeSearchesFileNamesAndManualMarkdownWithoutChangingSource() => Run((browser, fixture, notes) =>
    {
        Assert.Equal(2, browser.VisibleNotes.Count);
        Assert.Contains(browser.Tree, entry => entry.IsFolder && entry.RelativePath == "名词");
        Assert.Contains(browser.Tree, entry => entry.IsFolder && entry.RelativePath == "空目录");
        Assert.Contains(All(browser.Tree), entry => entry.Note?.Title == "中文摘记");
        var manual = notes.Single(note => note.Title == "中文摘记");
        browser.Query = "手写正文";
        Assert.Equal(manual.Id, Assert.Single(browser.VisibleNotes).Id);
        Assert.Single(All(browser.Tree), entry => entry.Note is not null);
        browser.Query = "apple";
        Assert.Equal("apple", Assert.Single(browser.VisibleNotes).Title);
        Assert.Equal(manual.Markdown, File.ReadAllText(manual.AbsolutePath));
    });

    [Fact]
    public void RefreshPreservesFolderCollapseSelectionDocumentAndReadingOffset() => Run((browser, fixture, notes) =>
    {
        var source = notes.Single(note => note.Title == "apple");
        browser.OpenNote(source);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        var preview = Descendants<MarkdownPreview>(browser.Reader).Single();
        preview.ScrollToVerticalOffset(280);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
        var document = preview.Document;
        var folder = browser.Tree.Single(entry => entry.RelativePath == "名词");
        folder.IsExpanded = false;
        var selected = Assert.IsType<LibraryTreeEntry>(Tree(browser).SelectedItem);
        browser.Configure(fixture.Catalog, notes.Select(note => note with { }).Reverse().ToArray(), fixture.WordRoot);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        Assert.Same(folder, browser.Tree.Single(entry => entry.RelativePath == "名词"));
        Assert.False(folder.IsExpanded);
        Assert.Same(selected, Tree(browser).SelectedItem);
        Assert.Same(document, preview.Document);
        Assert.InRange(preview.VerticalOffset, 278, 282);
        Assert.Equal(source.Id, browser.Reader.Note?.Id);
    });

    [Fact]
    public void SidebarCanCollapseAndRestoreWithoutChangingReaderAndRefreshIsAnEvent() => Run((browser, fixture, notes) =>
    {
        browser.OpenNote(notes[0]);
        var preview = Descendants<MarkdownPreview>(browser.Reader).Single();
        var document = preview.Document;
        var pane = Assert.IsType<Border>(browser.FindName("FileListPane"));
        var originalWidth = pane.ActualWidth;
        Click(browser, "CollapseFilesButton");
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(200));
        Assert.Equal(Visibility.Collapsed, pane.Visibility);
        Click(browser, "ShowFilesButton");
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(200));
        Assert.Equal(Visibility.Visible, pane.Visibility);
        Assert.InRange(pane.ActualWidth, originalWidth - 2, originalWidth + 2);
        Assert.Same(document, preview.Document);
        var reloads = 0;
        browser.ReloadRequested += (_, _) => reloads++;
        Click(browser, "ReloadLibraryButton");
        Assert.Equal(1, reloads);
    });

    [Fact]
    public void UsesClientThemesAndIgnoresOldWorkspacePreferences() => Run((browser, fixture, notes) =>
    {
        Directory.CreateDirectory(fixture.Metadata);
        File.WriteAllText(Path.Combine(fixture.Metadata, "workspace-ui.json"), "{\"Dark\":true,\"LeftOpen\":false}");
        browser.Configure(fixture.Catalog, notes, fixture.WordRoot);
        browser.OpenNote(notes[0]);
        var preview = Descendants<MarkdownPreview>(browser.Reader).Single();
        var document = preview.Document;
        var bodySize = document.FontSize;
        var owner = Window.GetWindow(browser);
        ResourceDictionary? previous = null;
        foreach (var theme in new[] { AppTheme.WarmNeutral, AppTheme.Mono, AppTheme.Pink })
        {
            if (previous is not null) owner.Resources.MergedDictionaries.Remove(previous);
            previous = ThemePalette.Create(theme); owner.Resources.MergedDictionaries.Add(previous);
            browser.UpdateLayout();
            Assert.Same(previous["Sheet"], Assert.IsType<Border>(browser.FindName("ReaderPane")).Background);
            Assert.Same(document, preview.Document);
            Assert.Equal(bodySize, document.FontSize);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(browser.FindName("FileListPane")).Visibility);
        }
        Assert.Empty(browser.Resources.MergedDictionaries);
    });

    [Fact]
    public void SourceLinksPreserveCaptureOffsetAndControlClickStaysInSameReader() => Run((browser, fixture, notes) =>
    {
        var source = notes.Single(note => note.Title == "apple");
        var target = notes.Single(note => note.Title == "中文摘记");
        using var capture = new MarkdownPreview { Markdown = source.Markdown };
        var owner = new Window { Content = capture, Width = 600, Height = 400, ShowInTaskbar = false };
        try
        {
            owner.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
            capture.ScrollToVerticalOffset(280); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
            browser.OpenNote(source);
            browser.HandleLink(new PreviewLinkEventArgs("中文摘记", true, modifiers: System.Windows.Input.ModifierKeys.Control), source, capture);
            Assert.Equal(target.Id, browser.Reader.Note?.Id);
            Click(browser, "BrowserBackButton");
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
            Assert.Equal(source.Id, browser.Reader.Note?.Id);
            Assert.InRange(browser.Reader.VerticalOffset, 278, 282);
            Click(browser, "BrowserForwardButton");
            Assert.Equal(target.Id, browser.Reader.Note?.Id);
        }
        finally { owner.Close(); }
    });

    private static void Run(Action<LibraryBrowser, LibraryFixture, IReadOnlyList<LibraryNote>> action) => MarkdownPreviewTests.OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var fixture = new LibraryFixture();
        fixture.Write("名词/apple.md", "# apple\n\n[[中文摘记]]\n\n" + string.Join("\n\n", Enumerable.Repeat("Long reading paragraph with selectable content.", 100)));
        fixture.Write("中文摘记.md", "# 中文摘记\n\n手写正文，保持原样。\n");
        Directory.CreateDirectory(Path.Combine(fixture.WordRoot, "空目录"));
        var scan = fixture.Catalog.ScanAsync();
        while (!scan.IsCompleted) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        var notes = scan.GetAwaiter().GetResult().Notes;
        using var browser = new LibraryBrowser();
        browser.Resources.MergedDictionaries.Clear();
        var owner = new Window { Content = browser, Width = 1000, Height = 600, ShowInTaskbar = false };
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try
        {
            browser.Configure(fixture.Catalog, notes, fixture.WordRoot);
            owner.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
            action(browser, fixture, notes);
        }
        finally { owner.Close(); }
    });
    private static TreeView Tree(LibraryBrowser browser) => Assert.IsType<TreeView>(browser.FindName("BrowserFilesTree"));
    private static void Click(LibraryBrowser browser, string name) => Assert.IsType<Button>(browser.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<LibraryTreeEntry> All(IEnumerable<LibraryTreeEntry> entries)
    { foreach (var entry in entries) { yield return entry; foreach (var child in All(entry.Children)) yield return child; } }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
