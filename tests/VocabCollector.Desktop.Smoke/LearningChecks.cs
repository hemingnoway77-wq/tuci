using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;
using VocabCollector.Desktop.Reading;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    private async Task CheckPreviewLinkOpensInLibraryAsync(MainWindow main, Hyperlink link, string path,
        string expectedText, RecordingNavigator navigator)
    {
        var externalOpens = navigator.NoteOpens;
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));
        await CheckLibraryReadsPathAsync(main, path, expectedText);
        Check(navigator.NoteOpens == externalOpens, "capture preview single-click opens the full note internally without an external application");
        await SelectPageAsync(main, "CaptureTab");
    }

    private static LearningPage Learning(MainWindow main) => Control<Grid>(main, "PageHost").Children.OfType<LearningPage>().Single();
    private static T LearningControl<T>(LearningPage page, string name) where T : FrameworkElement =>
        (T)(page.FindName(name) ?? page.BrowserControl.FindName(name) ?? throw new InvalidOperationException("Missing learning control " + name));
    private static MarkdownPreview ReadingPreview(LearningPage page) => Descendants<MarkdownPreview>(page.ReaderControl).Single(preview => preview.Name == "ReadingPreview");

    private static async Task CheckLibraryReadsPathAsync(MainWindow main, string path, string expectedText)
    {
        await Until(() => Control<RadioButton>(main, "LibraryTab").IsChecked == true &&
            Control<Grid>(main, "PageHost").Children.OfType<LearningPage>().Any(page => page.IsVisible && page.ReaderControl.Note?.AbsolutePath == path));
        var page = Learning(main);
        await Until(() => !page.IsBusy);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var preview = ReadingPreview(page);
        Check(preview.IsVisible && preview.RenderedText.Contains(expectedText, StringComparison.Ordinal), "library reader displays the complete local target note");
        Check(page.ReaderControl.Note!.Markdown == await File.ReadAllTextAsync(path), "in-app reader uses the actual Markdown file without replacing its contents");
    }

    private static async Task<LearningPage> OpenLibraryAsync(MainWindow main)
    {
        await SelectPageAsync(main, "LibraryTab");
        await Until(() => Control<Grid>(main, "PageHost").Children.OfType<LearningPage>().Any(page => page.IsVisible && !page.IsBusy));
        var page = Learning(main);
        await page.ReloadAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        return page;
    }

    private static async Task SelectLibraryNoteAsync(LearningPage page, LibraryNote note, bool clearSearch = true)
    {
        if (clearSearch) LearningControl<TextBox>(page, "LibrarySearchBox").Text = "";
        await Until(() => page.BrowserControl.VisibleNotes.Any(candidate => candidate.Id == note.Id));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var tree = LearningControl<TreeView>(page, "BrowserFilesTree");
        foreach (var folder in FlattenTree(page.BrowserControl.Tree).Where(entry => entry.IsFolder && note.RelativePath.StartsWith(entry.RelativePath + "/", StringComparison.OrdinalIgnoreCase))) folder.IsExpanded = true;
        tree.UpdateLayout();
        var item = Descendants<TreeViewItem>(tree).Single(container => container.DataContext is LibraryTreeEntry entry && entry.Note?.Id == note.Id);
        item.IsSelected = false;
        ((ISelectionItemProvider)new TreeViewItemAutomationPeer(item).GetPattern(PatternInterface.SelectionItem)!).Select();
        item.BringIntoView();
        await Until(() => page.ReaderControl.Note?.Id == note.Id);
        Check(tree.SelectedItem is LibraryTreeEntry selected && selected.Note?.Id == note.Id, "real file tree selects and opens its Markdown file");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(220);
    }

    private async Task CheckLearningAsync(MainWindow main, MainViewModel vm, ThemeManager themes, string vault,
        string run, string learningState, string output, RecordingNavigator navigator, Func<int> modelRequests)
    {
        var beforeRequests = modelRequests();
        var settings = vm.Settings;
        var theme = themes.CurrentTheme;
        var originalWidth = main.Width;
        var originalHeight = main.Height;
        var settingsFile = Path.Combine(run, "config", "settings.json");
        var savedSettings = await File.ReadAllTextAsync(settingsFile);
        var wordRoot = VaultPaths.WordRoot(settings.Vault);
        Check(Path.GetFullPath(wordRoot).StartsWith(Path.GetFullPath(vault) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "learning checks use the isolated smoke vault");
        var folder = Path.Combine(wordRoot, "随手笔记");
        Directory.CreateDirectory(folder);
        var chinesePath = Path.Combine(folder, "中文学习.md");
        var chineseMarkdown = "# 中文学习\n\n这是一篇手写中文笔记，阅读不需要配置 API。\n\n[[形容词/resilient|查看 resilient 词条]]\n\n" +
            string.Join("\n\n", Enumerable.Range(1, 90).Select(index => $"第 {index} 段：把遇见的表达放回自己的语境，在长文阅读中继续积累。"));
        await File.WriteAllTextAsync(chinesePath, chineseMarkdown);

        try
        {
            vm.ApplySettings(settings with { Api = new ApiOptions("", "", "") });
            Check(vm.Settings.Api.ApiKey.Length == 0 && vm.Settings.Api.BaseUrl.Length == 0, "local learning is exercised with no API configuration");
            var page = await OpenLibraryAsync(main);
            Check(page.AllNotes.Any(note => note.AbsolutePath == chinesePath && !note.IsWord), "ordinary Chinese Markdown appears in the same library as vocabulary");
            Check(page.AllNotes.Any(note => note.Title == "resilient" && note.IsWord), "generated English vocabulary remains searchable and eligible for review");
            Check(page.AllNotes.Where(note => note.RelativePath.StartsWith("词根/", StringComparison.Ordinal)).All(note => !note.IsWord), "root notes are readable without becoming roaming candidates");

            var search = LearningControl<TextBox>(page, "LibrarySearchBox");
            search.Text = "手写中文";
            await Until(() => page.BrowserControl.Query == "手写中文" && page.BrowserControl.VisibleNotes.Count == 1 && page.BrowserControl.VisibleNotes.Single().AbsolutePath == chinesePath);
            await SelectLibraryNoteAsync(page, page.BrowserControl.VisibleNotes.Single(), clearSearch: false);
            var preview = ReadingPreview(page);
            preview.SelectAll();
            Check(preview.Selection.Text.Contains("手写中文笔记", StringComparison.Ordinal), "ordinary Markdown reading permits selecting and copying text");
            Check(!preview.RenderedText.Contains("[[", StringComparison.Ordinal) && !preview.RenderedText.Contains("# 中文学习", StringComparison.Ordinal), "local reader renders text and links instead of raw Markdown delimiters");
            search.Text = "resilient";
            await Until(() => page.BrowserControl.Query == "resilient" && page.BrowserControl.VisibleNotes.Any(note => note.Title == "resilient") && page.BrowserControl.VisibleNotes.Any(note => note.AbsolutePath == chinesePath));
            await SelectLibraryNoteAsync(page, page.AllNotes.Single(note => note.AbsolutePath == chinesePath));
            Check(FlattenTree(page.BrowserControl.Tree).Any(entry => entry.IsFolder && entry.RelativePath == "随手笔记"), "real folder tree includes the ordinary Markdown directory");
            await CheckLibraryNavigationAndReturnAsync(main, page, chinesePath, navigator);
            await CheckLibraryBrowserAsync(main, page, wordRoot, output);
            var importSources = Path.Combine(run, "import-sources");
            Directory.CreateDirectory(importSources);
            var source = Path.Combine(importSources, "导入札记.md");
            const string importedMarkdown = "# 导入札记\n\n从旧笔记带来的手写内容，保持原样。\n";
            await File.WriteAllTextAsync(source, importedMarkdown);
            Check(LearningControl<Button>(page, "ImportMarkdownButton").IsVisible && LearningControl<Button>(page, "ImportMarkdownButton").IsEnabled, "Markdown import is available without API configuration");
            await page.ImportAsync([source]);
            var imported = page.AllNotes.Single(note => note.Title == "导入札记");
            Check(await File.ReadAllTextAsync(imported.AbsolutePath) == importedMarkdown && await File.ReadAllTextAsync(source) == importedMarkdown, "import preserves both the source and the new library copy");
            await SelectLibraryNoteAsync(page, imported);
            Check(ReadingPreview(page).RenderedText.Contains("从旧笔记带来", StringComparison.Ordinal), "imported ordinary Markdown can be selected and read immediately");
            await File.WriteAllTextAsync(source, "# 不应覆盖\n\nchanged source\n");
            await page.ImportAsync([source]);
            Check(await File.ReadAllTextAsync(imported.AbsolutePath) == importedMarkdown && page.AllNotes.Count(note => note.Title == "导入札记") == 1, "importing the same filename never overwrites an existing note");

            await CheckRoamingAsync(main, page, learningState, output);
            await CheckLearningThemesAndMinimumAsync(main, page, themes, chinesePath, learningState, output);
            Check(modelRequests() == beforeRequests, "search, import, full-note navigation, return, roaming and themes use zero model requests without an API key");
            Check(navigator.NoteOpens == 0, "all learning paths stay inside the application");
            Check(!app.Windows.OfType<ArticleWindow>().Any(window => window.IsVisible), "local learning never opens or requests a remote article");
            Check(await File.ReadAllTextAsync(chinesePath) == chineseMarkdown, "reading and review do not write progress into Markdown content");
            Check(await File.ReadAllTextAsync(settingsFile) == savedSettings, "local learning does not rewrite saved API settings");
            Check(!Directory.GetFiles(vault, "roaming.json", SearchOption.AllDirectories).Any() &&
                !Directory.GetFiles(vault, "catalog.json", SearchOption.AllDirectories).Any(), "learning history and stable identities stay outside the synchronized vault");
            await CheckStandaloneLibraryWithoutObsidianAsync(run, output);
        }
        finally
        {
            vm.ApplySettings(settings);
            await themes.SetThemeAsync(theme);
            main.Width = originalWidth;
            main.Height = originalHeight;
            await SelectPageAsync(main, "CaptureTab");
        }
    }

    private async Task CheckLibraryNavigationAndReturnAsync(MainWindow main, LearningPage page, string chinesePath, RecordingNavigator navigator)
    {
        var preview = ReadingPreview(page);
        preview.ScrollToVerticalOffset(360);
        await Until(() => preview.VerticalOffset > 100);
        var offset = preview.VerticalOffset;
        var link = Links(preview.Document).Single(candidate => candidate.Tag is PreviewLink { Target: "形容词/resilient" });
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));
        await Until(() => page.ReaderControl.Note?.Title == "resilient");
        Check(ReadingPreview(page).RenderedText.Contains("有韧性的", StringComparison.Ordinal) && navigator.NoteOpens == 0, "single library link opens the local full note");
        Invoke(LearningControl<Button>(page, "BrowserBackButton"));
        await Until(() => page.ReaderControl.Note?.AbsolutePath == chinesePath && Math.Abs(ReadingPreview(page).VerticalOffset - offset) <= 3);
        Check(ReadingPreview(page).RenderedText.Contains("第 90 段", StringComparison.Ordinal), "back navigation restores the complete source note and its prior reading position");
    }

    private static async Task CheckStandaloneLibraryWithoutObsidianAsync(string run, string output)
    {
        var root = Path.Combine(run, "独立本地词库");
        Directory.CreateDirectory(root);
        var options = new VaultOptions(root, "英语单词", true);
        var wordRoot = VaultPaths.WordRoot(options);
        Directory.CreateDirectory(wordRoot);
        var path = Path.Combine(wordRoot, "独立中文笔记.md");
        const string markdown = "# 独立中文笔记\n\n没有 Obsidian 目录标记，也可以在兔词里查找和阅读。\n";
        await File.WriteAllTextAsync(path, markdown);
        await File.WriteAllTextAsync(Path.Combine(wordRoot, "wonder.md"), "# wonder\n\n**释义**：\n\n> n. 惊奇\n");
        Check(!Directory.Exists(Path.Combine(root, ".obsidian")) && !Directory.Exists(Path.Combine(wordRoot, ".obsidian")), "standalone fixture has no Obsidian installation or vault marker");
        var page = new LearningPage(options, Path.Combine(run, "standalone-learning-state"));
        var window = new Window { Title = "独立本地词库 · 兔词", Width = 1080, Height = 780, Content = page };
        try
        {
            window.Show();
            await page.ReloadAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(page.AllNotes.Count == 2, "a plain local directory loads both Chinese Markdown and an English word");
            LearningControl<TextBox>(page, "LibrarySearchBox").Text = "没有 Obsidian";
            await Until(() => page.BrowserControl.Query == "没有 Obsidian" && page.BrowserControl.VisibleNotes.Count == 1 && page.BrowserControl.VisibleNotes.Single().AbsolutePath == path);
            await SelectLibraryNoteAsync(page, page.BrowserControl.VisibleNotes.Single(), clearSearch: false);
            Check(page.ReaderControl.Note?.AbsolutePath == path && ReadingPreview(page).RenderedText.Contains("在兔词里查找和阅读", StringComparison.Ordinal), "a standalone local library searches and reads without an API service or external application");
            Snapshot(window, Path.Combine(output, "learning-no-obsidian.png"));
            Check(await File.ReadAllTextAsync(path) == markdown, "standalone reading preserves the original Markdown");
        }
        finally { page.Dispose(); window.Close(); }
    }

    private static async Task CheckRoamingAsync(MainWindow main, LearningPage page, string learningState, string output)
        => await CheckRoamingDashboardAsync(main, page, learningState, output, requirePreviewLink: false);

    private static ReviewStateSnapshot ReviewSnapshot(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var records = root.GetProperty("Records").EnumerateObject().Select(property => property.Value).ToArray();
        return new(root.GetProperty("CurrentExposure").GetGuid(), records.Sum(record => record.GetProperty("Remembered").GetInt32()),
            records.Sum(record => record.GetProperty("Skipped").GetInt32()), json);
    }

    private static async Task CheckLearningThemesAndMinimumAsync(MainWindow main, LearningPage page, ThemeManager themes,
        string chinesePath, string learningState, string output)
    {
        var stateFile = Directory.GetFiles(learningState, "roaming.json", SearchOption.AllDirectories).Single();
        var before = ReviewSnapshot(stateFile).Json;
        foreach (var (option, target, slug) in new[]
        {
            ("ThemeWarmOption", AppTheme.WarmNeutral, "warm"),
            ("ThemeRoseOption", AppTheme.Pink, "pink"),
            ("ThemeMonoOption", AppTheme.Mono, "mono")
        })
        {
            await SelectPageAsync(main, "LibraryTab");
            await Until(() => !page.IsBusy);
            page.OpenNote(page.AllNotes.Single(note => note.AbsolutePath == chinesePath));
            await SwitchThemeAsync(Control<RadioButton>(main, option), themes, target);
            CheckLibraryChrome(main, page);
            CheckLibraryTheme(main, page);
            await Task.Delay(220);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReadingPreview(page).RenderedText.Contains("手写中文", StringComparison.Ordinal), "theme change preserves the open local reading document");
            Snapshot(main, Path.Combine(output, "library-reader-" + slug + ".png"));
            await SelectPageAsync(main, "RoamingTab");
            await Until(() => !page.IsBusy && page.HasExposure);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Snapshot(main, Path.Combine(output, "learning-roaming-" + slug + ".png"));
            await OpenRoamingPreviewAsync(main, page);
            CheckRoamingColumns(main, page);
            Snapshot(main, Path.Combine(output, "learning-roaming-preview-" + slug + ".png"));
            Invoke(LearningControl<Button>(page, "CloseRoamingPreviewButton"));
            await Until(() => !page.IsRoamingPreviewOpen);
        }
        Check(ReviewSnapshot(stateFile).Json == before, "theme changes and repeated learning-page rendering do not create extra exposures or counts");
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        await SelectPageAsync(main, "LibraryTab");
        await Until(() => !page.IsBusy);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        main.UpdateLayout();
        CheckReachable(LearningControl<Button>(page, "ImportMarkdownButton"), main, "Markdown import remains reachable at minimum size");
        CheckReachable(LearningControl<TextBox>(page, "LibrarySearchBox"), main, "library search remains reachable at minimum size");
        Check(ReadingPreview(page).ActualWidth >= 250 && ReadingPreview(page).ActualHeight >= 180, "minimum library window leaves a usable text-reading area");
        CheckLibraryChrome(main, page);
        CheckLibraryTheme(main, page);
        await Task.Delay(220);
        Snapshot(main, Path.Combine(output, "library-reader-minimum.png"));
        await SelectPageAsync(main, "RoamingTab");
        await Until(() => !page.IsBusy && page.HasExposure);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        main.UpdateLayout();
        var skip = LearningControl<Button>(page, "RoamingSkipButton");
        var remember = LearningControl<Button>(page, "RoamingRememberButton");
        CheckReachable(skip, main, "next-card button remains reachable at minimum size");
        CheckReachable(remember, main, "remember button remains reachable at minimum size");
        Check(!VisibleBounds(skip, main).IntersectsWith(VisibleBounds(remember, main)), "minimum-size roaming actions do not overlap");
        var cardScroll = Descendants<ScrollViewer>(LearningControl<Border>(page, "RoamingWordCard")).Single();
        cardScroll.ScrollToEnd();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CheckReachable(LearningControl<Button>(page, "RoamingReadNoteButton"), main, "full-note action remains reachable by scrolling the minimum roaming card");
        Snapshot(main, Path.Combine(output, "learning-roaming-card-minimum.png"));
        await OpenRoamingPreviewAsync(main, page);
        CheckRoamingColumns(main, page);
        CheckReachable(LearningControl<Button>(page, "CloseRoamingPreviewButton"), main, "minimum third-column preview has a reachable close action");
        Check(RoamingReadingPreview(page).ActualWidth >= 240 && RoamingReadingPreview(page).ActualHeight >= 180,
            "minimum roaming window retains a usable independent Markdown reader");
        Snapshot(main, Path.Combine(output, "learning-roaming-minimum.png"));
        Invoke(LearningControl<Button>(page, "CloseRoamingPreviewButton"));
        await Until(() => !page.IsRoamingPreviewOpen);
        Check(ReviewSnapshot(stateFile).Json == before, "resizing and scrolling the roaming card never complete it");
    }

    private static void CheckReachable(FrameworkElement control, Window main, string message)
    {
        var bounds = VisibleBounds(control, main);
        var viewport = new Rect(0, 0, main.ActualWidth, main.ActualHeight);
        Check(control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0 &&
            bounds.Width >= control.ActualWidth - 1 && bounds.Height >= control.ActualHeight - 1 && viewport.Contains(bounds), message);
    }

    private sealed record ReviewStateSnapshot(Guid Exposure, int Remembered, int Skipped, string Json);
}
