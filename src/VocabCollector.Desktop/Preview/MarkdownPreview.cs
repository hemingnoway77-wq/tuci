using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace VocabCollector.Desktop.Preview;

/// <summary>A local, selectable reading surface. Source markup and metadata stay off the page.</summary>
public sealed class MarkdownPreview : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownPreview), new PropertyMetadata("", OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string RenderedText => new TextRange(Document.ContentStart, Document.ContentEnd).Text;
    public event EventHandler<PreviewLinkEventArgs>? LinkRequested;

    public MarkdownPreview()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsReadOnlyCaretVisible = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = Render("");
    }

    private static void OnMarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var preview = (MarkdownPreview)sender;
        preview.Document = preview.Render(args.NewValue as string ?? "");
        preview.ScrollToHome();
    }

    private FlowDocument Render(string markdown) => MarkdownDocumentRenderer.Render(markdown,
        link => LinkRequested?.Invoke(this, new PreviewLinkEventArgs(link.Target, link.IsWikiLink)));
}

public sealed class PreviewLinkEventArgs(string target, bool isWikiLink) : EventArgs
{
    public string Target { get; } = target;
    public bool IsWikiLink { get; } = isWikiLink;
}
