using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VocabCollector.Core.Library;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Desktop.Learning;

public sealed class NoteReader : UserControl, IDisposable
{
    private readonly MarkdownPreview _preview = new() { Name = "ReadingPreview", Margin = new Thickness(0, 14, 0, 0) };
    private readonly Button _back = new() { Content = "← 返回", IsEnabled = false, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Stack<ReadingPosition> _history = new();
    private LibraryCatalog? _catalog;
    private IReadOnlyList<LibraryNote> _notes = [];
    private Window? _card;
    private DispatcherOperation? _restoreOperation;
    private double? _restoreOffset;
    private bool _disposed;

    public NoteReader()
    {
        var toolbar = new DockPanel();
        toolbar.Children.Add(_back);
        toolbar.Children.Add(_path);
        _back.SetResourceReference(StyleProperty, "QuietAction");
        _path.SetResourceReference(TextBlock.ForegroundProperty, "InkTertiary");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "InkSecondary");
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(toolbar);
        Grid.SetRow(_message, 1);
        layout.Children.Add(_message);
        Grid.SetRow(_preview, 2);
        layout.Children.Add(_preview);
        Content = layout;
        _back.Click += (_, _) => GoBack();
        _preview.LinkRequested += OnPreviewLink;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public LibraryNote? Note { get; private set; }
    public bool CanGoBack => _history.Count > 0;
    public event EventHandler<LibraryNote>? NoteChanged;
    public event EventHandler? NavigationRequested;
    public event EventHandler<string>? MessageChanged;
    public event EventHandler<string>? WebLinkRequested;
    public void Configure(LibraryCatalog catalog, IReadOnlyList<LibraryNote> notes)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(notes);
        if (ReferenceEquals(_catalog, catalog)) { RefreshNotes(notes); return; }
        CloseCard();
        CancelRestore();
        _catalog = catalog;
        _notes = notes.ToArray();
        _history.Clear();
        Note = null;
        _path.Text = "选择一篇笔记开始阅读";
        _preview.Markdown = "";
        _back.IsEnabled = false;
        SetMessage("");
    }

    public void OpenNote(LibraryNote note, bool remember = true, double? initialOffset = null)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(note);
        CloseCard();
        var offset = initialOffset is { } requested ? (double.IsFinite(requested) ? Math.Max(0, requested) : 0) : (double?)null;
        if (Note?.Id == note.Id)
        {
            if (Note != note) Display(note, offset ?? _restoreOffset ?? _preview.VerticalOffset);
            else if (offset is not null)
            {
                // A capture preview can preload the same note at a newer position.
                // Keep the requested offset pending so immediate navigation records it.
                _restoreOffset = offset;
                QueueRestore();
            }
            if (remember) NavigationRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (remember && Note is not null) _history.Push(new ReadingPosition(Note, _restoreOffset ?? _preview.VerticalOffset));
        Display(note, offset ?? 0);
        if (remember) NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshNotes(IReadOnlyList<LibraryNote> notes)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(notes);
        // Rescans also occur when focus returns from a card or article. Equal content,
        // including newly allocated records in another order, must not interrupt reading.
        var unchanged = _notes.Count == notes.Count && _notes.ToHashSet().SetEquals(notes);
        _notes = notes.ToArray();
        if (unchanged) return;
        CloseCard();
        if (Note is not null && _notes.FirstOrDefault(n => n.Id == Note.Id) is { } refreshed)
        {
            if (refreshed != Note) Display(refreshed, _restoreOffset ?? _preview.VerticalOffset);
        }
        else if (Note is not null)
            SetMessage("这篇笔记已不在词库中，当前显示已读取的内容。");
    }

    public void GoBack()
    {
        if (_disposed || !_history.TryPop(out var previous)) return;
        var note = _notes.FirstOrDefault(n => n.Id == previous.Note.Id) ?? previous.Note;
        Display(note, previous.Offset);
        NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    public void HandleLink(PreviewLinkEventArgs args, LibraryNote? source, FrameworkElement? placementTarget = null)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(args);
        var target = args.Target.Trim();
        if (!args.IsWikiLink && Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if ((uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !string.IsNullOrWhiteSpace(uri.Host))
            { CloseCard(); SetMessage(""); WebLinkRequested?.Invoke(this, target); }
            else SetMessage("无法打开此链接：仅支持 HTTP(S) 网页或词库内的笔记。");
            return;
        }
        if (_catalog is null) { SetMessage("请先打开词库。"); return; }
        var matches = _catalog.Resolve(target, source, _notes);
        if (matches.Count == 0) { CloseCard(); SetMessage("未找到笔记：" + target); return; }
        SetMessage("");
        if (matches.Count > 1) ShowCandidates(matches, args.IsDoubleClick, placementTarget);
        else if (args.IsDoubleClick) OpenNote(matches[0]);
        else ShowCard(matches[0], placementTarget);
    }

    private void OnPreviewLink(object? sender, PreviewLinkEventArgs args) => HandleLink(args, Note, this);

    private void Display(LibraryNote note, double offset)
    {
        CloseCard();
        CancelRestore();
        Note = note;
        _path.Text = note.RelativePath;
        _preview.Markdown = note.Markdown;
        _back.IsEnabled = CanGoBack;
        _restoreOffset = offset;
        QueueRestore();
        SetMessage("");
        NoteChanged?.Invoke(this, note);
    }

    private void QueueRestore()
    {
        if (_disposed || _restoreOffset is null || !IsLoaded || _restoreOperation is { Status: DispatcherOperationStatus.Pending }) return;
        _restoreOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _restoreOperation = null;
            if (_disposed || !IsLoaded || _restoreOffset is not { } offset) return;
            // The new FlowDocument must finish measure/arrange before its range is known.
            _preview.UpdateLayout();
            _preview.ScrollToVerticalOffset(Math.Clamp(offset, 0, Math.Max(0, _preview.ExtentHeight - _preview.ViewportHeight)));
            _restoreOffset = null;
        }));
    }

    private void CancelRestore()
    {
        _restoreOperation?.Abort();
        _restoreOperation = null;
        _restoreOffset = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => QueueRestore();
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CloseCard();
        _restoreOperation?.Abort();
        _restoreOperation = null;
    }

    private void ShowCard(LibraryNote note, FrameworkElement? placementTarget)
    {
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text(note.Title, 27, "Ink"));
        if (!string.IsNullOrWhiteSpace(note.Phonetics)) panel.Children.Add(Text(note.Phonetics, 15, "Accent"));
        var summary = string.IsNullOrWhiteSpace(note.Meaning) ? note.Excerpt : note.Meaning;
        panel.Children.Add(Text(summary.Length > 480 ? summary[..480] + "…" : summary, 15, "InkSecondary"));
        panel.Children.Add(Text(note.RelativePath, 12, "InkTertiary"));
        var open = new Button { Content = "打开整篇", Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        open.SetResourceReference(StyleProperty, "PrimaryAction");
        open.Click += (_, _) => OpenNote(note);
        panel.Children.Add(open);
        ShowWindow("词条简卡 · " + note.Title, new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, placementTarget);
    }

    private void ShowCandidates(IReadOnlyList<LibraryNote> notes, bool openFullNote, FrameworkElement? placementTarget)
    {
        var panel = new Grid { Margin = new Thickness(24) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(Text("找到同名笔记，请选择", 19, "Ink"));
        var list = new ListBox { Margin = new Thickness(0, 12, 0, 16), MinHeight = 120, SelectedIndex = 0 };
        foreach (var note in notes) list.Items.Add(new ListBoxItem { Content = note.RelativePath, Tag = note });
        Grid.SetRow(list, 1);
        panel.Children.Add(list);
        var open = new Button { Content = openFullNote ? "打开整篇" : "查看简卡", HorizontalAlignment = HorizontalAlignment.Left };
        open.SetResourceReference(StyleProperty, "PrimaryAction");
        void SelectNote()
        {
            if (list.SelectedItem is not ListBoxItem { Tag: LibraryNote selected }) return;
            if (openFullNote) OpenNote(selected); else ShowCard(selected, placementTarget);
        }
        open.Click += (_, _) => SelectNote();
        list.MouseDoubleClick += (_, args) =>
        {
            if (ItemsControl.ContainerFromElement(list, args.OriginalSource as DependencyObject) is ListBoxItem)
            { args.Handled = true; SelectNote(); }
        };
        list.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; SelectNote(); } };
        Grid.SetRow(open, 2);
        panel.Children.Add(open);
        ShowWindow("选择笔记", panel, placementTarget);
    }

    private static TextBlock Text(string text, double size, string color)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        block.SetResourceReference(TextBlock.ForegroundProperty, color);
        return block;
    }

    private void ShowWindow(string title, UIElement content, FrameworkElement? placementTarget)
    {
        CloseCard();
        var owner = Window.GetWindow(placementTarget ?? this);
        var window = new Window
        {
            Title = title, Content = content, Width = 460, Height = 340,
            MinWidth = 320, MinHeight = 240, ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        if (owner is not null)
        {
            window.Owner = owner;
            window.Resources.MergedDictionaries.Add(owner.Resources);
        }
        window.SetResourceReference(BackgroundProperty, "Sheet");
        window.SetResourceReference(ForegroundProperty, "Ink");
        window.PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) window.Close(); };
        window.Closed += (_, _) => { if (ReferenceEquals(_card, window)) _card = null; };
        _card = window;
        window.Show();
    }

    private void SetMessage(string text)
    {
        _message.Text = text;
        _message.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        MessageChanged?.Invoke(this, text);
    }

    private void CloseCard()
    {
        var window = _card;
        _card = null;
        window?.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseCard();
        CancelRestore();
        _preview.LinkRequested -= OnPreviewLink;
        _preview.Dispose();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    private sealed record ReadingPosition(LibraryNote Note, double Offset);
}
