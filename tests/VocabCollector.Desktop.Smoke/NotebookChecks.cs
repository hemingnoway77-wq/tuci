using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    public async Task RunNotebookChecksOnlyAsync()
    {
        var output = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "library-reader-smoke");
        var run = Path.Combine(output, Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(run, "中文 vault #复习");
        var config = Path.Combine(run, "config");
        Directory.CreateDirectory(Path.Combine(vault, "英语单词", "形容词"));
        Directory.CreateDirectory(Path.Combine(vault, "英语单词", "词根"));
        await File.WriteAllTextAsync(Path.Combine(vault, "英语单词", "形容词", "resilient.md"), "# resilient\n\n**释义**：\n\n> adj. 有韧性的\n\n## 用法\n\nShe remained resilient.\n");
        await File.WriteAllTextAsync(Path.Combine(vault, "英语单词", "steady.md"), "# steady\n\n**释义**：\n\n> adj. 稳定的\n");
        await File.WriteAllTextAsync(Path.Combine(vault, "英语单词", "词根", "sal.md"), "# sal\n\n词根说明。\n");
        var settings = new AppSettings(new ApiOptions("", "", ""), new VaultOptions(vault, "英语单词", true));
        var settingsStore = new WindowsSettingsStore(config);
        await settingsStore.SaveAsync(settings, default);
        using var client = new System.Net.Http.HttpClient();
        var store = new VaultStore();
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), store, TimeProvider.System), settings, store);
        var themes = new ThemeManager(app.Resources, new AppearanceSettingsStore(config));
        await themes.LoadAsync();
        var navigator = new RecordingNavigator();
        var state = Path.Combine(config, "learning");
        var bindingErrors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var main = new MainWindow(vm, settingsStore, navigator, new RecordingReadingService(),
            _ => throw new InvalidOperationException("Library-reader smoke cannot open remote articles"), themes, learningStateDirectory: state);
        app.MainWindow = main;
        main.Show();
        try
        {
            await CheckLearningAsync(main, vm, themes, vault, run, state, output, navigator, () => 0);
            Check(bindingErrors.Errors.Count == 0, "library reader has no WPF binding errors: " + string.Join("; ", bindingErrors.Errors));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
            main.Close();
        }
        Console.WriteLine("Library-reader artifacts: " + output);
    }

    private static IEnumerable<LibraryTreeEntry> FlattenTree(IEnumerable<LibraryTreeEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in FlattenTree(entry.Children)) yield return child;
        }
    }

    private static async Task ClickLibraryAsync(Button button)
    {
        Invoke(button);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task CheckLibraryBrowserAsync(MainWindow main, LearningPage page, string wordRoot, string output)
    {
        var browser = page.BrowserControl;
        CheckLibraryChrome(main, page);
        CheckLibraryTheme(main, page);
        Check(!Descendants<LibraryShell>(page).Any() && !Descendants<NoteWorkspace>(page).Any() && !Descendants<NotebookGraphView>(page).Any(),
            "library presents the two-pane reader without the removed workspace surfaces");

        var fixture = Path.Combine(wordRoot, "两栏验收");
        Directory.CreateDirectory(Path.Combine(fixture, "空目录"));
        var firstPath = Path.Combine(fixture, "第一篇.md");
        var secondPath = Path.Combine(fixture, "第二篇.md");
        const string firstText = "# 第一篇\n\n树形列表选中的第一篇内容。\n\n## 阅读\n\n保留原始 Markdown。\n";
        const string secondText = "# 第二篇\n\n全文搜索唯一口令。\n\n**加粗内容**和普通正文一起显示。\n";
        await File.WriteAllTextAsync(firstPath, firstText);
        await File.WriteAllTextAsync(secondPath, secondText);
        await ClickLibraryAsync(LearningControl<Button>(page, "ReloadLibraryButton"));
        await Until(() => !page.IsBusy && page.AllNotes.Any(note => note.AbsolutePath == firstPath) && page.AllNotes.Any(note => note.AbsolutePath == secondPath));
        var first = page.AllNotes.Single(note => note.AbsolutePath == firstPath);
        var second = page.AllNotes.Single(note => note.AbsolutePath == secondPath);
        Check(FlattenTree(browser.Tree).Any(entry => entry is { IsFolder: true, RelativePath: "两栏验收/空目录" }),
            "refresh displays actual Markdown files and real empty directories");

        var tree = LearningControl<TreeView>(page, "BrowserFilesTree");
        tree.UpdateLayout();
        var folder = Descendants<TreeViewItem>(tree).Single(item => item.DataContext is LibraryTreeEntry { RelativePath: "两栏验收", IsFolder: true });
        var expand = (IExpandCollapseProvider)new TreeViewItemAutomationPeer(folder).GetPattern(PatternInterface.ExpandCollapse)!;
        expand.Collapse();
        await Task.Delay(220);
        expand.Expand();
        await Task.Delay(220);
        Check(folder.IsExpanded && Descendants<TreeViewItem>(folder).Any(item => item.DataContext is LibraryTreeEntry entry && entry.Note?.Id == first.Id),
            "folder expansion reveals its real Markdown child rows");
        CheckSettled(folder, "folder expansion finishes with visible, unshifted rows");
        CheckSettled(Descendants<ItemsPresenter>(folder).First(), "expanded child rows finish their appearance transition");
        expand.Collapse();
        await Task.Delay(220);
        Check(!folder.IsExpanded, "folder collapse completes without opening a note");
        await SelectLibraryNoteAsync(page, first);
        Check(ReadingPreview(page).RenderedText.Contains("树形列表选中", StringComparison.Ordinal), "selecting a file updates the right Markdown pane");
        CheckSettled(LearningControl<FrameworkElement>(page, "ReaderPane"), "initial document transition finishes");

        var left = LearningControl<FrameworkElement>(page, "FileListPane");
        var right = LearningControl<FrameworkElement>(page, "ReaderPane");
        Check(VisibleBounds(left, main).Right <= VisibleBounds(right, main).Left + 1, "file list and Markdown reading pane occupy distinct columns");
        await SelectLibraryNoteAsync(page, second);
        Check(ReadingPreview(page).RenderedText.Contains("加粗内容", StringComparison.Ordinal) && !ReadingPreview(page).RenderedText.Contains("**", StringComparison.Ordinal),
            "switching files renders the complete new Markdown document");
        CheckSettled(right, "document switch returns opacity and translation to their final values");
        CheckSettled(ReadingPreview(page), "rendered Markdown remains fully visible after switching files");
        CheckLibraryChrome(main, page);

        LearningControl<TextBox>(page, "LibrarySearchBox").Text = "全文搜索唯一口令";
        await Until(() => browser.Query == "全文搜索唯一口令" && browser.VisibleNotes.Count == 1 && browser.VisibleNotes[0].Id == second.Id);
        await SelectLibraryNoteAsync(page, browser.VisibleNotes[0], clearSearch: false);
        Check(ReadingPreview(page).RenderedText.Contains("全文搜索唯一口令", StringComparison.Ordinal), "full-text search filters the same file tree and its result opens inside the reader");
        LearningControl<TextBox>(page, "LibrarySearchBox").Text = "";
        await Until(() => browser.Query.Length == 0 && browser.VisibleNotes.Any(note => note.Id == first.Id));

        var originalReaderWidth = right.ActualWidth;
        await ClickLibraryAsync(LearningControl<Button>(page, "CollapseFilesButton"));
        await Until(() => LearningControl<Button>(page, "ShowFilesButton").IsVisible && right.ActualWidth > originalReaderWidth + 100);
        await Task.Delay(220);
        CheckReachable(LearningControl<Button>(page, "ShowFilesButton"), main, "collapsed file pane keeps its restore action reachable");
        CheckLibraryChrome(main, page);
        Snapshot(main, Path.Combine(output, "library-reader-collapsed.png"));
        await ClickLibraryAsync(LearningControl<Button>(page, "ShowFilesButton"));
        await Until(() => left.IsVisible && left.ActualWidth >= 180 && Math.Abs(right.ActualWidth - originalReaderWidth) < 3);
        await Task.Delay(220);
        CheckReachable(LearningControl<TextBox>(page, "LibrarySearchBox"), main, "restoring files also restores the search control");
        CheckSettled(right, "restored two-pane layout has no unfinished document transition");
        Check(await File.ReadAllTextAsync(firstPath) == firstText && await File.ReadAllTextAsync(secondPath) == secondText,
            "tree selection, search, transitions and layout never modify note contents");
        Console.WriteLine("LIBRARY READER: real file/folder tree, refresh, full-text search, document transitions, shared chrome and sidebar restore passed");
    }

    private static void CheckLibraryChrome(MainWindow main, LearningPage page)
    {
        CheckReachable(Control<FrameworkElement>(main, "MainHeader"), main, "library retains the shared application header");
        CheckReachable(Control<FrameworkElement>(main, "MainFooter"), main, "library retains the shared application footer");
        foreach (var name in new[] { "CaptureTab", "LibraryTab", "RoamingTab", "ReadingTab", "ThemeWarmOption", "ThemeMonoOption", "ThemeRoseOption", "SettingsButton" })
            CheckReachable(Control<FrameworkElement>(main, name), main, "shared navigation remains reachable: " + name);
        Check(Control<RadioButton>(main, "LibraryTab").IsChecked == true && page.IsVisible, "shared navigation identifies the active library page");
    }

    private static void CheckLibraryTheme(MainWindow main, LearningPage page)
    {
        var resources = Application.Current.Resources;
        Check(ReferenceEquals(main.Background, resources["ShellBackground"]), "library window keeps the current application background palette");
        var expected = ((SolidColorBrush)resources["Ink"]).Color;
        Check(((SolidColorBrush)ReadingPreview(page).Document.Foreground).Color == expected,
            "Markdown text follows the current warm, mono or pink application ink");
    }

    private static void CheckSettled(FrameworkElement element, string message)
    {
        var matrix = element.RenderTransform.Value;
        Check(element.IsVisible && Math.Abs(element.Opacity - 1) < 0.01 &&
            Math.Abs(matrix.M11 - 1) < 0.01 && Math.Abs(matrix.M22 - 1) < 0.01 &&
            Math.Abs(matrix.OffsetX) < 0.1 && Math.Abs(matrix.OffsetY) < 0.1, message);
    }
}
