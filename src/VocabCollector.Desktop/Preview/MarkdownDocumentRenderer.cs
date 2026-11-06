using System.Net;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;

namespace VocabCollector.Desktop.Preview;

public static class MarkdownDocumentRenderer
{
    public static readonly DependencyProperty HeadingLevelProperty = DependencyProperty.RegisterAttached(
        "HeadingLevel", typeof(int), typeof(MarkdownDocumentRenderer), new PropertyMetadata(0));
    public static readonly DependencyProperty HeadingSourceOffsetProperty = DependencyProperty.RegisterAttached(
        "HeadingSourceOffset", typeof(int), typeof(MarkdownDocumentRenderer), new PropertyMetadata(-1));
    public static int GetHeadingLevel(DependencyObject element) => (int)element.GetValue(HeadingLevelProperty);
    public static void SetHeadingLevel(DependencyObject element, int value) => element.SetValue(HeadingLevelProperty, value);
    public static int GetHeadingSourceOffset(DependencyObject element) => (int)element.GetValue(HeadingSourceOffsetProperty);
    public static void SetHeadingSourceOffset(DependencyObject element, int value) => element.SetValue(HeadingSourceOffsetProperty, value);
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();
    private static readonly ResourceDictionary MonoFallback = ThemePalette.Create(AppTheme.Mono);
    private const string ContentFont = "Segoe UI, Microsoft YaHei UI, Microsoft YaHei";
    private const string CodeFont = "Cascadia Mono, Consolas, Microsoft YaHei UI, Microsoft YaHei";

    public static FlowDocument Render(string markdown, Action<PreviewLink>? onLink = null)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily(ContentFont),
            FontSize = 16,
            LineHeight = 26,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            ColumnWidth = double.PositiveInfinity
        };
        ThemeBrush(document, FlowDocument.ForegroundProperty, "Ink");
        foreach (var block in Markdig.Markdown.Parse(markdown ?? "", Pipeline))
            if (RenderBlock(block, onLink) is { } rendered) document.Blocks.Add(rendered);
        return document;
    }

    private static MarkdownPipeline CreatePipeline()
    {
        var builder = new MarkdownPipelineBuilder().UseYamlFrontMatter();
        builder.InlineParsers.Insert(0, new WikiLinkParser());
        return builder.Build();
    }

    private static WpfBlock? RenderBlock(MdBlock block, Action<PreviewLink>? onLink)
    {
        switch (block)
        {
            // Metadata and arbitrary HTML are never part of the reading surface.
            case YamlFrontMatterBlock:
            case HtmlBlock:
                return null;
            case HeadingBlock heading:
            {
                var paragraph = Paragraph(heading.Inline, onLink);
                SetHeadingLevel(paragraph, heading.Level);
                SetHeadingSourceOffset(paragraph, heading.Span.Start);
                paragraph.FontFamily = new FontFamily(ContentFont);
                paragraph.FontSize = heading.Level switch { 1 => 26, 2 => 22, 3 => 19, 4 => 18, _ => 16 };
                paragraph.FontWeight = FontWeights.SemiBold;
                ThemeBrush(paragraph, TextElement.ForegroundProperty, "Ink");
                paragraph.Margin = heading.Level == 1 ? new Thickness(0, 0, 0, 8) : new Thickness(0, 12, 0, 6);
                return paragraph;
            }
            case ParagraphBlock paragraph:
                return Paragraph(paragraph.Inline, onLink);
            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(14, 2, 4, 2),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                ThemeBrush(section, WpfBlock.BorderBrushProperty, "Line");
                foreach (var child in quote)
                {
                    var rendered = child is ParagraphBlock quotedParagraph ? Paragraph(quotedParagraph.Inline, onLink, preserveSoftBreaks: true) : RenderBlock(child, onLink);
                    if (rendered is not null) section.Blocks.Add(rendered);
                }
                foreach (var child in section.Blocks) child.Margin = new Thickness(0, 0, 0, 3);
                return section;
            }
            case ListBlock list:
            {
                var rendered = new WpfList
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    MarkerOffset = 6,
                    Padding = new Thickness(22, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) rendered.StartIndex = start;
                foreach (var child in list.OfType<ListItemBlock>())
                {
                    var item = new ListItem { Margin = new Thickness(0, 0, 0, 8) };
                    AddChildren(item.Blocks, child, onLink);
                    foreach (var itemBlock in item.Blocks) itemBlock.Margin = new Thickness(0, 0, 0, 3);
                    rendered.ListItems.Add(item);
                }
                return rendered;
            }
            case ThematicBreakBlock:
                return ThemeBrush(new Paragraph
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 4, 0, 18),
                    LineHeight = 1,
                    FontSize = 1
                }, WpfBlock.BorderBrushProperty, "LineSoft");
            case CodeBlock code:
                return ThemeBrush(new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = new FontFamily(CodeFont),
                    FontSize = 13,
                    Padding = new Thickness(13, 9, 13, 9),
                    Margin = new Thickness(0, 0, 0, 16)
                }, TextElement.BackgroundProperty, "AccentSoft");
            case ContainerBlock container:
            {
                var section = new Section();
                AddChildren(section.Blocks, container, onLink);
                return section;
            }
            default:
                return null;
        }
    }

    private static Paragraph Paragraph(ContainerInline? source, Action<PreviewLink>? onLink, bool preserveSoftBreaks = false)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        AddInlines(paragraph.Inlines, source, onLink, preserveSoftBreaks: preserveSoftBreaks);
        return paragraph;
    }

    private static void AddChildren(BlockCollection target, ContainerBlock source, Action<PreviewLink>? onLink)
    {
        foreach (var child in source)
            if (RenderBlock(child, onLink) is { } rendered) target.Add(rendered);
    }

    private static void AddInlines(InlineCollection target, ContainerInline? source, Action<PreviewLink>? onLink, bool allowLinks = true, bool preserveSoftBreaks = false)
    {
        if (source is null) return;
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard || preserveSoftBreaks ? new LineBreak() : new Run(" "));
                    break;
                case CodeInline code:
                    target.Add(ThemeBrush(new Run(code.Content) { FontFamily = new FontFamily(CodeFont), FontSize = 13 }, TextElement.BackgroundProperty, "AccentSoft"));
                    break;
                case WikiLinkInline wiki:
                    target.Add(allowLinks ? Link(new PreviewLink(wiki.Target, true), new Run(wiki.Label), onLink) : new Run(wiki.Label));
                    break;
                case LinkInline { IsImage: true } image:
                    // Display alt text only. Preview must never fetch a remote resource.
                    var alternate = ThemeBrush(new Span(), TextElement.ForegroundProperty, "InkTertiary");
                    AddInlines(alternate.Inlines, image, onLink, allowLinks: false, preserveSoftBreaks);
                    target.Add(alternate);
                    break;
                case LinkInline link:
                    var label = new Span();
                    // WPF forbids Hyperlink descendants even through formatting spans.
                    AddInlines(label.Inlines, link, onLink, allowLinks: false, preserveSoftBreaks);
                    target.Add(allowLinks ? Link(new PreviewLink(link.Url ?? "", false), label, onLink) : label);
                    break;
                case AutolinkInline link:
                    target.Add(allowLinks ? Link(new PreviewLink(link.Url, false), new Run(link.Url), onLink) : new Run(link.Url));
                    break;
                case EmphasisInline emphasis:
                    Span formatting = emphasis.DelimiterCount >= 2 ? ThemeBrush(new Bold(), TextElement.ForegroundProperty, "Ink") : new Italic();
                    AddInlines(formatting.Inlines, emphasis, onLink, allowLinks, preserveSoftBreaks);
                    target.Add(formatting);
                    break;
                case HtmlInline:
                    break;
                case ContainerInline container:
                    AddInlines(target, container, onLink, allowLinks, preserveSoftBreaks);
                    break;
            }
        }
    }

    private static Hyperlink Link(PreviewLink target, WpfInline label, Action<PreviewLink>? onLink)
    {
        var link = new Hyperlink(label)
        {
            TextDecorations = TextDecorations.Underline,
            Cursor = Cursors.Hand,
            Tag = target
        };
        ThemeBrush(link, TextElement.ForegroundProperty, "Accent");
        link.Click += (_, args) => { args.Handled = true; onLink?.Invoke(target); };
        return link;
    }

    private static T ThemeBrush<T>(T element, DependencyProperty property, string key) where T : FrameworkContentElement
    {
        if (element.TryFindResource(key) is Brush)
        {
            element.SetResourceReference(property, key);
            return element;
        }

        // Standalone renderers also work without an Application. Bind once hosted, so
        // an early-created document joins the live palette without rebuilding its text.
        element.SetValue(property, MonoFallback[key]);
        RoutedEventHandler? bindWhenLoaded = null;
        bindWhenLoaded = (_, _) =>
        {
            element.Loaded -= bindWhenLoaded;
            if (element.TryFindResource(key) is Brush) element.SetResourceReference(property, key);
        };
        element.Loaded += bindWhenLoaded;
        return element;
    }

    private sealed class WikiLinkInline(string target, string label) : LeafInline
    {
        public string Target { get; } = target;
        public string Label { get; } = label;
    }

    private sealed class WikiLinkParser : InlineParser
    {
        public WikiLinkParser() => OpeningCharacters = ['['];

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            if (slice.CurrentChar != '[' || slice.PeekChar(1) != '[') return false;
            var end = slice.Text.IndexOf("]]", slice.Start + 2, StringComparison.Ordinal);
            if (end < 0 || end + 1 > slice.End) return false;
            var contents = slice.Text.Substring(slice.Start + 2, end - slice.Start - 2);
            if (contents.IndexOfAny(['\r', '\n', '[', ']']) >= 0) return false;
            var separator = contents.IndexOf('|');
            var target = (separator < 0 ? contents : contents[..separator]).Trim();
            if (target.Length == 0) return false;
            var label = separator < 0 ? target.Split('#')[0].Split('/', '\\').Last() : contents[(separator + 1)..];
            if (separator < 0 && label.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) label = label[..^3];
            processor.Inline = new WikiLinkInline(WebUtility.HtmlDecode(target), WebUtility.HtmlDecode(label));
            slice.Start = end + 2;
            return true;
        }
    }
}

public sealed record PreviewLink(string Target, bool IsWikiLink);
