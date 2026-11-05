using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VocabCollector.Core.Library;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Desktop.Learning;

/// <summary>Each tab owns its reader, source draft and reading history.</summary>
public sealed class NoteWorkspace : UserControl, IDisposable
{
    private readonly List<WorkspaceTab> _tabs = [];
    private readonly Stack<ClosedTab> _closed = new();
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _path = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _saveStatus = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
    private readonly TextBox _findBox = new() { Width = 240, Margin = new Thickness(8, 4, 8, 4) };
    private readonly StackPanel _findBar = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly StackPanel _conflictBar = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly Button _editButton, _backButton, _forwardButton;
    private readonly DispatcherTimer _autosave;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private LibraryCatalog? _catalog;
    private IReadOnlyList<LibraryNote> _notes = [];
    private WorkspaceTab _active = null!;
    private bool _updating, _disposed, _restoring;
    private string? _dragging;
    private Point _dragStart;

    public NoteWorkspace()
    {
        SetResourceReference(BackgroundProperty, "NotebookBackground");
        SetResourceReference(ForegroundProperty, "NotebookText");
        var grid = new Grid();
        foreach (var height in new[] { 36.0, 34.0, double.NaN, double.NaN })
            grid.RowDefinitions.Add(new RowDefinition { Height = double.IsNaN(height) ? GridLength.Auto : new GridLength(height) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var tabs = new DockPanel();
        var plus = MakeButton("+", "新标签 Ctrl+T", (_, _) => NewTab());
        DockPanel.SetDock(plus, Dock.Right); tabs.Children.Add(plus);
        tabs.Children.Add(new ScrollViewer { Content = _tabStrip, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        grid.Children.Add(Separate(tabs, "NotebookSurface"));
        var toolbar = new DockPanel { Margin = new Thickness(8, 0, 8, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _backButton = MakeButton("←", "后退", (_, _) => GoBack()); actions.Children.Add(_backButton);
        _forwardButton = MakeButton("→", "前进", (_, _) => GoForward()); actions.Children.Add(_forwardButton);
        _editButton = MakeButton("源码", "阅读 / 源码 Ctrl+E", (_, _) => ToggleEditing());
        DockPanel.SetDock(actions, Dock.Left); toolbar.Children.Add(actions);
        DockPanel.SetDock(_editButton, Dock.Right); toolbar.Children.Add(_editButton);
        DockPanel.SetDock(_saveStatus, Dock.Right); toolbar.Children.Add(_saveStatus);
        toolbar.Children.Add(_path);
        var toolbarBorder = Separate(toolbar, "NotebookBackground"); Grid.SetRow(toolbarBorder, 1); grid.Children.Add(toolbarBorder);
        _findBar.Children.Add(_findBox);
        _findBar.Children.Add(MakeButton("查找", "查找下一个 Enter", (_, _) => FindNext()));
        _findBar.Children.Add(MakeButton("×", "关闭查找 Escape", (_, _) => _findBar.Visibility = Visibility.Collapsed));
        _findBox.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; FindNext(); } else if (args.Key == Key.Escape) _findBar.Visibility = Visibility.Collapsed; };
        Grid.SetRow(_findBar, 2); grid.Children.Add(_findBar);
        _conflictBar.Children.Add(new TextBlock { Text = "保存失败，草稿已保留。", Margin = new Thickness(12, 8, 8, 8), VerticalAlignment = VerticalAlignment.Center });
        _conflictBar.Children.Add(MakeButton("另存副本", "保留外部文件并保存本地草稿副本", async (_, _) => await SaveCopyAsync(_active)));
        Grid.SetRow(_conflictBar, 3); grid.Children.Add(_conflictBar);
        Grid.SetRow(_body, 4); grid.Children.Add(_body); Content = grid;
        _autosave = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(1000) };
        _autosave.Tick += OnAutosave; PreviewKeyDown += OnWorkspaceKey; NewTab();
    }

    public NoteReader ActiveReader => _active.Reader;
    public LibraryNote? Note => _active.Note;
    public bool HasUnsavedChanges => _tabs.Any(tab => tab.Dirty);
    public event EventHandler<LibraryNote>? NoteChanged;
    public event EventHandler? NavigationRequested;
    public event EventHandler<string>? MessageChanged;
    public event EventHandler<string>? WebLinkRequested;
    public event EventHandler? FilesChanged;
    public event EventHandler? ActiveTabChanged;
    public event EventHandler? QuickOpenRequested;
    public event EventHandler? NewNoteRequested;

    public void Configure(LibraryCatalog catalog, IReadOnlyList<LibraryNote> notes)
    {
        if (_disposed) return;
        if (ReferenceEquals(catalog, _catalog)) { RefreshNotes(notes); return; }
        if (HasUnsavedChanges) { Message("请先保存当前词库的草稿。"); return; }
        foreach (var tab in _tabs) tab.Reader.Dispose();
        _tabs.Clear(); _closed.Clear(); _catalog = catalog; _notes = notes.ToArray();
        _restoring = true;
        try { RestoreSession(); } finally { _restoring = false; }
        if (_tabs.Count == 0) NewTab(); else SelectTab(_tabs[Math.Clamp(_restoredActive, 0, _tabs.Count - 1)]);
    }

    public void RefreshNotes(IReadOnlyList<LibraryNote> notes)
    {
        if (_disposed) return;
        _notes = notes.ToArray();
        foreach (var tab in _tabs)
        {
            if (tab.Note is null) { tab.Reader.RefreshNotes(_notes); continue; }
            var refreshed = notes.FirstOrDefault(note => note.Id == tab.Note.Id);
            if (tab.Dirty)
            {
                if (refreshed is null || refreshed.Markdown != tab.Note.Markdown || refreshed.AbsolutePath != tab.Note.AbsolutePath)
                { tab.Conflict = true; Message("文件已在外部更改。草稿已保留，可另存副本。"); }
                continue;
            }
            if (refreshed is not null) { tab.Note = refreshed; SetDraft(tab, refreshed.Markdown); }
            _updating = true;
            try { tab.Reader.RefreshNotes(notes); } finally { _updating = false; }
        }
        UpdateChrome(); if (Note is { } current) NoteChanged?.Invoke(this, current);
    }

    public void OpenNote(LibraryNote note, bool newTab = false, double? offset = null)
    {
        if (_disposed || _catalog is null) return;
        if (newTab || (_active.Dirty && _active.Note?.Id != note.Id)) AddTab();
        if (_active.Dirty && _active.Note?.Id == note.Id) { SelectTab(_active); return; }
        _active.Reader.OpenNote(note, true, offset); SelectTab(_active);
    }

    public void HandleLink(PreviewLinkEventArgs args, LibraryNote? source, FrameworkElement? target = null)
    {
        if (_disposed) return;
        if (source is not null && (target is MarkdownPreview || ActiveReader.Note?.Id != source.Id))
        {
            if (_active.Dirty) AddTab();
            LoadNote(_active, source, (target as MarkdownPreview)?.VerticalOffset);
        }
        ActiveReader.HandleLink(args, source, target);
    }
    public void NewTab() { if (!_disposed) SelectTab(AddTab()); }

    private WorkspaceTab AddTab()
    {
        var tab = new WorkspaceTab(); tab.Reader.DirectLinks = true;
        tab.Reader.NavigationGuard = note =>
        {
            if (_updating || !tab.Dirty) return true;
            if (tab.Note?.Id != note.Id) OpenNote(note, true);
            else NavigationRequested?.Invoke(this, EventArgs.Empty);
            return false;
        };
        if (_catalog is not null) tab.Reader.Configure(_catalog, _notes);
        tab.Editor.Name = "WorkspaceEditor"; tab.Editor.AcceptsReturn = true; tab.Editor.AcceptsTab = true;
        tab.Editor.TextWrapping = TextWrapping.Wrap; tab.Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        tab.Editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        tab.Editor.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI, Microsoft YaHei"); tab.Editor.FontSize = 15;
        tab.Editor.Padding = new Thickness(30, 20, 30, 40); tab.Editor.BorderThickness = new Thickness(0);
        tab.Editor.SetResourceReference(BackgroundProperty, "NotebookBackground"); tab.Editor.SetResourceReference(ForegroundProperty, "NotebookText");
        tab.Editor.SetResourceReference(TextBox.SelectionBrushProperty, "NotebookSelection");
        tab.Editor.TextChanged += (_, _) =>
        {
            if (_updating || _disposed) return;
            tab.Draft = tab.Editor.Text; UpdateChrome(); _autosave.Stop(); _autosave.Start();
        };
        tab.Reader.NoteChanged += (_, note) =>
        {
            if (_updating || _disposed) return;
            tab.Note = note; SetDraft(tab, note.Markdown); tab.Conflict = false;
            if (ReferenceEquals(tab, _active)) { UpdateChrome(); NoteChanged?.Invoke(this, note); }
        };
        tab.Reader.NewTabRequested += (_, note) => OpenNote(note, true);
        tab.Reader.NavigationRequested += (_, _) =>
        {
            if (_updating || !ReferenceEquals(tab, _active)) return;
            UpdateChrome(); PersistSession(); NavigationRequested?.Invoke(this, EventArgs.Empty);
        };
        tab.Reader.MessageChanged += (_, text) => { if (!_updating && ReferenceEquals(tab, _active) && text.Length > 0) Message(text); };
        tab.Reader.WebLinkRequested += (_, url) => WebLinkRequested?.Invoke(this, url);
        tab.EmptyState.HorizontalAlignment = HorizontalAlignment.Center; tab.EmptyState.VerticalAlignment = VerticalAlignment.Center;
        tab.EmptyState.Children.Add(new TextBlock { Text = "未打开笔记", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        var open = MakeButton("打开笔记", "快速切换 Ctrl+O", (_, _) => QuickOpenRequested?.Invoke(this, EventArgs.Empty));
        var create = MakeButton("新建笔记", "新建笔记 Ctrl+N", (_, _) => NewNoteRequested?.Invoke(this, EventArgs.Empty));
        open.SetResourceReference(StyleProperty, "NotebookAction"); create.SetResourceReference(StyleProperty, "NotebookAction");
        open.Margin = new Thickness(0, 0, 0, 8); tab.EmptyState.Children.Add(open); tab.EmptyState.Children.Add(create);
        tab.Body.Children.Add(tab.Reader); tab.Body.Children.Add(tab.Editor); tab.Body.Children.Add(tab.EmptyState); tab.Editor.Visibility = Visibility.Collapsed;
        _tabs.Add(tab); _active = tab; return tab;
    }
    private void LoadNote(WorkspaceTab tab, LibraryNote note, double? offset = null)
    {
        _updating = true;
        try { tab.Note = note; SetDraft(tab, note.Markdown); tab.Conflict = false; tab.Reader.OpenNote(note, false, offset); }
        finally { _updating = false; }
    }
    private void SetDraft(WorkspaceTab tab, string markdown)
    {
        var previous = _updating; _updating = true;
        try { tab.Draft = markdown; if (tab.Editor.Text != markdown) tab.Editor.Text = markdown; }
        finally { _updating = previous; }
    }
    private void SelectTab(WorkspaceTab tab)
    {
        _active = tab; _body.Content = tab.Body;
        tab.Reader.Visibility = tab.Editing ? Visibility.Collapsed : Visibility.Visible;
        tab.Editor.Visibility = tab.Editing ? Visibility.Visible : Visibility.Collapsed; UpdateChrome();
        if (SystemParameters.ClientAreaAnimation && IsLoaded) _body.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        if (tab.Note is { } note) NoteChanged?.Invoke(this, note);
        ActiveTabChanged?.Invoke(this, EventArgs.Empty); PersistSession();
    }

    public async Task CloseActiveTabAsync()
    {
        if (_disposed) return;
        var closing = _active;
        if (!await SaveTabAsync(closing)) { SelectTab(closing); return; }
        var index = _tabs.IndexOf(closing);
        if (index < 0) return;
        if (closing.Note is { } note) _closed.Push(new(note, closing.Reader.VerticalOffset));
        _tabs.Remove(closing); closing.Reader.Dispose();
        if (_tabs.Count == 0) NewTab(); else SelectTab(_tabs[Math.Clamp(index, 0, _tabs.Count - 1)]);
    }
    public void ReopenClosedTab()
    {
        if (_disposed || !_closed.TryPop(out var tab)) return;
        OpenNote(_notes.FirstOrDefault(note => note.Id == tab.Note.Id) ?? tab.Note, true, tab.Offset);
    }
    public void NextTab(int delta)
    {
        if (_disposed || _tabs.Count == 0) return;
        SelectTab(_tabs[((_tabs.IndexOf(_active) + delta) % _tabs.Count + _tabs.Count) % _tabs.Count]);
    }
    public void SelectTabNumber(int number)
    {
        if (_disposed || _tabs.Count == 0 || number < 1 || number > 9) return;
        if (number == 9) SelectTab(_tabs[^1]); else if (number <= _tabs.Count) SelectTab(_tabs[number - 1]);
    }
    public void GoBack() { if (!_active.Dirty) ActiveReader.GoBack(); else Message("请先保存草稿再返回。"); }
    public void GoForward() { if (!_active.Dirty) ActiveReader.GoForward(); else Message("请先保存草稿再前进。"); }
    public void Find() { _findBar.Visibility = Visibility.Visible; _findBox.Focus(); _findBox.SelectAll(); }
    public void NavigateHeading(int index)
    {
        if (_disposed || index < 0) return;
        if (!_active.Editing) { ActiveReader.NavigateHeading(index); return; }
        if (_active.Note is null) return;
        var text = _active.Editor.Text;
        if (ActiveReader.Note?.Markdown != text)
        {
            _updating = true;
            try { ActiveReader.OpenNote(_active.Note with { Markdown = text }, false, ActiveReader.VerticalOffset); }
            finally { _updating = false; }
        }
        if (ActiveReader.HeadingSourceOffset(index) is not { } offset || offset < 0 || offset > text.Length) return;
        var end = text.IndexOf('\n', offset);
        _active.Editor.Select(offset, (end < 0 ? text.Length : end) - offset);
        _active.Editor.ScrollToLine(_active.Editor.GetLineIndexFromCharacterIndex(offset));
        _active.Editor.Focus();
    }
    private void FindNext()
    {
        if (_active.Editing)
        {
            var query = _findBox.Text; if (query.Length == 0) return;
            var text = _active.Editor.Text;
            var index = text.IndexOf(query, Math.Min(text.Length, _active.Editor.SelectionStart + _active.Editor.SelectionLength), StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
            if (index >= 0) { _active.Editor.Select(index, query.Length); _active.Editor.ScrollToLine(_active.Editor.GetLineIndexFromCharacterIndex(index)); _active.Editor.Focus(); }
            else Message("未找到匹配文字。");
        }
        else if (!ActiveReader.FindText(_findBox.Text)) Message("未找到匹配文字。");
    }
    public void ToggleEditing()
    {
        if (_disposed || _active.Note is null) { Message("先打开或新建一篇笔记。"); return; }
        _active.Editing = !_active.Editing;
        if (!_active.Editing)
        {
            _updating = true;
            try { _active.Reader.OpenNote(_active.Note with { Markdown = _active.Draft }, false, _active.Reader.VerticalOffset); }
            finally { _updating = false; }
        }
        SelectTab(_active); if (_active.Editing) _active.Editor.Focus();
    }
    public async Task<bool> SaveAllAsync()
    {
        if (_disposed) return !HasUnsavedChanges;
        _autosave.Stop(); var saved = true; WorkspaceTab? failed = null;
        foreach (var tab in _tabs.ToArray()) if (!await SaveTabAsync(tab)) { saved = false; failed ??= tab; }
        if (failed != null) { SelectTab(failed); NavigationRequested?.Invoke(this, EventArgs.Empty); }
        if (saved && !HasUnsavedChanges) PersistSession(); return saved && !HasUnsavedChanges;
    }
    private async Task<bool> SaveTabAsync(WorkspaceTab tab)
    {
        if (!tab.Dirty) return true;
        if (_catalog is null || tab.Note is null) return false;
        await _saveGate.WaitAsync();
        try
        {
            if (!tab.Dirty) return true;
            var saved = await _catalog.SaveNoteAsync(tab.Note, tab.Draft);
            if (_disposed) return false;
            tab.Note = saved; tab.Conflict = false;
            _notes = _notes.Where(note => note.Id != saved.Id).Append(saved).ToArray();
            _updating = true;
            try { tab.Reader.OpenNote(saved with { Markdown = tab.Draft }, false, tab.Reader.VerticalOffset); }
            finally { _updating = false; }
            UpdateChrome(); FilesChanged?.Invoke(this, EventArgs.Empty);
            if (ReferenceEquals(tab, _active)) NoteChanged?.Invoke(this, saved); return !tab.Dirty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            tab.Conflict = true; UpdateChrome();
            Message(error is LibraryConflictException ? "文件已在外部更改。草稿已保留，请另存副本。" : "保存失败，草稿已保留：" + error.Message); return false;
        }
        finally { _saveGate.Release(); }
    }
    private async Task SaveCopyAsync(WorkspaceTab tab)
    {
        if (_catalog is null || tab.Note is null || _disposed) return;
        var note = tab.Note; var folder = note.Folder.Length > 0 ? note.Folder + "/" : ""; var draft = tab.Draft;
        try
        {
            var saved = await _catalog.CreateNoteAsync(folder + note.Title + " (副本 " + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4] + ").md", draft);
            if (_disposed) return;
            _notes = _notes.Append(saved).ToArray(); tab.Note = saved; tab.Conflict = false;
            _updating = true;
            try { tab.Reader.RefreshNotes(_notes); tab.Reader.OpenNote(saved with { Markdown = tab.Draft }, false); }
            finally { _updating = false; }
            UpdateChrome(); FilesChanged?.Invoke(this, EventArgs.Empty);
            if (ReferenceEquals(tab, _active)) NoteChanged?.Invoke(this, saved);
            Message("草稿已另存副本，外部原文件保持不变。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { Message("副本暂未保存，草稿仍保留：" + error.Message); }
    }
    private async void OnAutosave(object? sender, EventArgs args)
    {
        _autosave.Stop();
        foreach (var tab in _tabs.Where(tab => tab.Dirty && !tab.Conflict).ToArray()) await SaveTabAsync(tab);
    }
    private void UpdateChrome()
    {
        if (_active is null) return;
        _tabStrip.Children.Clear();
        foreach (var tab in _tabs)
        {
            tab.EmptyState.Visibility = tab.Note is null ? Visibility.Visible : Visibility.Collapsed;
            var row = new DockPanel { Margin = new Thickness(10, 0, 4, 0) };
            var close = MakeButton("×", "关闭标签 Ctrl+W", async (_, _) => { SelectTab(tab); await CloseActiveTabAsync(); });
            DockPanel.SetDock(close, Dock.Right); row.Children.Add(close);
            row.Children.Add(new TextBlock { Text = (tab.Dirty ? "● " : "") + (tab.Note?.Title ?? "新标签"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 });
            var header = new Border { Child = row, Height = 35, MinWidth = 120, MaxWidth = 240, AllowDrop = true, BorderThickness = new Thickness(0, 0, 1, 0), ToolTip = tab.Note?.RelativePath ?? "新标签" };
            header.SetResourceReference(Border.BackgroundProperty, ReferenceEquals(tab, _active) ? "NotebookBackground" : "NotebookSurface");
            header.SetResourceReference(Border.BorderBrushProperty, "NotebookBorder");
            header.PreviewMouseLeftButtonDown += (_, args) => { _dragStart = args.GetPosition(this); _dragging = tab.Id; };
            header.MouseLeftButtonUp += (_, _) => { _dragging = null; SelectTab(tab); };
            header.PreviewMouseMove += (_, args) =>
            {
                if (args.LeftButton != MouseButtonState.Pressed || _dragging != tab.Id || (args.GetPosition(this) - _dragStart).Length < SystemParameters.MinimumHorizontalDragDistance) return;
                DragDrop.DoDragDrop(header, new DataObject("TuciWorkspaceTab", tab.Id), DragDropEffects.Move); _dragging = null;
            };
            header.Drop += (_, args) =>
            {
                if (args.Data.GetData("TuciWorkspaceTab") is not string id || _tabs.FirstOrDefault(t => t.Id == id) is not { } moved || ReferenceEquals(moved, tab)) return;
                var target = _tabs.IndexOf(tab); _tabs.Remove(moved); _tabs.Insert(Math.Min(target, _tabs.Count), moved); UpdateChrome(); PersistSession(); args.Handled = true;
            };
            _tabStrip.Children.Add(header);
        }
        _path.Text = Note?.RelativePath ?? "新标签"; _path.SetResourceReference(TextBlock.ForegroundProperty, "NotebookMuted");
        _saveStatus.Text = _active.Conflict ? "保存冲突" : _active.Dirty ? "未保存" : ""; _saveStatus.SetResourceReference(TextBlock.ForegroundProperty, "NotebookMuted");
        _editButton.Content = new NotebookIcon { Kind = _active.Editing ? "book" : "edit" };
        _backButton.IsEnabled = ActiveReader.CanGoBack && !_active.Dirty; _forwardButton.IsEnabled = ActiveReader.CanGoForward && !_active.Dirty;
        _conflictBar.Visibility = _active.Conflict ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_disposed && _tabs.IndexOf(_active) is var active && active >= 0 && active < _tabStrip.Children.Count)
                    (_tabStrip.Children[active] as FrameworkElement)?.BringIntoView();
            }));
    }
    private static Border Separate(UIElement child, string color)
    {
        var border = new Border { Child = child, BorderThickness = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BackgroundProperty, color); border.SetResourceReference(Border.BorderBrushProperty, "NotebookBorder"); return border;
    }
    private static Button MakeButton(string text, string tooltip, RoutedEventHandler action)
    {
        var kind = text switch { "+" => "plus", "×" => "close", "←" => "back", "→" => "forward", "源码" => "edit", "查找" => "search", _ => null };
        var button = new Button { Content = kind is null ? text : new NotebookIcon { Kind = kind }, ToolTip = tooltip, MinWidth = 28, Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(StyleProperty, "NotebookIconButton"); button.Click += action; return button;
    }
    private async void OnWorkspaceKey(object sender, KeyEventArgs args)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var number = (int)args.Key - (int)Key.D0;
        if (!shift && number is >= 1 and <= 9)
        {
            args.Handled = true;
            SelectTabNumber(number);
            return;
        }
        switch (args.Key)
        {
            case Key.T: args.Handled = true; if (shift) ReopenClosedTab(); else NewTab(); break;
            case Key.W: args.Handled = true; await CloseActiveTabAsync(); break;
            case Key.Tab: args.Handled = true; NextTab(shift ? -1 : 1); break;
            case Key.E: args.Handled = true; ToggleEditing(); break;
            case Key.S: args.Handled = true; await SaveAllAsync(); break;
            case Key.F when !shift: args.Handled = true; Find(); break;
        }
    }
    private void Message(string text) => MessageChanged?.Invoke(this, text);
    private int _restoredActive;
    private void RestoreSession()
    {
        if (_catalog is null) return;
        try
        {
            var path = Path.Combine(_catalog.LocalStateDirectory, "workspace-tabs.json"); if (!File.Exists(path)) return;
            EnsureSafeSessionPath(path);
            var session = JsonSerializer.Deserialize<TabSession>(File.ReadAllText(path)); if (session?.Tabs is null) return;
            foreach (var state in session.Tabs.Take(50))
            {
                if (state is null) continue;
                var tab = AddTab(); if (_notes.FirstOrDefault(note => note.Id == state.NoteId) is { } note) LoadNote(tab, note, state.Offset);
            }
            _restoredActive = session.Active;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { Message("未能恢复上次标签；笔记文件保持不变。"); }
    }
    private void PersistSession()
    {
        if (_catalog is null || _restoring || _disposed) return;
        var temporary = "";
        try
        {
            var folder = _catalog.LocalStateDirectory; var path = Path.Combine(folder, "workspace-tabs.json");
            EnsureSafeSessionPath(path); Directory.CreateDirectory(folder); EnsureSafeSessionPath(path);
            temporary = Path.Combine(folder, ".workspace-tabs-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new TabSession(_tabs.Select(tab => new TabState(tab.Note?.Id, tab.Reader.VerticalOffset)).ToArray(), _tabs.IndexOf(_active))));
            EnsureSafeSessionPath(path);
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { Message("标签布局暂未保存；笔记内容不受影响。"); }
        finally { if (temporary.Length > 0 && File.Exists(temporary)) { try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } } }
    }
    private static void EnsureSafeSessionPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("标签状态路径含有文件或目录链接。");
    }
    public void Dispose()
    {
        if (_disposed) return; PersistSession(); _disposed = true;
        _autosave.Stop(); _autosave.Tick -= OnAutosave; foreach (var tab in _tabs) tab.Reader.Dispose();
    }
    private sealed class WorkspaceTab
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public NoteReader Reader { get; } = new(); public TextBox Editor { get; } = new(); public Grid Body { get; } = new();
        public StackPanel EmptyState { get; } = new();
        public LibraryNote? Note; public string Draft = ""; public bool Editing, Conflict;
        public bool Dirty => Note is not null && Draft != Note.Markdown;
    }
    private sealed record ClosedTab(LibraryNote Note, double Offset);
    private sealed record TabState(string? NoteId, double Offset);
    private sealed record TabSession(TabState[] Tabs, int Active);
}
