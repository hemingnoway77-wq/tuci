using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        var output = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "notebook-smoke");
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
            _ => throw new InvalidOperationException("Notebook smoke cannot open remote articles"), themes, learningStateDirectory: state);
        app.MainWindow = main;
        main.Show();
        try
        {
            await CheckLearningAsync(main, vm, themes, vault, run, state, output, navigator, () => 0);
            Check(bindingErrors.Errors.Count == 0, "notebook controls have no WPF binding errors: " + string.Join("; ", bindingErrors.Errors));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
            main.Close();
        }
        Console.WriteLine("Notebook artifacts: " + output);
    }

    private static IEnumerable<LibraryTreeEntry> FlattenTree(IEnumerable<LibraryTreeEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in FlattenTree(entry.Children)) yield return child;
        }
    }

    private async Task CheckNotebookWorkspaceAsync(MainWindow main, LearningPage page, string wordRoot, string output)
    {
        var shell = page.NotebookControl;
        var workspace = shell.Documents;
        var fixture = Path.Combine(wordRoot, "工作区验收");
        Directory.CreateDirectory(Path.Combine(fixture, "空目录"));
        var alphaPath = Path.Combine(fixture, "起点.md");
        var betaPath = Path.Combine(fixture, "终点.md");
        var alphaText = "# 起点\n\n---\n\n字段检索唯一口令。\n\n[[工作区验收/终点|继续阅读]]\n\n" +
            string.Join("\n\n", Enumerable.Range(1, 70).Select(index => $"长文第 {index} 段：在应用内阅读、编辑和继续探索。")) +
            "\n\n## 深入阅读\n\n" + string.Join("\n\n", Enumerable.Range(71, 30).Select(index => $"长文第 {index} 段：保留大纲跳转后的上下文。"));
        const string betaText = "# 终点\n\n## 复习\n\n终点内容保持完整。\n\n[[工作区验收/起点]]\n";
        await File.WriteAllTextAsync(alphaPath, alphaText);
        await File.WriteAllTextAsync(betaPath, betaText);
        await page.ReloadAsync();
        var alpha = page.AllNotes.Single(note => note.AbsolutePath == alphaPath);
        var beta = page.AllNotes.Single(note => note.AbsolutePath == betaPath);
        await SelectLibraryNoteAsync(page, alpha);
        Check(FlattenTree(shell.Tree).Any(entry => entry.IsFolder && entry.RelativePath == "工作区验收/空目录"), "file explorer includes real empty directories");
        await ClickNotebookAsync(NotebookButton(shell, "折叠所有文件夹"));
        Check(FlattenTree(shell.Tree).Where(entry => entry.IsFolder).All(entry => !entry.IsExpanded), "collapse-all updates every real folder");
        await ClickNotebookAsync(NotebookButton(shell, "定位当前文件"));
        Check(LearningControl<TreeView>(page, "LibraryFilesTree").SelectedItem is LibraryTreeEntry { Note: { } selected } && selected.Id == alpha.Id,
            "reveal expands ancestors and selects the active file");
        await Task.Delay(240);
        Snapshot(main, Path.Combine(output, "notebook-dark.png"));

        shell.FocusSearch();
        LearningControl<TextBox>(page, "LibrarySearchBox").Text = "path:\"工作区验收/\" 字段检索唯一口令";
        await Until(() => shell.SearchResults.Count == 1 && shell.SearchResults[0].Note.Id == alpha.Id);
        Check(shell.SearchResults[0].Excerpt.Contains("字段检索", StringComparison.Ordinal), "field/full-text search displays a matching excerpt");
        LearningControl<ListBox>(page, "LibrarySearchResults").SelectedIndex = 0;

        await NotebookPickerAsync(main, () => NotebookShortcut(main, Key.O), "快速切换", "终点");
        await Until(() => workspace.Note?.Id == beta.Id);
        await NotebookPickerAsync(main, () => NotebookShortcut(main, Key.P), "命令面板", "显示文件");
        await Until(() => LearningControl<TreeView>(page, "LibraryFilesTree").IsVisible);
        Console.WriteLine("NOTEBOOK: tree, field search, Ctrl+O and Ctrl+P passed");

        await SelectLibraryNoteAsync(page, alpha);
        var firstReader = page.ReaderControl;
        ReadingPreview(page).ScrollToVerticalOffset(440);
        await Until(() => ReadingPreview(page).VerticalOffset > 200);
        var offset = ReadingPreview(page).VerticalOffset;
        workspace.OpenNote(beta, true);
        var secondReader = page.ReaderControl;
        Check(!ReferenceEquals(firstReader, secondReader), "each tab owns an independent reader");
        Check(!NotebookButton(workspace, "后退").IsEnabled, "new tab begins without back history");
        Check(workspace.Note?.Id == beta.Id, "new tab does not inherit another tab's navigation history");
        workspace.NextTab(-1);
        await Until(() => ReferenceEquals(page.ReaderControl, firstReader) && Math.Abs(ReadingPreview(page).VerticalOffset - offset) < 3);
        Links(ReadingPreview(page).Document).Single(link => link.Tag is VocabCollector.Desktop.Preview.PreviewLink { Target: "工作区验收/终点" })
            .RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
        await Until(() => workspace.Note?.Id == beta.Id);
        await ClickNotebookAsync(NotebookButton(workspace, "后退"));
        await Until(() => workspace.Note?.Id == alpha.Id && Math.Abs(ReadingPreview(page).VerticalOffset - offset) < 3);
        await ClickNotebookAsync(NotebookButton(workspace, "前进"));
        await Until(() => workspace.Note?.Id == beta.Id);
        workspace.NextTab(1);
        Check(ReferenceEquals(page.ReaderControl, secondReader) && workspace.Note?.Id == beta.Id, "switching tabs retains the second reader and note");

        await SelectLibraryNoteAsync(page, alpha);
        await ClickNotebookAsync(LearningControl<Button>(page, "OutlineModeButton"));
        await Until(() => shell.Headings.Count == 2);
        LearningControl<ListBox>(page, "NoteOutlineList").SelectedIndex = 1;
        await Until(() => ReadingPreview(page).VerticalOffset > 0);
        await ClickNotebookAsync(LearningControl<Button>(page, "OutgoingModeButton"));
        Check(shell.References.Any(note => note.Id == beta.Id), "outgoing inspector resolves the linked local file");
        LearningControl<ListBox>(page, "NoteReferencesList").SelectedIndex = 0;
        await Until(() => workspace.Note?.Id == beta.Id);
        await ClickNotebookAsync(LearningControl<Button>(page, "BacklinksModeButton"));
        Check(shell.References.Any(note => note.Id == alpha.Id), "backlinks inspector finds the source file");
        Snapshot(main, Path.Combine(output, "notebook-relations.png"));

        await ClickNotebookAsync(NotebookButton(workspace, "阅读 / 源码 Ctrl+E"));
        var editor = Descendants<TextBox>(workspace).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible);
        var savedText = betaText + "\n自动保存验收内容。\n";
        editor.Text = savedText;
        Snapshot(main, Path.Combine(output, "notebook-editor.png"));
        await Until(() => File.ReadAllText(betaPath) == savedText && !workspace.HasUnsavedChanges);
        var externalText = savedText + "\n外部编辑的最新内容。\n";
        var localDraft = savedText + "\n本地未保存草稿。\n";
        await File.WriteAllTextAsync(betaPath, externalText);
        editor.Text = localDraft;
        Check(!await workspace.SaveAllAsync(), "external change blocks an overwrite");
        Check(File.ReadAllText(betaPath) == externalText && editor.Text == localDraft && workspace.HasUnsavedChanges,
            "save conflict preserves the external file and the local editor draft");
        await ClickNotebookAsync(NotebookButton(workspace, "保留外部文件并保存本地草稿副本"));
        await Until(() => !workspace.HasUnsavedChanges && workspace.Note?.AbsolutePath != betaPath);
        Check(File.ReadAllText(betaPath) == externalText && File.ReadAllText(workspace.Note!.AbsolutePath) == localDraft,
            "save-copy resolves the conflict without losing either version");
        await ClickNotebookAsync(NotebookButton(workspace, "阅读 / 源码 Ctrl+E"));
        Console.WriteLine("NOTEBOOK: independent tabs/history, relations, autosave and external conflict passed");

        await SelectLibraryNoteAsync(page, page.AllNotes.Single(note => note.AbsolutePath == alphaPath));
        await CheckNotebookPanelsAsync(main, page, output);
        await ClickNotebookAsync(LearningControl<Button>(page, "NotebookGraphButton"));
        var graph = Descendants<NotebookGraphView>(shell).Single();
        await Until(() => !graph.IsBusy && graph.DisplayedNodeCount >= 2 && graph.DisplayedEdgeCount > 0);
        Snapshot(main, Path.Combine(output, "notebook-graph.png"));
        graph.LocalMode = true;
        await Until(() => !graph.IsBusy && graph.DisplayedNodeCount >= 2);
        graph.LocalMode = false;
        graph.Filter = "path:\"工作区验收/终点.md\"";
        await Until(() => !graph.IsBusy);
        Check(graph.DisplayedNodeCount == 1, "graph uses the same field search to filter notes");
        graph.Filter = "";
        graph.LocalMode = false;
        await Until(() => !graph.IsBusy && graph.DisplayedNodeCount >= 2);
        await OpenGraphNoteByKeyboardAsync(graph, alpha);
        await Until(() => workspace.IsVisible && !graph.IsVisible && workspace.Note?.Id == alpha.Id);
        await CheckNotebookFileOperationsAsync(main, page, wordRoot);
        Check(File.ReadAllText(alphaPath) == alphaText, "workspace exploration preserves untouched Markdown");
        Console.WriteLine("NOTEBOOK: file menus, panels/themes, graph filtering and keyboard navigation passed");
    }

    private static Button NotebookButton(DependencyObject root, string tooltip) =>
        Descendants<Button>(root).Single(button => Equals(button.ToolTip, tooltip));

    private static async Task ClickNotebookAsync(Button button)
    {
        Invoke(button);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private async Task NotebookPickerAsync(MainWindow main, Action open, string title, string query)
    {
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(open, DispatcherPriority.Background);
        await Until(() => app.Windows.OfType<NotebookPicker>().Any(window => window.IsVisible && window.Title == title));
        var picker = app.Windows.OfType<NotebookPicker>().Single(window => window.IsVisible && window.Title == title);
        Descendants<TextBox>(picker).Single(box => box.Name == "NotebookPickerQuery").Text = query;
        var list = Descendants<ListBox>(picker).Single(box => box.Name == "NotebookPickerResults");
        Check(list.Items.Count > 0, title + " offers a matching action or note");
        picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker), Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        await Until(() => !picker.IsVisible);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private async Task NotebookPromptAsync(Action open, string title, string? value)
    {
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(open, DispatcherPriority.Background);
        await Until(() => app.Windows.Cast<Window>().Any(window => window.IsVisible && window.Title == title));
        var dialog = app.Windows.Cast<Window>().Single(window => window.IsVisible && window.Title == title);
        if (value != null) Descendants<TextBox>(dialog).Single(box => box.Name == "NotebookNameBox").Text = value;
        var confirm = Descendants<Button>(dialog).Single(button => button.Name == "NotebookConfirmButton");
        dialog.UpdateLayout();
        Console.WriteLine($"NOTEBOOK DIALOG {title}: window={dialog.ActualWidth}x{dialog.ActualHeight}; confirm={confirm.ActualWidth}x{confirm.ActualHeight}; visible={VisibleBounds(confirm, dialog)}");
        Snapshot(dialog, Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "notebook-smoke", "dialog-" + title + ".png"));
        CheckReachable(confirm, dialog, title + " confirmation is visible and reachable");
        await ClickNotebookAsync(confirm);
        await Until(() => !dialog.IsVisible);
    }

    private async Task CheckNotebookFileOperationsAsync(MainWindow main, LearningPage page, string wordRoot)
    {
        await ClickNotebookAsync(LearningControl<Button>(page, "NotebookFilesButton"));
        await NotebookPromptAsync(() => Invoke(LearningControl<Button>(page, "NewFolderButton")), "新建文件夹", "UI操作验收");
        await Until(() => Directory.Exists(Path.Combine(wordRoot, "UI操作验收")));
        await page.ReloadAsync();
        var tree = LearningControl<TreeView>(page, "LibraryFilesTree");
        tree.UpdateLayout();
        var folder = Descendants<TreeViewItem>(tree).Single(item => item.DataContext is LibraryTreeEntry { RelativePath: "UI操作验收", IsFolder: true });
        ((ISelectionItemProvider)new TreeViewItemAutomationPeer(folder).GetPattern(PatternInterface.SelectionItem)!).Select();
        await NotebookPromptAsync(() => Invoke(LearningControl<Button>(page, "NewMarkdownButton")), "新建笔记", "UI操作验收/界面新建");
        var initialPath = Path.Combine(wordRoot, "UI操作验收", "界面新建.md");
        await Until(() => File.Exists(initialPath) && page.ReaderControl.Note?.AbsolutePath == initialPath);
        var editor = Descendants<TextBox>(page.NotebookControl.Documents).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible);
        const string content = "# 界面新建\n\n通过真实新建窗口和菜单完成。\n";
        editor.Text = content;
        Check(await page.SaveAllAsync(), "new note is safely saved before file operations");
        page.NotebookControl.Documents.ToggleEditing();
        await page.ReloadAsync();
        await SelectLibraryNoteAsync(page, page.AllNotes.Single(note => note.AbsolutePath == initialPath));
        await OpenNotebookContextMenuAsync(main, page);
        await NotebookPromptAsync(() => InvokeMenu(tree, "重命名…"), "重命名笔记", "界面改名");
        var renamedPath = Path.Combine(wordRoot, "UI操作验收", "界面改名.md");
        await Until(() => File.Exists(renamedPath) && !File.Exists(initialPath));
        Check(File.ReadAllText(renamedPath) == content, "rename menu preserves Markdown content");
        await page.ReloadAsync();
        await SelectLibraryNoteAsync(page, page.AllNotes.Single(note => note.AbsolutePath == renamedPath));
        await OpenNotebookContextMenuAsync(main, page);
        await NotebookPromptAsync(() => InvokeMenu(tree, "删除"), "删除笔记", null);
        await Until(() => !File.Exists(renamedPath));
        await NotebookPickerAsync(main, () => Invoke(NotebookButton(page.NotebookControl, "已删除的笔记")), "回收站 · 选择笔记恢复", "界面改名");
        await Until(() => File.Exists(renamedPath));
        Check(File.ReadAllText(renamedPath) == content, "trash and restore UI round-trip preserves the exact file contents");
        await page.ReloadAsync();
    }

    private static void InvokeMenu(TreeView tree, string header)
    {
        var menu = tree.ContextMenu ?? throw new InvalidOperationException("File context menu did not open");
        var item = menu.Items.OfType<MenuItem>().Single(entry => Equals(entry.Header, header));
        ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)!).Invoke();
        menu.IsOpen = false;
    }

    private static async Task OpenNotebookContextMenuAsync(MainWindow main, LearningPage page)
    {
        var tree = LearningControl<TreeView>(page, "LibraryFilesTree");
        var item = Descendants<TreeViewItem>(tree).Single(container => container.IsSelected);
        item.BringIntoView();
        item.Focus();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        // WPF exposes ContextMenuEventArgs constructors internally. Raise its actual routed
        // opening event on the selected item; the production handler creates and opens the menu.
        var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [item, true], null)!;
        item.RaiseEvent(opening);
        await Until(() => tree.ContextMenu?.IsOpen == true);
        Check(opening.Handled && tree.ContextMenu.Items.OfType<MenuItem>().Any(entry => Equals(entry.Header, "重命名…")),
            "selected file opens its real production context menu");
    }

    private static async Task CheckNotebookPanelsAsync(MainWindow main, LearningPage page, string output)
    {
        var shell = page.NotebookControl;
        var left = (ColumnDefinition)shell.FindName("LeftWidth");
        var right = (ColumnDefinition)shell.FindName("RightWidth");
        await Task.Delay(240);
        var originalLeft = left.ActualWidth;
        var splitter = LearningControl<System.Windows.Controls.GridSplitter>(page, "LeftSplitter");
        var resize = (ITransformProvider)new GridSplitterAutomationPeer(splitter).GetPattern(PatternInterface.Transform)!;
        Check(resize.CanMove, "splitter exposes an accessible resize interaction");
        resize.Move(24, 0);
        await Until(() => left.ActualWidth > originalLeft + 10);
        Console.WriteLine($"NOTEBOOK PANELS: left width {originalLeft} -> {left.ActualWidth}");
        Check(left.ActualWidth > originalLeft + 10 && left.ActualWidth >= 180, "left sidebar splitter resizes the actual workspace columns");
        await ClickNotebookAsync(NotebookButton(shell, "折叠左侧栏"));
        await ClickNotebookAsync(NotebookButton(shell, "折叠右侧栏"));
        await Until(() => left.ActualWidth < 1 && right.ActualWidth < 1);
        await Task.Delay(220);
        CheckReachable(LearningControl<Button>(page, "ShowLeftButton"), main, "left sidebar restore remains visible");
        CheckReachable(LearningControl<Button>(page, "ShowRightButton"), main, "right sidebar restore remains visible");
        Snapshot(main, Path.Combine(output, "notebook-panels.png"));
        await ClickNotebookAsync(LearningControl<Button>(page, "ShowLeftButton"));
        await ClickNotebookAsync(LearningControl<Button>(page, "ShowRightButton"));
        await Until(() => left.ActualWidth >= 180 && right.ActualWidth >= 180);
        var dark = shell.IsDark;
        await ClickNotebookAsync(LearningControl<Button>(page, "NotebookThemeButton"));
        await Until(() => shell.IsDark != dark);
        await Task.Delay(220);
        Snapshot(main, Path.Combine(output, "notebook-light.png"));
        await ClickNotebookAsync(LearningControl<Button>(page, "NotebookThemeButton"));
        await Until(() => shell.IsDark == dark);
    }

    private static async Task OpenGraphNoteByKeyboardAsync(NotebookGraphView graph, LibraryNote note)
    {
        graph.FocusNote = note;
        var surface = Descendants<FrameworkElement>(graph).Single(element => element.Name == "GraphCanvas");
        surface.Focus();
        var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(surface), Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent };
        surface.RaiseEvent(enter);
        Check(enter.Handled, "real graph canvas handles Enter through its production keyboard navigation");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    // Shortcuts use the real WPF route and thread modifier state, without global input.
    private static void NotebookShortcut(Window window, Key key)
    {
        var shell = Learning((MainWindow)window).NotebookControl;
        shell.FocusSearch();
        var original = new byte[256];
        Check(GetKeyboardState(original), "smoke can read its thread keyboard state");
        var control = (byte[])original.Clone();
        control[0x11] = control[0xA2] = 0x80;
        Check(SetKeyboardState(control), "smoke can set the thread Control modifier");
        // ShowDialog pumps a nested dispatcher frame. Release the modifier there before
        // the picker accepts Enter, exactly as releasing Ctrl after invoking a shortcut.
        var release = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => SetKeyboardState(original)), DispatcherPriority.Input);
        try
        {
            Check(Keyboard.Modifiers.HasFlag(ModifierKeys.Control), "shortcut sees the Control modifier");
            var target = (UIElement?)Keyboard.FocusedElement ?? shell;
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(shell), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        finally { release.Abort(); SetKeyboardState(original); }
    }
    private static void NativePointer(Window window, Point point, bool right = false)
    {
        EnsureSmokeForeground(window);
        SetCursorPos((int)point.X, (int)point.Y);
        mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, UIntPtr.Zero);
        mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, UIntPtr.Zero);
    }
    private static void EnsureSmokeForeground(Window window)
    {
        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        Check(GetForegroundWindow() == handle, "native smoke input is confined to its own foreground window");
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
