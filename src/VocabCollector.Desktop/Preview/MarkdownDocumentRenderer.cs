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
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;

namespace VocabCollector.Desktop.Preview;

public static class MarkdownDocumentRenderer
{
    private static readonly MarkdownPipeline Pipeline = CreatePipeline();
    private static readonly Brush Ink = Brush("#49333F");
    private static readonly Brush Rose = Brush("#BF4775");
    private static readonly Brush Muted = Brush("#85717C");
    private static readonly Brush PalePink = Brush("#FFF0F5");
    private static readonly Brush Rule = Brush("#F2CFDE");

    public static FlowDocument Render(string markdown, Action<PreviewLink>? onLink = null)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 14.5,
            Foreground = Ink,
            LineHeight = 25,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            ColumnWidth = double.PositiveInfinity
        };
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
                paragraph.FontSize = heading.Level switch { 1 => 30, 2 => 23, 3 => 20, 4 => 18, _ => 15 };
                paragraph.FontWeight = FontWeights.SemiBold;
                paragraph.Foreground = heading.Level == 1 ? Ink : Rose;
                paragraph.Margin = heading.Level == 1 ? new Thickness(0, 0, 0, 17) : new Thickness(0, 16, 0, 8);
                return paragraph;
            }
            case ParagraphBlock paragraph:
                return Paragraph(paragraph.Inline, onLink);
            case QuoteBlock quote:
            {
                var section = new Section
                {
                    BorderBrush = Rule,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(15, 5, 5, 5),
                    Margin = new Thickness(0, 0, 0, 18)
                };
                AddChildren(section.Blocks, quote, onLink);
                foreach (var child in section.Blocks) child.Margin = new Thickness(0);
                return section;
            }
            case ListBlock list:
            {
                var rendered = new WpfList
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    MarkerOffset = 6,
                    Padding = new Thickness(24, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 12)
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) rendered.StartIndex = start;
                foreach (var child in list.OfType<ListItemBlock>())
                {
                    var item = new ListItem { Margin = new Thickness(0, 0, 0, 5) };
                    AddChildren(item.Blocks, child, onLink);
                    foreach (var itemBlock in item.Blocks) itemBlock.Margin = new Thickness(0, 0, 0, 3);
                    rendered.ListItems.Add(item);
                }
                return rendered;
            }
            case ThematicBreakBlock:
                return new Paragraph
                {
                    BorderBrush = Rule,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 0, 0, 18),
                    LineHeight = 1,
                    FontSize = 1
                };
            case CodeBlock code:
                return new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    FontSize = 12.5,
                    Background = PalePink,
                    Padding = new Thickness(13, 9, 13, 9),
                    Margin = new Thickness(0, 0, 0, 16)
                };
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

    private static Paragraph Paragraph(ContainerInline? source, Action<PreviewLink>? onLink)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 16) };
        AddInlines(paragraph.Inlines, source, onLink);
        return paragraph;
    }

    private static void AddChildren(BlockCollection target, ContainerBlock source, Action<PreviewLink>? onLink)
    {
        foreach (var child in source)
            if (RenderBlock(child, onLink) is { } rendered) target.Add(rendered);
    }

    private static void AddInlines(InlineCollection target, ContainerInline? source, Action<PreviewLink>? onLink, bool allowLinks = true)
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
                    target.Add(lineBreak.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 13, Background = PalePink });
                    break;
                case WikiLinkInline wiki:
                    target.Add(allowLinks ? Link(new PreviewLink(wiki.Target, true), new Run(wiki.Label), onLink) : new Run(wiki.Label));
                    break;
                case LinkInline { IsImage: true } image:
                    // Display alt text only. Preview must never fetch a remote resource.
                    var alternate = new Span { Foreground = Muted };
                    AddInlines(alternate.Inlines, image, onLink, allowLinks: false);
                    target.Add(alternate);
                    break;
                case LinkInline link:
                    var label = new Span();
                    // WPF forbids Hyperlink descendants even through formatting spans.
                    AddInlines(label.Inlines, link, onLink, allowLinks: false);
                    target.Add(allowLinks ? Link(new PreviewLink(link.Url ?? "", false), label, onLink) : label);
                    break;
                case AutolinkInline link:
                    target.Add(allowLinks ? Link(new PreviewLink(link.Url, false), new Run(link.Url), onLink) : new Run(link.Url));
                    break;
                case EmphasisInline emphasis:
                    Span formatting = emphasis.DelimiterCount >= 2 ? new Bold { Foreground = Rose } : new Italic();
                    AddInlines(formatting.Inlines, emphasis, onLink, allowLinks);
                    target.Add(formatting);
                    break;
                case HtmlInline:
                    break;
                case ContainerInline container:
                    AddInlines(target, container, onLink, allowLinks);
                    break;
            }
        }
    }

    private static Hyperlink Link(PreviewLink target, WpfInline label, Action<PreviewLink>? onLink)
    {
        var link = new Hyperlink(label)
        {
            Foreground = Rose,
            TextDecorations = null,
            Cursor = Cursors.Hand,
            ToolTip = target.Target,
            Tag = target
        };
        link.Click += (_, args) => { args.Handled = true; onLink?.Invoke(target); };
        return link;
    }

    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
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
