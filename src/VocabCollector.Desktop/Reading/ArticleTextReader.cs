using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace VocabCollector.Desktop.Reading;

public sealed record ReadingWordSelection(string Word, string Context)
{
    private readonly string _contextText = Context;
    private Lazy<string>? _deferredContext;
    public string Context
    {
        get => _deferredContext?.Value ?? _contextText;
        init { _contextText = value; _deferredContext = null; }
    }
    internal ReadingWordSelection(string word, Lazy<string> context) : this(word, "") => _deferredContext = context;
    public bool Equals(ReadingWordSelection? other) => ReferenceEquals(this, other) || other is not null && Word == other.Word && Context == other.Context;
    public override int GetHashCode() => HashCode.Combine(Word, Context);
}

public sealed record ArticleWord(string Text, int Start, string Context)
{
    private readonly string _contextText = Context;
    private Lazy<string>? _deferredContext;
    public string Context
    {
        get => _deferredContext?.Value ?? _contextText;
        init { _contextText = value; _deferredContext = null; }
    }
    internal ArticleWord(string text, int start, Lazy<string> context) : this(text, start, "") => _deferredContext = context;
    internal ReadingWordSelection CreateSelection() => _deferredContext is { } context ? new(Text, context) : new(Text, _contextText);
    public bool Equals(ArticleWord? other) => ReferenceEquals(this, other) || other is not null && Text == other.Text && Start == other.Start && Context == other.Context;
    public override int GetHashCode() => HashCode.Combine(Text, Start, Context);
}
public sealed class ReadingWordEventArgs(ReadingWordSelection selection, Rect bounds) : EventArgs
{
    public ReadingWordSelection Selection { get; } = selection;
    public Rect Bounds { get; } = bounds;
}

public static partial class ArticleWords
{
    [GeneratedRegex(@"[A-Za-z]+(?:[-'‘’][A-Za-z]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    public static IReadOnlyList<ArticleWord> Tokenize(string paragraph)
    {
        var result = new List<ArticleWord>();
        var sentenceStart = 0;
        var sentenceEnd = SentenceEnd(paragraph, 0);
        Lazy<string>? sharedContext = null;
        foreach (var match in Words().EnumerateMatches(paragraph))
        {
            if (match.Length > 64) continue;
            while (sentenceEnd <= match.Index)
            {
                sentenceStart = sentenceEnd;
                sentenceEnd = SentenceEnd(paragraph, sentenceStart);
                sharedContext = null;
            }
            Lazy<string> context;
            if (sentenceEnd - sentenceStart <= 2000)
                context = sharedContext ??= DeferredSlice(paragraph, sentenceStart, sentenceEnd);
            else
            {
                var start = Math.Clamp(match.Index - (2000 - match.Length) / 2, sentenceStart, sentenceEnd - 2000);
                context = DeferredSlice(paragraph, start, start + 2000);
            }
            result.Add(new(paragraph.Substring(match.Index, match.Length), match.Index, context));
        }
        return result;
    }

    private static int SentenceEnd(string text, int start)
    {
        while (start < text.Length && text[start] is not ('.' or '!' or '?')) start++;
        return start < text.Length ? start + 1 : text.Length;
    }

    private static Lazy<string> DeferredSlice(string text, int start, int end) =>
        new(() => text.AsSpan(start, end - start).Trim().ToString());
}

/// <summary>Plain, selectable article text. Only a stationary single click opens word actions.</summary>
public sealed class ArticleTextReader : RichTextBox
{
    private const double MaximumTextWidth = 950, ScrollbarGutter = 24;
    private ScrollViewer? _viewport;
    private Point? _down;
    private Span? _pressed, _active;
    private PlainWord? _pressedPlain;
    private TextRange? _activePlainRange;
    private readonly Dictionary<Paragraph, Lazy<IReadOnlyList<ArticleWord>>> _plainParagraphs = [];
    public event EventHandler<ReadingWordEventArgs>? WordClicked;

    public ArticleTextReader()
    {
        IsReadOnly = true; IsDocumentEnabled = true;
        BorderThickness = new Thickness(0); Padding = new Thickness(0); Background = Brushes.Transparent;
        FocusVisualStyle = null;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(ForegroundProperty, "Ink");
        SetResourceReference(SelectionBrushProperty, "Selection");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Unloaded += (_, _) => ClearWordSelection();
        PreviewKeyDown += OnSelectionKey;
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(WordDown), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(WordUp), true);
    }

    public override void OnApplyTemplate()
    {
        if (_viewport is not null) { _viewport.SizeChanged -= ViewportChanged; _viewport.ScrollChanged -= ViewportMetricsChanged; }
        base.OnApplyTemplate();
        _viewport = Template?.FindName("PART_ContentHost", this) as ScrollViewer;
        if (_viewport is not null)
        {
            AutoHideScrollbars.SetIsEnabled(_viewport, true);
            SmoothScroll.SetIsEnabled(_viewport, true);
            _viewport.SizeChanged += ViewportChanged;
            _viewport.ScrollChanged += ViewportMetricsChanged;
        }
        UpdateReadingPadding();
    }

    private void ViewportChanged(object sender, SizeChangedEventArgs args) => UpdateReadingPadding();
    private void ViewportMetricsChanged(object sender, ScrollChangedEventArgs args)
    { if (args.ViewportWidthChange != 0 && ReferenceEquals(args.OriginalSource, _viewport)) UpdateReadingPadding(); }
    private void UpdateReadingPadding()
    {
        var width = _viewport?.ViewportWidth ?? 0;
        if (!double.IsFinite(width) || width <= 0) return;
        var inset = Math.Max(0, (width - ScrollbarGutter - MaximumTextWidth) / 2);
        var padding = new Thickness(inset, 6, inset + ScrollbarGutter, 24);
        if (Document.PagePadding != padding) Document.PagePadding = padding;
    }

    public void SetParagraphs(IReadOnlyList<string> paragraphs)
    {
        if (_viewport is not null) SmoothScroll.Cancel(_viewport);
        ClearWordSelection();
        _plainParagraphs.Clear();
        // A long article must not allocate a Span, Run and event handlers for every
        // word before its first frame. Resolve words only when the user uses them.
        var plain = paragraphs.Sum(text => (long)text.Length) >= 6000;
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei"),
            FontSize = 17, LineHeight = 29, PagePadding = new Thickness(0, 6, ScrollbarGutter, 24),
            TextAlignment = TextAlignment.Left
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        foreach (var text in paragraphs)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 16) };
            if (plain)
            {
                paragraph.Inlines.Add(new Run(text));
                _plainParagraphs.Add(paragraph, new(() => ArticleWords.Tokenize(text)));
                document.Blocks.Add(paragraph);
                continue;
            }
            var offset = 0;
            foreach (var word in ArticleWords.Tokenize(text))
            {
                if (word.Start > offset) paragraph.Inlines.Add(new Run(text[offset..word.Start]));
                var span = new Span(new Run(word.Text))
                {
                    Tag = word.CreateSelection(), Cursor = Cursors.Hand
                };
                span.MouseEnter += (_, _) => { if (span != _active) span.SetResourceReference(TextElement.BackgroundProperty, "Hover"); };
                span.MouseLeave += (_, _) => { if (span != _active) span.ClearValue(TextElement.BackgroundProperty); };
                paragraph.Inlines.Add(span);
                offset = word.Start + word.Text.Length;
            }
            if (offset < text.Length) paragraph.Inlines.Add(new Run(text[offset..]));
            document.Blocks.Add(paragraph);
        }
        Document = document;
        UpdateReadingPadding();
        ScrollToHome();
    }

    private void WordDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _pressed = e.ClickCount == 1 ? FindWord(e.OriginalSource) : null;
        _pressedPlain = e.ClickCount == 1 && _pressed is null ? FindPlainWord(e.GetPosition(this)) : null;
        _down = e.GetPosition(this);
    }
    private void WordUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var pressed = _pressed; var plain = _pressedPlain; var down = _down;
        _pressed = null; _pressedPlain = null; _down = null;
        if (down is null || (e.GetPosition(this) - down.Value).Length > 4 || !Selection.IsEmpty) return;
        if (plain is not null)
        {
            var hit = FindPlainWord(e.GetPosition(this));
            if (hit is not null && hit.Paragraph == plain.Paragraph && hit.Word.Start == plain.Word.Start) ActivatePlainWord(hit);
            return;
        }
        var span = FindWord(e.OriginalSource);
        // RichTextBox captures the mouse while selecting; release may be routed to the reader itself.
        if (span is null && ReferenceEquals(e.OriginalSource, this))
            span = FindWord(GetPositionFromPoint(e.GetPosition(this), snapToText: false)?.Parent);
        if (span?.Tag is not ReadingWordSelection word || pressed != span) return;
        ClearWordSelection();
        _active = span;
        span.SetResourceReference(TextElement.BackgroundProperty, "AccentSoft");
        var bounds = span.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        WordClicked?.Invoke(this, new(word, bounds));
    }

    private static Span? FindWord(object? source)
    {
        for (var node = source as FrameworkContentElement; node is not null; node = node.Parent as FrameworkContentElement)
            if (node is Span { Tag: ReadingWordSelection } span) return span;
        return null;
    }

    public void ClearWordSelection()
    {
        _active?.ClearValue(TextElement.BackgroundProperty);
        _activePlainRange?.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
        _activePlainRange = null;
        _active = null; _pressed = null; _pressedPlain = null; _down = null;
    }

    private void OnSelectionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || Selection.IsEmpty) return;
        if (Selection.Start.Paragraph is { } paragraph && _plainParagraphs.TryGetValue(paragraph, out var tokens))
        {
            var start = new TextRange(paragraph.ContentStart, Selection.Start).Text.Length;
            var word = tokens.Value.FirstOrDefault(word => word.Start == start && word.Text == Selection.Text);
            if (word is not null)
            {
                ActivatePlainWord(new(paragraph, word, PositionAt(paragraph, start), PositionAt(paragraph, start + word.Text.Length)));
                e.Handled = true;
            }
            return;
        }
        var span = Document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Span>())
            .FirstOrDefault(item => item.Tag is ReadingWordSelection word && word.Word == Selection.Text.Trim()
                && Selection.Start.CompareTo(item.ContentStart) >= 0 && Selection.End.CompareTo(item.ContentEnd) <= 0);
        if (span?.Tag is not ReadingWordSelection selected) return;
        ClearWordSelection(); _active = span;
        span.SetResourceReference(TextElement.BackgroundProperty, "AccentSoft");
        WordClicked?.Invoke(this, new(selected, span.ContentStart.GetCharacterRect(LogicalDirection.Forward)));
        e.Handled = true;
    }

    private sealed record PlainWord(Paragraph Paragraph, ArticleWord Word, TextPointer Start, TextPointer End);

    private PlainWord? FindPlainWord(Point point)
    {
        var pointer = GetPositionFromPoint(point, snapToText: false);
        if (pointer?.Paragraph is not { } paragraph || !_plainParagraphs.TryGetValue(paragraph, out var tokens)) return null;
        var offset = new TextRange(paragraph.ContentStart, pointer).Text.Length;
        var word = tokens.Value.FirstOrDefault(word => word.Start <= offset && offset <= word.Start + word.Text.Length);
        if (word is null) return null;
        var start = PositionAt(paragraph, word.Start); var end = PositionAt(paragraph, word.Start + word.Text.Length);
        // Caret hit-testing snaps to character edges; reject adjacent whitespace.
        for (var position = start; position is not null && position.CompareTo(end) < 0; position = position.GetNextInsertionPosition(LogicalDirection.Forward))
        {
            var next = position.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next is null) break;
            var leading = position.GetCharacterRect(LogicalDirection.Forward);
            var trailing = next.GetCharacterRect(LogicalDirection.Backward);
            // WPF returns zero-width caret rectangles. Adjacent insertion edges
            // bound the glyph, including a word at the end of a wrapped line.
            if (Math.Abs(leading.Top - trailing.Top) > 1) continue;
            leading.Union(trailing);
            if (leading.Contains(point)) return new(paragraph, word, start, end);
        }
        return null;
    }

    private void ActivatePlainWord(PlainWord hit)
    {
        ClearWordSelection();
        _activePlainRange = new TextRange(hit.Start, hit.End);
        _activePlainRange.ApplyPropertyValue(TextElement.BackgroundProperty, TryFindResource("AccentSoft") as Brush ?? Brushes.LightGray);
        WordClicked?.Invoke(this, new(hit.Word.CreateSelection(), hit.Start.GetCharacterRect(LogicalDirection.Forward)));
    }

    private static TextPointer PositionAt(Paragraph paragraph, int offset)
    {
        for (var position = paragraph.ContentStart; position is not null && position.CompareTo(paragraph.ContentEnd) < 0;)
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var length = position.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= length) return position.GetPositionAtOffset(offset, LogicalDirection.Forward)!;
                offset -= length;
            }
            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }
        return paragraph.ContentEnd;
    }
}
