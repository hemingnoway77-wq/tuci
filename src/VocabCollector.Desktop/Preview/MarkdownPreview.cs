using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VocabCollector.Desktop.Preview;

/// <summary>A local, selectable reading surface. Source markup and metadata stay off the page.</summary>
public sealed class MarkdownPreview : RichTextBox, IDisposable
{
    private readonly DispatcherTimer _singleClickTimer;
    private Hyperlink? _pressedLink;
    private Hyperlink? _pendingLink;
    private Hyperlink? _hoverLink;
    private Point _pressedAt;
    private bool _doubleClick;
    private bool _disposed;
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownPreview), new PropertyMetadata("", OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string RenderedText => new TextRange(Document.ContentStart, Document.ContentEnd).Text;
    public bool ImmediateLinks { get; set; }
    public event EventHandler<PreviewLinkEventArgs>? LinkRequested;
    public event EventHandler<PreviewLinkEventArgs>? LinkHovered;
    public event EventHandler? HoverEnded;

    public MarkdownPreview()
    {
        _singleClickTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime()) };
        _singleClickTimer.Tick += OnSingleClick;
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnLinkMouseDown), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnLinkMouseUp), true);
        Unloaded += (_, _) => { CancelPendingLink(); EndHover(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) EndHover(); };
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsReadOnlyCaretVisible = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = System.Windows.Media.Brushes.Transparent;
        SetResourceReference(SelectionBrushProperty, "Selection");
        SetResourceReference(CaretBrushProperty, "Accent");
        SelectionOpacity = 0.5;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = Render("");
    }

    private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var preview = (MarkdownPreview)sender;
        SmoothScroll.Cancel(preview);
        preview.CancelPendingLink();
        preview.EndHover();
        preview.Document = preview.Render(args.NewValue as string ?? "");
        preview.ScrollToHome();
    }

    private FlowDocument Render(string markdown) => MarkdownDocumentRenderer.Render(markdown,
        link => { EndHover(); if (!_disposed) LinkRequested?.Invoke(this, new PreviewLinkEventArgs(link.Target, link.IsWikiLink, modifiers: Keyboard.Modifiers)); });

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        UpdateHover(args.OriginalSource);
    }

    private void UpdateHover(object? source)
    {
        var link = !_disposed ? FindLink(source) : null;
        if (link is not null)
        {
            DependencyObject? parent = link;
            while (parent is FrameworkContentElement content && parent is not FlowDocument) parent = content.Parent;
            if (!ReferenceEquals(parent, Document)) link = null;
        }
        if (ReferenceEquals(link, _hoverLink)) return;
        EndHover();
        _hoverLink = link;
        if (link?.Tag is PreviewLink target)
            LinkHovered?.Invoke(this, new PreviewLinkEventArgs(target.Target, target.IsWikiLink, modifiers: Keyboard.Modifiers));
    }


    protected override void OnMouseLeave(MouseEventArgs args) { base.OnMouseLeave(args); EndHover(); }

    private void EndHover()
    {
        if (_hoverLink is null) return;
        _hoverLink = null;
        HoverEnded?.Invoke(this, EventArgs.Empty);
    }

    private void OnLinkMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (_disposed || args.ChangedButton != MouseButton.Left || FindLink(args.OriginalSource) is not { } link) return;
        _pressedLink = link;
        EndHover();
        _pressedAt = args.GetPosition(this);
        _doubleClick = args.ClickCount == 2 && ReferenceEquals(link, _pendingLink);
        // Suppress Hyperlink's immediate Click. Keyboard/automation Click remains immediate.
        args.Handled = true;
        if (_doubleClick) _singleClickTimer.Stop();
    }

    private void OnLinkMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left || _pressedLink is not { } pressed) return;
        _pressedLink = null;
        args.Handled = true;
        var position = args.GetPosition(this);
        if (!ReferenceEquals(pressed, FindLink(args.OriginalSource)) ||
            Math.Abs(position.X - _pressedAt.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(position.Y - _pressedAt.Y) > SystemParameters.MinimumVerticalDragDistance)
        { _doubleClick = false; return; }
        if (ImmediateLinks)
        {
            CancelPendingLink();
            RaiseLink(pressed, false);
        }
        else if (_doubleClick)
        {
            CancelPendingLink();
            RaiseLink(pressed, true);
        }
        else
        {
            _singleClickTimer.Stop();
            _pendingLink = pressed;
            _singleClickTimer.Start();
        }
    }

    private static Hyperlink? FindLink(object? source)
    {
        for (var node = source as DependencyObject; node is not null;)
        {
            if (node is Hyperlink link) return link;
            node = node is FrameworkContentElement content ? content.Parent :
                node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void OnSingleClick(object? sender, EventArgs args)
    {
        var link = _pendingLink;
        CancelPendingLink();
        if (link is not null) RaiseLink(link, false);
    }

    private void RaiseLink(Hyperlink link, bool isDoubleClick)
    {
        EndHover();
        if (!_disposed && link.Tag is PreviewLink target)
            LinkRequested?.Invoke(this, new PreviewLinkEventArgs(target.Target, target.IsWikiLink, isDoubleClick, Keyboard.Modifiers));
    }

    private void CancelPendingLink()
    {
        _singleClickTimer.Stop();
        _pendingLink = null;
        _pressedLink = null;
        _doubleClick = false;
    }

    public void Dispose()
    {
        SmoothScroll.Cancel(this);
        if (_disposed) return;
        _disposed = true;
        CancelPendingLink();
        EndHover();
        _singleClickTimer.Tick -= OnSingleClick;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();
}

public sealed class PreviewLinkEventArgs(string target, bool isWikiLink, bool isDoubleClick = false, ModifierKeys modifiers = ModifierKeys.None) : EventArgs
{
    public string Target { get; } = target;
    public bool IsWikiLink { get; } = isWikiLink;
    public bool IsDoubleClick { get; } = isDoubleClick;
    public ModifierKeys Modifiers { get; } = modifiers;
}
