using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;

namespace VocabCollector.Desktop.Learning;

public sealed class LibraryTreeEntry(string name, string path, LibraryNote? note = null) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string RelativePath { get; } = path;
    public LibraryNote? Note { get; } = note;
    public bool IsFolder => Note is null;
    public string Icon => IsFolder ? "folder" : "file";
    public ObservableCollection<LibraryTreeEntry> Children { get; } = [];
    private bool _expanded;
    public bool IsExpanded { get => _expanded; set { _expanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed record NotebookHeadingRow(string Text, int Index, Thickness Indent);

public partial class LibraryShell : UserControl, IDisposable
{
    private LibraryCatalog? _catalog;
    private IReadOnlyList<LibraryNote> _notes = [];
    private LibraryGraph? _graph;
    private NotebookGraphView? _graphView;
    private string _wordRoot = "", _inspector = "outline", _mode = "files";
    private bool _disposed, _suppressTreeOpen, _refreshing, _dark = true, _leftOpen = true, _rightOpen = true, _descending, _operating;
    private double _leftSize = 240, _rightSize = 238;
    private Point _dragStart;
    private LibraryNote? _dragNote;
    private ResourceDictionary? _palette;
    private readonly HashSet<string> _bookmarks = [];
    private readonly DispatcherTimer _searchDebounce;
    private int _searchVersion;
    public ObservableCollection<LibraryTreeEntry> Tree { get; } = [];
    public ObservableCollection<NotebookSearchHit> SearchResults { get; } = [];
    public ObservableCollection<NotebookHeadingRow> Headings { get; } = [];
    public ObservableCollection<LibraryNote> References { get; } = [];
    public NoteWorkspace Documents => Workspace;
    public bool IsDark => _dark;
    public event EventHandler<string>? MessageChanged;
    public event EventHandler<string>? PageRequested;
    public event EventHandler<bool>? ThemeChanged;
    public event EventHandler? ReloadRequested;

    public LibraryShell()
    {
        InitializeComponent();
        DataContext = this;
        ApplyPalette();
        _searchDebounce = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(160) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RefreshSearch(); };
        Workspace.NoteChanged += (_, note) => { if (_disposed) return; UpdateInspector(); if (!_refreshing) Reveal(note); UpdateStatistics(); };
        Workspace.ActiveTabChanged += (_, _) => { UpdateInspector(); UpdateStatistics(); };
        Workspace.QuickOpenRequested += (_, _) => QuickOpen();
        Workspace.NewNoteRequested += async (_, _) => await CreateNoteAsync();
        Workspace.DeleteNoteRequested += async (_, note) => await TrashAsync(note);
        Workspace.NavigationRequested += (_, _) => { if (!_refreshing) ShowDocuments(); };
        Workspace.MessageChanged += (_, message) => SetMessage(message);
        Workspace.FilesChanged += (_, _) => ReloadRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Configure(LibraryCatalog catalog, IReadOnlyList<LibraryNote> notes, string wordRoot)
    {
        if (_disposed) return;
        if (!ReferenceEquals(_catalog, catalog))
        {
            _catalog = catalog;
            _wordRoot = wordRoot;
            LoadPreferences();
            NotebookName.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(wordRoot));
            NotebookName.ToolTip = wordRoot;
        }
        _notes = notes;
        _graph = new(catalog, notes);
        _refreshing = true;
        try { Workspace.Configure(catalog, notes); RebuildTree(); RefreshSearch(); UpdateInspector(); }
        finally { _refreshing = false; }
        if (GraphHost.IsVisible) _graphView?.Configure(catalog, notes);
        UpdateStatistics();
        LibraryEmptyText.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotebookStatus.Text = $"{notes.Count} 篇笔记";
    }

    private void RebuildTree()
    {
        var expanded = Walk(Tree).Where(e => e.IsExpanded).Select(e => e.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, LibraryTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var root = new LibraryTreeEntry("", "") { IsExpanded = true };
        folders[""] = root;
        LibraryTreeEntry Folder(string path)
        {
            if (folders.TryGetValue(path, out var existing)) return existing;
            var slash = path.LastIndexOf('/');
            var parent = Folder(slash < 0 ? "" : path[..slash]);
            var entry = new LibraryTreeEntry(slash < 0 ? path : path[(slash + 1)..], path) { IsExpanded = expanded.Contains(path) || Tree.Count == 0 };
            folders[path] = entry; parent.Children.Add(entry); return entry;
        }
        if (Directory.Exists(_wordRoot))
        {
            var pending = new Stack<string>(); pending.Push(_wordRoot);
            var visited = 0;
            while (pending.TryPop(out var directory) && visited++ < 5000)
            {
                try
                {
                    VaultPaths.EnsureInside(_wordRoot, directory);
                    foreach (var path in Directory.GetDirectories(directory))
                    {
                        if (Path.GetFileName(path).StartsWith('.') || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        Folder(Path.GetRelativePath(_wordRoot, path).Replace('\\', '/')); pending.Push(path);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        foreach (var note in _notes) Folder(note.Folder).Children.Add(new(note.Title, note.RelativePath, note));
        void Sort(LibraryTreeEntry item)
        {
            var ordered = item.Children.OrderBy(e => !e.IsFolder).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (_descending) ordered = ordered.OrderBy(e => !e.IsFolder).ThenByDescending(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            item.Children.Clear(); foreach (var child in ordered) { item.Children.Add(child); Sort(child); }
        }
        Sort(root);
        Tree.Clear(); foreach (var entry in root.Children) Tree.Add(entry);
        if (Workspace.Note is { } active) Reveal(active, false);
    }

    private static IEnumerable<LibraryTreeEntry> Walk(IEnumerable<LibraryTreeEntry> entries)
    { foreach (var entry in entries) { yield return entry; foreach (var child in Walk(entry.Children)) yield return child; } }
    private void Reveal(LibraryNote note, bool expand = true)
    {
        if (expand) foreach (var folder in Walk(Tree).Where(e => e.IsFolder && note.RelativePath.StartsWith(e.RelativePath + "/", StringComparison.OrdinalIgnoreCase))) folder.IsExpanded = true;
        LibraryFilesTree.UpdateLayout();
        var container = Descendants<TreeViewItem>(LibraryFilesTree).FirstOrDefault(i => i.DataContext is LibraryTreeEntry entry && entry.Note?.Id == note.Id);
        if (container == null) return;
        _suppressTreeOpen = true;
        try { container.IsSelected = true; container.BringIntoView(); } finally { _suppressTreeOpen = false; }
    }
    private void OpenDocument(LibraryNote note, bool newTab = false)
    {
        ShowDocuments(); Workspace.OpenNote(note, newTab); UpdateInspector();
    }
    private void ShowDocuments() { GraphHost.Visibility = Visibility.Collapsed; Workspace.Visibility = Visibility.Visible; }
    private void Tree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (!_suppressTreeOpen && !_refreshing && e.NewValue is LibraryTreeEntry { Note: { } note }) OpenDocument(note, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); }
    private void Tree_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        var item = Ancestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        _dragNote = (item?.DataContext as LibraryTreeEntry)?.Note;
        if (_dragNote == null || item == null) return;
        _suppressTreeOpen = true; item.IsSelected = true; _suppressTreeOpen = false;
        OpenDocument(_dragNote, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true;
    }
    private void Tree_RightClick(object sender, MouseButtonEventArgs e)
    { if (Ancestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item) { _suppressTreeOpen = true; item.IsSelected = true; _suppressTreeOpen = false; } }
    private void Tree_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNote == null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var note = _dragNote; _dragNote = null;
        DragDrop.DoDragDrop(LibraryFilesTree, new DataObject("TuciMarkdown", note.Id), DragDropEffects.Move);
    }
    private async void Tree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("TuciMarkdown") || e.Data.GetData("TuciMarkdown") is not string id || _notes.FirstOrDefault(n => n.Id == id) is not { } note) return;
        var target = Ancestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as LibraryTreeEntry;
        var folder = target?.IsFolder == true ? target.RelativePath : target?.Note?.Folder ?? "";
        e.Handled = true;
        await MoveAsync(note, (folder.Length == 0 ? "" : folder + "/") + Path.GetFileName(note.RelativePath));
    }
    private void Tree_Expanded(object sender, RoutedEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation || e.OriginalSource is not TreeViewItem item) return;
        item.UpdateLayout();
        if (Descendants<ItemsPresenter>(item).FirstOrDefault() is { } children) children.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop });
    }

    private async void RefreshSearch()
    {
        var version = ++_searchVersion;
        if (_mode == "bookmarks") { SearchResults.Clear(); foreach (var note in _notes.Where(n => _bookmarks.Contains(n.Id))) SearchResults.Add(new(note, note.Excerpt)); return; }
        if (_mode != "search") return;
        var notes = _notes; var query = LibrarySearchBox.Text;
        var response = await Task.Run(() => { var success = NotebookSearch.TrySearch(notes, query, out var results, out var error); return (success, results, error); });
        if (_disposed || version != _searchVersion) return;
        SearchResults.Clear();
        if (!response.success) { SetMessage(response.error); return; }
        foreach (var hit in response.results) SearchResults.Add(hit);
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (IsInitialized && _searchDebounce != null) { _searchVersion++; _searchDebounce.Stop(); _searchDebounce.Start(); } }
    private void SearchResult_Selected(object sender, SelectionChangedEventArgs e)
    { if (!_refreshing && e.AddedItems.OfType<NotebookSearchHit>().FirstOrDefault() is { } hit) OpenDocument(hit.Note, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); }
    public void FocusSearch()
    {
        _mode = "search"; SetLeft(true); SidebarTitle.Text = "搜索"; FileTools.Visibility = Visibility.Collapsed; SearchTools.Visibility = Visibility.Visible;
        LibraryFilesTree.Visibility = Visibility.Collapsed; LibrarySearchResults.Visibility = Visibility.Visible; RefreshSearch(); LibrarySearchBox.Focus(); LibrarySearchBox.SelectAll();
    }
    private void Files_Click(object sender, RoutedEventArgs e)
    { _mode = "files"; _searchVersion++; SetLeft(true); SidebarTitle.Text = "文件"; FileTools.Visibility = Visibility.Visible; SearchTools.Visibility = Visibility.Collapsed; LibraryFilesTree.Visibility = Visibility.Visible; LibrarySearchResults.Visibility = Visibility.Collapsed; }
    private void Search_Click(object sender, RoutedEventArgs e) => FocusSearch();
    private void Bookmarks_Click(object sender, RoutedEventArgs e)
    { _mode = "bookmarks"; _searchVersion++; SearchResults.Clear(); SetLeft(true); SidebarTitle.Text = "书签"; FileTools.Visibility = Visibility.Collapsed; SearchTools.Visibility = Visibility.Collapsed; LibraryFilesTree.Visibility = Visibility.Collapsed; LibrarySearchResults.Visibility = Visibility.Visible; RefreshSearch(); }

    private void UpdateInspector()
    {
        Headings.Clear(); References.Clear();
        var note = Workspace.Note;
        if (note != null && _graph != null)
        {
            if (_inspector == "outline") foreach (var heading in Workspace.ActiveReader.Outline) Headings.Add(new(heading.Text, heading.Index, new(Math.Max(0, heading.Level - 1) * 10, 0, 0, 0)));
            else foreach (var linked in _inspector == "backlinks" ? _graph.Backlinks(note) : _graph.Outgoing(note)) References.Add(linked);
        }
        NoteOutlineList.Visibility = _inspector == "outline" ? Visibility.Visible : Visibility.Collapsed;
        NoteReferencesList.Visibility = _inspector == "outline" ? Visibility.Collapsed : Visibility.Visible;
        InspectorTitle.Text = _inspector switch { "backlinks" => "反向链接", "outgoing" => "出链", _ => "大纲" };
        InspectorEmpty.Text = note == null ? "打开笔记后在这里查看关联信息。" : _inspector switch { "backlinks" => "没有其他笔记链接到这里。", "outgoing" => "这篇笔记没有指向其他已收录笔记的链接。", _ => "这篇笔记还没有标题。" };
        InspectorEmpty.Visibility = Headings.Count + References.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Heading_Selected(object sender, SelectionChangedEventArgs e)
    { if (e.AddedItems.OfType<NotebookHeadingRow>().FirstOrDefault() is { } heading) { ShowDocuments(); Workspace.NavigateHeading(heading.Index); } }
    private void Reference_Selected(object sender, SelectionChangedEventArgs e)
    { if (!_refreshing && e.AddedItems.OfType<LibraryNote>().FirstOrDefault() is { } note) OpenDocument(note, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); }
    private void Outline_Click(object sender, RoutedEventArgs e) { _inspector = "outline"; UpdateInspector(); }
    private void Backlinks_Click(object sender, RoutedEventArgs e) { _inspector = "backlinks"; UpdateInspector(); }
    private void Outgoing_Click(object sender, RoutedEventArgs e) { _inspector = "outgoing"; UpdateInspector(); }
    private void BookmarkCurrent_Click(object sender, RoutedEventArgs e)
    { if (Workspace.Note is not { } note) return; if (!_bookmarks.Add(note.Id)) _bookmarks.Remove(note.Id); SavePreferences(); SetMessage(_bookmarks.Contains(note.Id) ? "已加入书签。" : "已移除书签。"); if (_mode == "bookmarks") RefreshSearch(); }
    private void UpdateStatistics()
    { NoteStatistics.Text = Workspace.Note is { } note ? $"{Regex.Matches(note.Body, @"\b[\p{L}\p{N}]+\b").Count} 词 · {note.Body.Length} 字符" : $"{_notes.Count} 篇笔记"; }
    public void SetMessage(string message) { if (_disposed) return; NotebookStatus.Text = message; MessageChanged?.Invoke(this, message); }
    private void Graph_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog == null) return;
        if (_graphView == null) { _graphView = new(); _graphView.NoteRequested += (_, note) => OpenDocument(note); Grid.SetRow(_graphView, 1); GraphHost.Children.Add(_graphView); }
        _graphView.Configure(_catalog, _notes); _graphView.FocusNote = Workspace.Note; Workspace.Visibility = Visibility.Collapsed; GraphHost.Visibility = Visibility.Visible;
    }
    private void CloseGraph_Click(object sender, RoutedEventArgs e) => ShowDocuments();
    private void Reveal_Click(object sender, RoutedEventArgs e) { Files_Click(sender, e); if (Workspace.Note is { } note) Reveal(note); }
    private void Sort_Click(object sender, RoutedEventArgs e) { _descending = !_descending; RebuildTree(); }
    private void CollapseAll_Click(object sender, RoutedEventArgs e) { foreach (var entry in Walk(Tree)) entry.IsExpanded = false; }
    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke(this, EventArgs.Empty);
    private void Capture_Click(object sender, RoutedEventArgs e) => PageRequested?.Invoke(this, "capture");
    private void Roaming_Click(object sender, RoutedEventArgs e) => PageRequested?.Invoke(this, "roaming");
    private void Reading_Click(object sender, RoutedEventArgs e) => PageRequested?.Invoke(this, "reading");
    private void Settings_Click(object sender, RoutedEventArgs e) => PageRequested?.Invoke(this, "settings");
    private void Theme_Click(object sender, RoutedEventArgs e) { _dark = !_dark; ApplyPalette(); SavePreferences(); ThemeChanged?.Invoke(this, _dark); }
    private void ApplyPalette()
    { if (_palette != null) Resources.MergedDictionaries.Remove(_palette); _palette = NotebookPalette.Create(_dark); Resources.MergedDictionaries.Add(_palette); }
    private void ToggleLeft_Click(object sender, RoutedEventArgs e) => SetLeft(!_leftOpen);
    private void ToggleRight_Click(object sender, RoutedEventArgs e) => SetRight(!_rightOpen);
    private void SetLeft(bool open)
    {
        if (!open && _leftOpen) _leftSize = Math.Max(180, LeftWidth.ActualWidth);
        _leftOpen = open; AnimateColumn(LeftWidth, open ? _leftSize : 0); LeftDividerWidth.Width = new(open ? 4 : 0); ShowLeftButton.Visibility = open ? Visibility.Collapsed : Visibility.Visible; SavePreferences();
    }
    private void SetRight(bool open)
    {
        if (!open && _rightOpen) _rightSize = Math.Max(180, RightWidth.ActualWidth);
        _rightOpen = open; AnimateColumn(RightWidth, open ? _rightSize : 0); RightDividerWidth.Width = new(open ? 4 : 0); ShowRightButton.Visibility = open ? Visibility.Collapsed : Visibility.Visible; SavePreferences();
    }
    private static void AnimateColumn(ColumnDefinition column, double target)
    {
        var from = column.ActualWidth; column.BeginAnimation(ColumnDefinition.WidthProperty, null); column.Width = new(target);
        if (SystemParameters.ClientAreaAnimation) column.BeginAnimation(ColumnDefinition.WidthProperty, new NotebookColumnAnimation(from, target) { Duration = TimeSpan.FromMilliseconds(170), FillBehavior = FillBehavior.Stop });
    }
    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_leftOpen) LeftWidth.Width = new(_leftSize = Math.Clamp(LeftWidth.ActualWidth, 180, Math.Max(180, ActualWidth * 0.35)));
        if (_rightOpen) RightWidth.Width = new(_rightSize = Math.Clamp(RightWidth.ActualWidth, 180, Math.Max(180, ActualWidth * 0.32)));
        SavePreferences();
    }
    private static T? Ancestor<T>(DependencyObject? value) where T : DependencyObject
    { while (value != null) { if (value is T target) return target; value = value is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(value); } return null; }
    internal static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T target) yield return target; foreach (var item in Descendants<T>(child)) yield return item; } }
    public void Dispose() { if (_disposed) return; SavePreferences(); _disposed = true; _searchVersion++; _searchDebounce.Stop(); Workspace.Dispose(); _graphView?.Dispose(); }
}

public sealed class NotebookColumnAnimation(double from, double to) : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(GridLength);
    protected override Freezable CreateInstanceCore() => new NotebookColumnAnimation(from, to);
    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    { var progress = animationClock.CurrentProgress ?? 1; var eased = 1 - Math.Pow(1 - progress, 3); return new GridLength(Math.Max(0, from + (to - from) * eased)); }
}
