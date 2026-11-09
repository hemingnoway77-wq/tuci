using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Core.Library;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Desktop.Learning;

public sealed class NoteReader : UserControl, IDisposable
{
    private readonly MarkdownPreview _preview = new() { Name = "ReadingPreview", Margin = new Thickness(0, 14, 0, 0) };
    private readonly NotebookNavigationButton _back = new() { Name = "ReaderBackButton", Kind = "back", Label = "后退", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _path = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Stack<ReadingPosition> _history = new();
    private readonly Stack<ReadingPosition> _forward = new();
    private readonly DockPanel _toolbar = new();
    private Popup? _hover;
    private LibraryCatalog? _catalog;
    private IReadOnlyList<LibraryNote> _notes = [];
    private Window? _card;
    private DispatcherOperation? _restoreOperation;
    private double? _restoreOffset;
    private bool _disposed;
    private string _lastFind = "";
    private int _nextFindAt;
    private readonly DispatcherTimer _positionTimer;
    private bool _restoringReadingPosition;

    public NoteReader()
    {
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(650) };
        _positionTimer.Tick += (_, _) =>
        {
            _positionTimer.Stop();
            if (!_disposed && !_restoringReadingPosition && Note is { } note)
            {
                var position = ReadingPositionTools.Capture(_preview);
                ReadingPositionChanged?.Invoke(this, new(note.Id, position.Progress, position.Anchor, note.Title));
            }
        };
        _preview.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, args) =>
        {
            if (args.VerticalChange == 0 || _disposed || !IsVisible || _restoreOffset is not null || _restoringReadingPosition) return;
            _positionTimer.Stop(); _positionTimer.Start();
        }));
        var toolbar = _toolbar;
        toolbar.Children.Add(_back);
        toolbar.Children.Add(_path);
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
        _preview.LinkHovered += OnLinkHovered;
        _preview.HoverEnded += (_, _) => CloseHover();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseHover(); };
    }

    public LibraryNote? Note { get; private set; }
    public bool CanGoBack => _history.Count > 0;
    public IReadOnlyList<LibraryHeading> Outline => HeadingParagraphs().Select((paragraph, index) => new LibraryHeading(
        MarkdownDocumentRenderer.GetHeadingLevel(paragraph), new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim(), index)).ToArray();
    private Paragraph[] HeadingParagraphs() => ReadingBlocks(_preview.Document.Blocks).OfType<Paragraph>()
        .Where(paragraph => MarkdownDocumentRenderer.GetHeadingLevel(paragraph) > 0).ToArray();
    internal int? HeadingSourceOffset(int index)
    {
        var headings = HeadingParagraphs();
        return index >= 0 && index < headings.Length ? MarkdownDocumentRenderer.GetHeadingSourceOffset(headings[index]) : null;
    }
    public bool DirectLinks
    {
        get => _preview.ImmediateLinks;
        set
        {
            _preview.ImmediateLinks = value;
            _toolbar.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            _preview.Margin = value ? new Thickness(0) : new Thickness(0, 14, 0, 0);
            _preview.Padding = value ? new Thickness(24, 12, 24, 20) : new Thickness(0);
            _preview.MaxWidth = value ? 900 : double.PositiveInfinity;
            if (value) ApplyReadingTypography();
        }
    }
    public double VerticalOffset => _restoreOffset ?? _preview.VerticalOffset;
    public bool CanGoForward => _forward.Count > 0;
    public void GoForward()
    {
        if (_disposed || !_forward.TryPop(out var next)) return;
        if (Note is not null) _history.Push(new(Note, VerticalOffset));
        Display(_notes.FirstOrDefault(n => n.Id == next.Note.Id) ?? next.Note, next.Offset);
        NavigationRequested?.Invoke(this, EventArgs.Empty);
    }
    public void NavigateHeading(int index)
    {
        if (_disposed || index < 0) return;
        var document = _preview.Document;
        var headings = HeadingParagraphs();
        if (index >= headings.Length) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_disposed && ReferenceEquals(document, _preview.Document)) headings[index].BringIntoView();
        }));
    }

    public bool FindText(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var text = new System.Text.StringBuilder();
        var points = new List<TextPointer>();
        for (var pointer = _preview.Document.ContentStart; pointer is not null && pointer.CompareTo(_preview.Document.ContentEnd) < 0;)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = pointer.GetTextInRun(LogicalDirection.Forward);
                for (var i = 0; i < run.Length; i++) { text.Append(run[i]); points.Add(pointer.GetPositionAtOffset(i)!); }
                pointer = pointer.GetPositionAtOffset(run.Length);
            }
            else pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        var content = text.ToString();
        var start = _lastFind == query ? Math.Min(_nextFindAt, content.Length) : 0;
        var match = content.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
        if (match < 0 && start > 0) match = content.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (match < 0) return false;
        _lastFind = query; _nextFindAt = match + query.Length;
        _preview.Selection.Select(points[match], points[match + query.Length - 1].GetPositionAtOffset(1)!);
        points[match].Paragraph?.BringIntoView();
        _preview.Focus();
        return true;
    }
    public event EventHandler<LibraryNote>? NewTabRequested;
    internal Func<LibraryNote, bool>? NavigationGuard { get; set; }
    public event EventHandler<LibraryNote>? NoteChanged;
    public event EventHandler<NoteReadingPosition>? ReadingPositionChanged;
    public ReadingPositionValue ReadingProgress => ReadingPositionTools.Capture(_preview);
    public void RestoreReadingPosition(double progress, string? anchor)
    {
        if (_disposed || Note is null || !double.IsFinite(progress)) return;
        var note = Note;
        _restoringReadingPosition = true; _positionTimer.Stop();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_disposed && Note?.Id == note.Id) ReadingPositionTools.Restore(_preview, progress, anchor);
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _restoringReadingPosition = false));
        }));
    }
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
        _forward.Clear();
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
        if (remember && NavigationGuard?.Invoke(note) == false) return;
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
                if (offset == 0) _preview.ScrollToHome();
                QueueRestore();
            }
            if (remember) NavigationRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (remember && Note is not null) _history.Push(new ReadingPosition(Note, _restoreOffset ?? _preview.VerticalOffset));
        if (remember) _forward.Clear();
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
        if (Note is not null) _forward.Push(new(Note, VerticalOffset));
        var note = _notes.FirstOrDefault(n => n.Id == previous.Note.Id) ?? previous.Note;
        Display(note, previous.Offset);
        NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void ForgetHistory(string noteId)
    {
        static void Remove(Stack<ReadingPosition> history, string id)
        {
            var retained = history.Where(position => position.Note.Id != id).Reverse().ToArray();
            history.Clear(); foreach (var position in retained) history.Push(position);
        }
        Remove(_history, noteId); Remove(_forward, noteId);
        _back.IsEnabled = CanGoBack;
    }
    internal void ForgetNote(string noteId)
    {
        ForgetHistory(noteId);
        if (Note?.Id != noteId) return;
        _positionTimer.Stop(); CancelRestore(); CloseCard();
        Note = null; _preview.Markdown = ""; _path.Text = "选择一篇笔记开始阅读";
        _lastFind = ""; _nextFindAt = 0; SetMessage("");
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
        if (matches.Count > 1) ShowCandidates(matches, DirectLinks || args.IsDoubleClick, placementTarget, DirectLinks && args.Modifiers.HasFlag(ModifierKeys.Control));
        else if (DirectLinks && args.Modifiers.HasFlag(ModifierKeys.Control)) NewTabRequested?.Invoke(this, matches[0]);
        else if (DirectLinks || args.IsDoubleClick) OpenNote(matches[0]);
        else ShowCard(matches[0], placementTarget);
    }

    private void OnPreviewLink(object? sender, PreviewLinkEventArgs args) => HandleLink(args, Note, this);

    private void Display(LibraryNote note, double offset)
    {
        _positionTimer.Stop();
        CloseCard();
        CancelRestore();
        Note = note;
        _path.Text = note.RelativePath;
        var sameMarkdown = _preview.Markdown == note.Markdown;
        _preview.Markdown = note.Markdown;
        if (offset == 0 && sameMarkdown) _preview.ScrollToHome();
        _lastFind = ""; _nextFindAt = 0;
        if (DirectLinks) ApplyReadingTypography();
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
            // A new document already queues ScrollToHome. Keep its zero intent
            // until that turn for immediate navigation/history, without forcing
            // another measure and redundant scroll command on every file switch.
            if (offset > 0)
            {
                _preview.UpdateLayout();
                _preview.ScrollToVerticalOffset(Math.Clamp(offset, 0, Math.Max(0, _preview.ExtentHeight - _preview.ViewportHeight)));
            }
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
        _positionTimer.Stop();
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

    private void ShowCandidates(IReadOnlyList<LibraryNote> notes, bool openFullNote, FrameworkElement? placementTarget, bool newTab = false)
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
            if (newTab) { CloseCard(); NewTabRequested?.Invoke(this, selected); }
            else if (openFullNote) OpenNote(selected); else ShowCard(selected, placementTarget);
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
        CloseHover();
        var window = _card;
        _card = null;
        window?.Close();
    }

    private void ApplyReadingTypography()
    {
        _preview.Document.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        _preview.Document.FontSize = 16;
        _preview.Document.LineHeight = 26;
        foreach (var paragraph in HeadingParagraphs())
        {
            paragraph.FontFamily = _preview.Document.FontFamily;
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        }
    }

    private static IEnumerable<Block> ReadingBlocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            if (block is Section section) foreach (var child in ReadingBlocks(section.Blocks)) yield return child;
            if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems) foreach (var child in ReadingBlocks(item.Blocks)) yield return child;
        }
    }

    private void OnLinkHovered(object? sender, PreviewLinkEventArgs args)
    {
        CloseHover();
        if (_catalog is null || (!args.IsWikiLink && Uri.TryCreate(args.Target, UriKind.Absolute, out _))) return;
        var matches = _catalog.Resolve(args.Target, Note, _notes);
        if (matches.Count != 1) return;
        var note = matches[0];
        var panel = new StackPanel { Margin = new Thickness(16), MaxWidth = 360 };
        panel.Children.Add(Text(note.Title, 19, "Ink"));
        if (note.Phonetics.Length > 0) panel.Children.Add(Text(note.Phonetics, 13, "Accent"));
        var summary = note.Meaning.Length > 0 ? note.Meaning : note.Excerpt;
        panel.Children.Add(Text(summary.Length > 360 ? summary[..360] + "…" : summary, 14, "InkSecondary"));
        var border = new Border { Child = panel, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        border.SetResourceReference(Border.BackgroundProperty, "Sheet");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        _hover = new Popup { Child = border, PlacementTarget = _preview, Placement = PlacementMode.MousePoint,
            HorizontalOffset = 12, VerticalOffset = 18, StaysOpen = true, AllowsTransparency = true,
            Focusable = false, IsHitTestVisible = false, IsOpen = true };
    }

    private void CloseHover()
    {
        if (_hover is null) return;
        _hover.IsOpen = false; _hover.Child = null; _hover = null;
    }

    public void Dispose()
    {
        _positionTimer.Stop();
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
