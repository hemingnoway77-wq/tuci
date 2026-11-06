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
    private Point? _down;
    private Span? _pressed, _active;
    public event EventHandler<ReadingWordEventArgs>? WordClicked;

    public ArticleTextReader()
    {
        IsReadOnly = true; IsDocumentEnabled = true;
        BorderThickness = new Thickness(0); Background = Brushes.Transparent;
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

    public void SetParagraphs(IReadOnlyList<string> paragraphs)
    {
        ClearWordSelection();
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei"),
            FontSize = 17, LineHeight = 29, PagePadding = new Thickness(0, 6, 12, 24),
            TextAlignment = TextAlignment.Left
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        foreach (var text in paragraphs)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 16) };
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
        ScrollToHome();
    }

    private void WordDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _pressed = e.ClickCount == 1 ? FindWord(e.OriginalSource) : null;
        _down = e.GetPosition(this);
    }
    private void WordUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var pressed = _pressed; var down = _down; _pressed = null; _down = null;
        var span = FindWord(e.OriginalSource);
        // RichTextBox captures the mouse while selecting; release may be routed to the reader itself.
        if (span is null && ReferenceEquals(e.OriginalSource, this))
            span = FindWord(GetPositionFromPoint(e.GetPosition(this), snapToText: false)?.Parent);
        if (span?.Tag is not ReadingWordSelection word || pressed != span || down is null
            || (e.GetPosition(this) - down.Value).Length > 4 || !Selection.IsEmpty) return;
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
        _active = null; _pressed = null; _down = null;
    }

    private void OnSelectionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || Selection.IsEmpty) return;
        var span = Document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Span>())
            .FirstOrDefault(item => item.Tag is ReadingWordSelection word && word.Word == Selection.Text.Trim()
                && Selection.Start.CompareTo(item.ContentStart) >= 0 && Selection.End.CompareTo(item.ContentEnd) <= 0);
        if (span?.Tag is not ReadingWordSelection selected) return;
        ClearWordSelection(); _active = span;
        span.SetResourceReference(TextElement.BackgroundProperty, "AccentSoft");
        WordClicked?.Invoke(this, new(selected, span.ContentStart.GetCharacterRect(LogicalDirection.Forward)));
        e.Handled = true;
    }
}
