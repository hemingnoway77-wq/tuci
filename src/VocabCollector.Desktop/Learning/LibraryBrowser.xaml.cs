using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Desktop.Learning;

public partial class LibraryBrowser : UserControl, INotifyPropertyChanged, IDisposable
{
    private LibraryCatalog? _catalog;
    private IReadOnlyList<LibraryNote> _notes = [], _visible = [];
    private Dictionary<string, bool>? _beforeSearch;
    private string _wordRoot = "", _query = "";
    private bool _refreshing, _disposed, _filesOpen = true;
    private double _filesWidth = 270;
    private DispatcherOperation? _treeRestore;

    public LibraryBrowser()
    {
        InitializeComponent(); DataContext = this;
        Reader.DirectLinks = true;
        Reader.NoteChanged += Reader_NoteChanged;
        Reader.NavigationRequested += (_, _) =>
        {
            if (_disposed || _refreshing) return;
            CancelTreeRestore(); UpdateReaderChrome();
            if (Reader.Note is { } note) RevealNote(note);
            NavigationRequested?.Invoke(this, EventArgs.Empty);
        };
        Reader.NewTabRequested += (_, note) => OpenNote(note);
        Reader.MessageChanged += (_, message) => { if (message.Length > 0) SetMessage(message); };
        Reader.WebLinkRequested += (_, url) => WebLinkRequested?.Invoke(this, url);
        UpdateReaderChrome(); UpdateEmptyState();
    }
    public NoteReader Reader => ReadingSurface;
    public ObservableCollection<LibraryTreeEntry> Tree { get; } = [];
    public IReadOnlyList<LibraryNote> VisibleNotes => _visible;
    public string Query
    {
        get => _query;
        set
        {
            value ??= "";
            if (_query == value || _disposed) return;
            if (_query.Trim().Length == 0 && value.Trim().Length > 0)
                _beforeSearch = Walk(Tree).Where(entry => entry.IsFolder).ToDictionary(entry => entry.RelativePath, entry => entry.IsExpanded, StringComparer.OrdinalIgnoreCase);
            var restoreExpansion = _query.Trim().Length > 0 && value.Trim().Length == 0;
            _query = value; PropertyChanged?.Invoke(this, new(nameof(Query)));
            RebuildTree(restoreExpansion);
            if (restoreExpansion) _beforeSearch = null;
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<LibraryNote>? NoteChanged;
    public event EventHandler? NavigationRequested;
    public event EventHandler<string>? MessageChanged;
    public event EventHandler<string>? WebLinkRequested;
    public event EventHandler? ReloadRequested;
    public event EventHandler<IReadOnlyList<string>>? ImportRequested;
    public void Configure(LibraryCatalog catalog, IReadOnlyList<LibraryNote> notes, string wordRoot)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(notes);
        var root = Path.GetFullPath(wordRoot);
        if (_catalog is null || !_wordRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            _catalog = catalog; _wordRoot = root; Tree.Clear(); _beforeSearch = null;
        }
        _notes = notes.ToArray();
        _refreshing = true;
        try { Reader.Configure(_catalog, _notes); }
        finally { _refreshing = false; }
        RebuildTree(); UpdateReaderChrome();
    }

    private void RebuildTree(bool restoreExpansion = false)
    {
        var previous = Walk(Tree).ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var treeScroll = Descendants<ScrollViewer>(BrowserFilesTree).FirstOrDefault();
        var scrollOffset = treeScroll?.VerticalOffset ?? 0;
        var selectedPath = (BrowserFilesTree.SelectedItem as LibraryTreeEntry)?.RelativePath;
        var selectedId = Reader.Note?.Id;
        var query = _query.Trim();
        _visible = _notes.Where(note => query.Length == 0 || note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            note.RelativePath.Contains(query, StringComparison.CurrentCultureIgnoreCase) || note.Markdown.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        PropertyChanged?.Invoke(this, new(nameof(VisibleNotes)));
        var entries = new Dictionary<string, LibraryTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<LibraryTreeEntry>>(StringComparer.OrdinalIgnoreCase) { [""] = [] };
        LibraryTreeEntry Folder(string path)
        {
            if (entries.TryGetValue(path, out var found)) return found;
            var slash = path.LastIndexOf('/');
            var parent = slash < 0 ? "" : path[..slash];
            if (parent.Length > 0) Folder(parent);
            var folder = previous.TryGetValue(path, out var old) && old.IsFolder ? old : new LibraryTreeEntry(path.Split('/').Last(), path) { IsExpanded = true };
            if (query.Length > 0) folder.IsExpanded = true;
            else if (restoreExpansion && _beforeSearch?.TryGetValue(path, out var expanded) == true) folder.IsExpanded = expanded;
            entries[path] = folder; children[path] = []; children[parent].Add(folder); return folder;
        }
        _refreshing = true;
        try
        {
            if (query.Length == 0)
                foreach (var folder in FoldersOnDisk()) Folder(folder);
            foreach (var note in _visible)
            {
                if (note.Folder.Length > 0) Folder(note.Folder);
                var entry = previous.TryGetValue(note.RelativePath, out var old) && old.Note == note ? old : new LibraryTreeEntry(note.Title, note.RelativePath, note);
                entries[note.RelativePath] = entry;
                children[note.Folder].Add(entry);
            }
            foreach (var (path, list) in children.Where(pair => pair.Key.Length > 0))
                Synchronize(entries[path].Children, Order(list));
            Synchronize(Tree, Order(children[""]));
        }
        finally { _refreshing = false; }
        UpdateEmptyState();
        _treeRestore?.Abort();
        _treeRestore = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _treeRestore = null;
            if (_disposed) return;
            _refreshing = true;
            try
            {
                BrowserFilesTree.UpdateLayout();
                if (selectedPath is not null) SelectVisibleEntry(entry => entry.RelativePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase), false);
                else if (selectedId is not null) SelectVisibleNote(selectedId, false);
                Descendants<ScrollViewer>(BrowserFilesTree).FirstOrDefault()?.ScrollToVerticalOffset(scrollOffset);
            }
            finally { _refreshing = false; }
        }));
    }

    private IReadOnlyList<string> FoldersOnDisk()
    {
        var folders = new List<string>();
        if (_wordRoot.Length == 0 || !Directory.Exists(_wordRoot)) return folders;
        var pending = new Stack<string>(); pending.Push(_wordRoot);
        while (pending.TryPop(out var directory) && folders.Count < 5000)
        {
            try
            {
                VaultPaths.EnsureInside(_wordRoot, directory);
                foreach (var path in Directory.GetDirectories(directory))
                {
                    if (Path.GetFileName(path).StartsWith('.') || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                    VaultPaths.EnsureInside(_wordRoot, path);
                    folders.Add(Path.GetRelativePath(_wordRoot, path).Replace('\\', '/')); pending.Push(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { SetMessage("部分文件夹暂时无法列出，请检查目录权限后刷新。"); }
        }
        return folders;
    }

    private static IReadOnlyList<LibraryTreeEntry> Order(IEnumerable<LibraryTreeEntry> entries) => entries
        .OrderBy(entry => !entry.IsFolder).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    private static void Synchronize(ObservableCollection<LibraryTreeEntry> collection, IReadOnlyList<LibraryTreeEntry> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < collection.Count && ReferenceEquals(collection[index], desired[index])) continue;
            var existing = collection.IndexOf(desired[index]);
            if (existing >= 0) collection.Move(existing, index); else collection.Insert(index, desired[index]);
        }
        while (collection.Count > desired.Count) collection.RemoveAt(collection.Count - 1);
    }

    public void OpenNote(LibraryNote note)
    { if (!_disposed) { CancelTreeRestore(); Reader.OpenNote(note); } }
    public void HandleLink(PreviewLinkEventArgs args, LibraryNote? source, FrameworkElement? target = null)
    {
        if (_disposed) return;
        CancelTreeRestore();
        if (source is not null)
        {
            _refreshing = true;
            try { Reader.OpenNote(source, false, (target as MarkdownPreview)?.VerticalOffset); }
            finally { _refreshing = false; }
            RevealNote(source);
        }
        Reader.HandleLink(args, source, target);
    }
    private void Reader_NoteChanged(object? sender, LibraryNote note)
    {
        if (_disposed) return;
        UpdateReaderChrome();
        if (!_refreshing)
        {
            CancelTreeRestore(); RevealNote(note); AnimateReader();
        }
        NoteChanged?.Invoke(this, note);
    }
    private void CancelTreeRestore() { _treeRestore?.Abort(); _treeRestore = null; }
    private void RevealNote(LibraryNote note)
    {
        foreach (var folder in Walk(Tree).Where(entry => entry.IsFolder && note.RelativePath.StartsWith(entry.RelativePath + "/", StringComparison.OrdinalIgnoreCase))) folder.IsExpanded = true;
        BrowserFilesTree.UpdateLayout(); SelectVisibleNote(note.Id, true);
    }
    private void SelectVisibleNote(string id, bool bringIntoView)
        => SelectVisibleEntry(entry => entry.Note?.Id == id, bringIntoView);
    private void SelectVisibleEntry(Func<LibraryTreeEntry, bool> match, bool bringIntoView)
    {
        var item = Descendants<TreeViewItem>(BrowserFilesTree).FirstOrDefault(container => container.DataContext is LibraryTreeEntry entry && match(entry));
        if (item is null) return;
        var refreshing = _refreshing; _refreshing = true;
        try { item.IsSelected = true; if (bringIntoView) item.BringIntoView(); }
        finally { _refreshing = refreshing; }
    }
    private void UpdateReaderChrome()
    {
        DocumentPathText.Text = Reader.Note?.RelativePath ?? "笔记阅读";
        DocumentPathText.ToolTip = Reader.Note?.RelativePath;
        BrowserBackButton.IsEnabled = Reader.CanGoBack; BrowserForwardButton.IsEnabled = Reader.CanGoForward;
        ReaderEmptyState.Visibility = Reader.Note is null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateEmptyState()
    {
        FileCountText.Text = $"{_visible.Count} 篇笔记";
        FilesEmptyText.Text = _notes.Count == 0 ? "还没有笔记。可以先收录单词，或导入 Markdown。" : "没有匹配的笔记。";
        FilesEmptyText.Visibility = Tree.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Tree_Selected(object sender, RoutedPropertyChangedEventArgs<object> args)
    {
        if (_refreshing || _disposed || args.NewValue is not LibraryTreeEntry { Note: { } note }) return;
        Reader.OpenNote(note);
        var selected = Descendants<TreeViewItem>(BrowserFilesTree).FirstOrDefault(item => item.DataContext == args.NewValue);
        if (selected?.Template?.FindName("Row", selected) is FrameworkElement row) Fade(row, 130);
    }
    private void Tree_Expanded(object sender, RoutedEventArgs args)
    {
        if (_refreshing || _disposed || args.OriginalSource is not TreeViewItem { IsLoaded: true } item) return;
        item.UpdateLayout();
        if (item.Template?.FindName("Children", item) is FrameworkElement children) Fade(children, 140);
    }
    private void Tree_Collapsed(object sender, RoutedEventArgs args)
    {
        if (!_refreshing && args.OriginalSource is TreeViewItem item && item.Template?.FindName("Row", item) is FrameworkElement row) Fade(row, 130);
    }
    private static void Fade(FrameworkElement element, int duration)
    {
        element.BeginAnimation(OpacityProperty, null); element.Opacity = 1;
        if (SystemParameters.ClientAreaAnimation) element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(duration)) { FillBehavior = FillBehavior.Stop });
    }
    private void AnimateReader()
    {
        Fade(ReaderPane, 150);
        var translation = (TranslateTransform)ReaderPane.RenderTransform;
        translation.BeginAnimation(TranslateTransform.YProperty, null); translation.Y = 0;
        if (SystemParameters.ClientAreaAnimation) translation.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    private void CollapseFiles_Click(object sender, RoutedEventArgs args) => SetFilesOpen(false);
    private void ShowFiles_Click(object sender, RoutedEventArgs args) => SetFilesOpen(true);
    private void SetFilesOpen(bool open)
    {
        if (_disposed || _filesOpen == open) return;
        if (!open) _filesWidth = Math.Clamp(FilesColumn.ActualWidth, 200, 380);
        var from = FilesColumn.ActualWidth; var to = open ? _filesWidth : 0;
        _filesOpen = open; FileListPane.Visibility = Visibility.Visible;
        ShowFilesButton.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        BrowserSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SplitterColumn.Width = new GridLength(open ? 16 : 0);
        FilesColumn.BeginAnimation(ColumnDefinition.WidthProperty, null); FilesColumn.Width = new GridLength(to);
        if (SystemParameters.ClientAreaAnimation && IsLoaded)
        {
            var animation = new ColumnWidthAnimation(from, to) { Duration = TimeSpan.FromMilliseconds(150), FillBehavior = FillBehavior.Stop };
            animation.Completed += (_, _) => { if (!_disposed && !_filesOpen) FileListPane.Visibility = Visibility.Collapsed; };
            FilesColumn.BeginAnimation(ColumnDefinition.WidthProperty, animation);
        }
        else FileListPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs args)
    { if (_filesOpen) { _filesWidth = Math.Clamp(FilesColumn.ActualWidth, 200, 380); FilesColumn.Width = new(_filesWidth); } }
    private void Back_Click(object sender, RoutedEventArgs args) => Reader.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs args) => Reader.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs args) { if (!_disposed) ReloadRequested?.Invoke(this, EventArgs.Empty); }
    private void Import_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        var picker = new OpenFileDialog { Title = "导入 Markdown 笔记", Filter = "Markdown 笔记|*.md", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) ImportRequested?.Invoke(this, picker.FileNames);
    }
    public void SetMessage(string message)
    { if (!_disposed) { FileCountText.ToolTip = message; MessageChanged?.Invoke(this, message); } }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _treeRestore?.Abort();
        Reader.NoteChanged -= Reader_NoteChanged; Reader.Dispose();
    }
    private static IEnumerable<LibraryTreeEntry> Walk(IEnumerable<LibraryTreeEntry> entries)
    { foreach (var entry in entries) { yield return entry; foreach (var child in Walk(entry.Children)) yield return child; } }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private sealed class ColumnWidthAnimation(double from, double to) : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(GridLength);
        protected override Freezable CreateInstanceCore() => new ColumnWidthAnimation(from, to);
        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
        {
            var progress = animationClock.CurrentProgress ?? 1;
            var eased = 1 - Math.Pow(1 - progress, 3);
            return new GridLength(Math.Max(0, from + (to - from) * eased));
        }
    }
}
