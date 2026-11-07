using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core.Library;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;
using Xunit.Abstractions;

namespace VocabCollector.Tests;

[Collection("Library browser WPF input")]
public sealed class LibraryBrowserTests(ITestOutputHelper output)
{
    [Fact]
    public void FolderSelectionHasNoPersistentFillAfterThePointerLeavesItsOwnRow() => Run((browser, _, _) =>
    {
        var folder = Descendants<TreeViewItem>(Tree(browser)).Single(item => item.DataContext is LibraryTreeEntry { RelativePath: "名词" });
        var row = Assert.IsType<Border>(folder.Template.FindName("Row", folder));
        var previous = Mouse.DirectlyOver;
        try
        {
            Select(folder); MovePointer(Assert.IsType<TextBox>(browser.FindName("LibrarySearchBox")));
            Assert.True(folder.IsSelected); Assert.Equal(MouseButtonState.Released, Mouse.LeftButton);
            Assert.False(row.IsMouseOver);
            Assert.Equal(0, Assert.IsType<SolidColorBrush>(row.Background).Color.A);
        }
        finally { SetHover(previous); }
    });

    [Fact]
    public void HoveringANestedFileDoesNotPaintItsAncestorFolderHeaders() => Run((browser, fixture, _) =>
    {
        fixture.Write("capture/nested/conform.md", "# conform\n\nNested file.");
        var scan = fixture.Catalog.ScanAsync(); while (!scan.IsCompleted) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        browser.Configure(fixture.Catalog, scan.GetAwaiter().GetResult().Notes, fixture.WordRoot);
        Window.GetWindow(browser).UpdateLayout(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
        var items = Descendants<TreeViewItem>(Tree(browser)).ToArray();
        var file = items.Single(item => item.DataContext is LibraryTreeEntry { RelativePath: "capture/nested/conform.md" });
        var row = Assert.IsType<Border>(file.Template.FindName("Row", file));
        var previous = Mouse.DirectlyOver;
        var owner = Window.GetWindow(browser);
        try
        {
            Select(file); MovePointer(row);
            foreach (var path in new[] { "capture", "capture/nested" })
            {
                var folder = items.Single(item => item.DataContext is LibraryTreeEntry entry && entry.RelativePath == path);
                var header = Assert.IsType<Border>(folder.Template.FindName("Row", folder));
                Assert.True(folder.IsMouseOver, "The pointer must really exercise descendant-to-ancestor IsMouseOver propagation.");
                Assert.False(header.IsMouseOver);
                Assert.Equal(0, Assert.IsType<SolidColorBrush>(header.Background).Color.A);
            }
            MovePointer(Assert.IsType<TextBox>(browser.FindName("LibrarySearchBox")));
            Assert.True(file.IsSelected);
            Assert.Same(owner.FindResource("Selection"), row.Background);
            UiRegressionSnapshot.Save(owner, "library-nested-selection.png");
        }
        finally { SetHover(previous); }
    });

    private static void Select(TreeViewItem item) => ((ISelectionItemProvider)new TreeViewItemAutomationPeer(item).GetPattern(PatternInterface.SelectionItem)!).Select();
    private static void MovePointer(FrameworkElement element)
    {
        var owner = Window.GetWindow(element);
        var point = element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), owner);
        var hit = owner.InputHitTest(point);
        Assert.NotNull(hit);
        SetHover(hit);
    }
    private static void SetHover(IInputElement? hit)
    {
        // Feed a real visual hit into WPF's own reverse-inherited mouse state.
        // OS cursor coordinates are DPI-virtualized in the test runner and do not
        // reliably address its independent STA windows on mixed-DPI desktops.
        typeof(MouseDevice).GetMethod("ChangeMouseOver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Mouse.PrimaryDevice, [hit, Environment.TickCount]);
    }
    [Fact]
    public void RapidNoteSwitchesKeepTheReadingContainerStationaryAndFullyVisible() => Run((browser, _, notes) =>
    {
        var pane = Assert.IsType<Border>(browser.FindName("ReaderPane"));
        for (var index = 0; index < 8; index++)
        {
            browser.OpenNote(notes[index % notes.Count]);
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(25));
            Assert.Equal(0, pane.RenderTransform.Value.OffsetY);
            Assert.Equal(1, pane.Opacity);
        }
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(180));
        Assert.Equal(notes[7 % notes.Count].Id, browser.Reader.Note?.Id);
        Assert.Equal(0, pane.RenderTransform.Value.OffsetY);
    });

    [Fact]
    public void SidebarTransitionReflowsTheLongDocumentOnceInsteadOfOnEveryAnimationFrame() => Run((browser, fixture, notes) =>
    {
        for (var index = 0; index < 200; index++) fixture.Write($"many/note-{index:000}.md", $"# note {index}\n\nA local note.");
        var scan = fixture.Catalog.ScanAsync();
        while (!scan.IsCompleted) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        browser.Configure(fixture.Catalog, scan.GetAwaiter().GetResult().Notes, fixture.WordRoot);
        browser.OpenNote(notes.Single(note => note.Title == "apple"));
        var preview = Descendants<MarkdownPreview>(browser.Reader).Single();
        var owner = Window.GetWindow(browser); owner.UpdateLayout();
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(70));
        var document = preview.Document;
        var widths = 0;
        preview.SizeChanged += (_, args) => { if (args.WidthChanged) widths++; };
        Click(browser, "CollapseFilesButton"); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(240));
        output.WriteLine($"202-note tree + 100-paragraph document: collapse caused {widths} Markdown viewport width changes.");
        var collapseChanges = widths;
        widths = 0;
        Click(browser, "ShowFilesButton"); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(240));
        output.WriteLine($"202-note tree + 100-paragraph document: expand caused {widths} Markdown viewport width changes.");
        Assert.InRange(collapseChanges, 0, 2);
        Assert.InRange(widths, 0, 2);
        Assert.Same(document, preview.Document);
    });

    [Fact]
    public void SidebarReversalAndResizeReleaseTheSnapshotAndRestoreLocalInput() => Run((browser, _, _) =>
    {
        var snapshot = Assert.IsType<Image>(browser.FindName("SidebarTransitionImage"));
        var layout = Assert.IsType<Grid>(browser.FindName("BrowserLayout"));
        Click(browser, "CollapseFilesButton");
        if (SystemParameters.ClientAreaAnimation)
        {
            var frame = Assert.IsType<RenderTargetBitmap>(snapshot.Source);
            Assert.True(frame.IsFrozen); Assert.False(layout.IsHitTestVisible);
            var surface = Assert.IsType<Grid>(browser.FindName("BrowserMotionHost"));
            Assert.Equal((int)Math.Ceiling(surface.ActualWidth * VisualTreeHelper.GetDpi(surface).DpiScaleX), frame.PixelWidth);
        }
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30));
        Click(browser, "ShowFilesButton"); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(25));
        Click(browser, "CollapseFilesButton");
        var owner = Window.GetWindow(browser); owner.Width -= 35; owner.UpdateLayout();
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(180));
        Assert.Null(snapshot.Source); Assert.False(snapshot.HasAnimatedProperties); Assert.Equal(Visibility.Collapsed, snapshot.Visibility);
        Assert.True(layout.IsHitTestVisible);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(browser.FindName("FileListPane")).Visibility);
        Click(browser, "ShowFilesButton"); browser.Visibility = Visibility.Collapsed;
        Assert.Null(snapshot.Source);
        browser.Visibility = Visibility.Visible;
        Assert.True(layout.IsHitTestVisible);
        Click(browser, "CollapseFilesButton"); browser.Dispose();
        Assert.Null(snapshot.Source); Assert.False(snapshot.HasAnimatedProperties); Assert.True(layout.IsHitTestVisible);
    });
    [Fact]
    public void FocusingRefreshDoesNotGrowTheFooterOrLeaveAPersistentSurfaceBorder() => Run((browser, fixture, notes) =>
    {
        var owner = Window.GetWindow(browser); owner.Activate();
        Assert.IsType<TextBox>(browser.FindName("LibrarySearchBox")).Focus(); owner.UpdateLayout();
        var reload = Assert.IsType<Button>(browser.FindName("ReloadLibraryButton"));
        var footer = Assert.IsType<StackPanel>(Assert.IsType<System.Windows.Controls.Primitives.UniformGrid>(reload.Parent).Parent);
        var height = footer.ActualHeight;
        var buttonHeight = reload.DesiredSize.Height;
        Assert.True(reload.Focus()); owner.UpdateLayout();
        Assert.Equal(buttonHeight, reload.DesiredSize.Height);
        Assert.Equal(height, footer.ActualHeight);
        var surface = Assert.IsType<Border>(reload.Template.FindName("Surface", reload));
        Assert.Equal(new Thickness(0), surface.BorderThickness);
        Assert.NotNull(reload.FocusVisualStyle);
        browser.Configure(fixture.Catalog, notes, fixture.WordRoot); owner.UpdateLayout();
        Assert.Equal(height, footer.ActualHeight);
    });

    [Fact]
    public void ReaderAndSidebarActionsUseCompactAccessibleLineIcons() => Run((browser, _, _) =>
    {
        foreach (var (name, kind, label) in new[] { ("BrowserBackButton", "back", "后退"), ("BrowserForwardButton", "forward", "前进"),
            ("CollapseFilesButton", "panel-left", "收起文件列表"), ("ShowFilesButton", "panel-left", "文件列表") })
        {
            var button = Assert.IsType<NotebookNavigationButton>(browser.FindName(name));
            Assert.Equal(kind, button.Kind); Assert.Equal(label, AutomationProperties.GetName(button)); Assert.Equal(label, button.ToolTip);
            Assert.Equal(32, button.Width); Assert.Equal(32, button.Height);
            var icon = Assert.IsType<NotebookIcon>(button.Content);
            Assert.Equal(16, icon.Width); Assert.Equal(16, icon.Height);
            Assert.NotNull(button.FocusVisualStyle);
        }
    });
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
        var owner = new Window { Content = capture, Width = 600, Height = 400, ShowInTaskbar = false, Icon = Window.GetWindow(browser).Icon };
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
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); icon.Freeze();
        var owner = new Window { Style = null, Icon = icon, Content = browser, Width = 1000, Height = 600, ShowInTaskbar = false };
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        owner.SetResourceReference(Control.BackgroundProperty, "ShellBackground");
        try
        {
            browser.Configure(fixture.Catalog, notes, fixture.WordRoot);
            owner.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
            action(browser, fixture, notes);
        }
        finally { owner.Close(); }
    });
    private static TreeView Tree(LibraryBrowser browser) => Assert.IsType<TreeView>(browser.FindName("BrowserFilesTree"));
    private static void Click(LibraryBrowser browser, string name) => Assert.IsAssignableFrom<Button>(browser.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

[CollectionDefinition("Library browser WPF input", DisableParallelization = true)]
public sealed class LibraryBrowserInputCollection;
