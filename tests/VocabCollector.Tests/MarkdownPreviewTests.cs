using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Documents;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Tests;

public sealed class MarkdownPreviewTests
{
    [Fact]
    public void VocabularyPreviewShowsReadingContentWithoutSourceOrMetadata() => OnSta(() =>
    {
        var markdown = "---\nword: introduced\ntags:\n  - vocabulary\n---\n# introduced\n\n<!-- vocabcollector:links:v1 secret -->\n**发音**：UK /ɪntrəˈdjuːst/\n\n**释义**：\n\n> v. 介绍  \n> v. 引进\n\n#### 例句\n1. Tom **introduced** his [[英语单词/名词/sister|sister]].\n   - 汤姆介绍了他的妹妹。\n";
        var document = MarkdownDocumentRenderer.Render(markdown);
        var text = Text(document);
        Assert.Contains("introduced", text);
        Assert.Contains("v. 介绍", text);
        Assert.Contains("v. 引进", text);
        Assert.Contains("his sister.", text);
        Assert.Contains("汤姆介绍了他的妹妹", text);
        Assert.DoesNotContain("word:", text);
        Assert.DoesNotContain("vocabulary", text);
        Assert.DoesNotContain("<!--", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("[[", text);
        Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
        Assert.Contains(Blocks(document), b => b is Section);
        Assert.Contains(Blocks(document), b => b is System.Windows.Documents.List);
    });

    [Fact]
    public void LinksRaiseTargetsWithoutNavigatingAndPreserveDisplayLabels() => OnSta(() =>
    {
        PreviewLink? clicked = null;
        var document = MarkdownDocumentRenderer.Render("[说明](https://example.com/docs) 与 [[词根/duc|duc / duct]]，[[名词/policy]]。", link => clicked = link);
        var links = Inlines(document).OfType<Hyperlink>().ToArray();
        Assert.Equal(3, links.Length);
        Assert.Contains("说明 与 duc / duct，policy。", Text(document));
        Assert.All(links, link => Assert.Null(link.NavigateUri));
        links[1].RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(new PreviewLink("词根/duc", true), clicked);
        links[0].RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(new PreviewLink("https://example.com/docs", false), clicked);
    });

    [Fact]
    public void CodeAndEscapedContentRemainLiteralAndNestedFormattingSurvives() => OnSta(() =>
    {
        var document = MarkdownDocumentRenderer.Render("Text **bold *emphasis*** and `[[literal]] **raw**`.\n\n```text\n# literal heading\n**literal bold**\n<script>literal code</script>\n```\n\n- one\n  - nested\n\n&amp; &#91;escaped&#93;");
        var text = Text(document);
        Assert.Contains("bold emphasis", text);
        Assert.Contains("[[literal]] **raw**", text);
        Assert.Contains("# literal heading", text);
        Assert.Contains("<script>literal code</script>", text);
        Assert.Contains("& [escaped]", text);
        Assert.Contains(Inlines(document), inline => inline is Bold);
        Assert.Contains(Inlines(document), inline => inline is Italic);
        Assert.Equal(2, Blocks(document).OfType<System.Windows.Documents.List>().Count());
    });

    [Fact]
    public void HtmlCommentsAndActiveHtmlAreNeverLoadedOrExposedAsProgramMarkup() => OnSta(() =>
    {
        var document = MarkdownDocumentRenderer.Render("<!-- hidden metadata -->\n\n<script>window.location='https://evil.invalid'</script>\n\n<img src='https://evil.invalid/pixel'>\n\nSafe **word**.\n\n![image label](https://evil.invalid/remote.png)");
        var text = Text(document);
        Assert.Contains("Safe word.", text);
        Assert.Contains("image label", text);
        Assert.DoesNotContain("hidden metadata", text);
        Assert.DoesNotContain("window.location", text);
        Assert.DoesNotContain("<img", text);
        Assert.DoesNotContain("evil.invalid", text);
        Assert.Empty(Inlines(document).OfType<InlineUIContainer>());
    });

    [Fact]
    public void PreviewControlUpdatesItsReadingSurfaceAndKeepsTextSelectable() => OnSta(() =>
    {
        var preview = new MarkdownPreview { Markdown = "# introduced\n\n**释义**：介绍。" };
        Assert.True(preview.IsReadOnly);
        Assert.True(preview.IsDocumentEnabled);
        Assert.Contains("释义：介绍。", preview.RenderedText);
        preview.SelectAll();
        Assert.Contains("introduced", preview.Selection.Text);
        Assert.DoesNotContain("#", preview.Selection.Text);

        PreviewLinkEventArgs? requested = null;
        preview.LinkRequested += (_, args) => requested = args;
        preview.Markdown = "[[词根/duc|duc / duct]]";
        Assert.DoesNotContain("introduced", preview.RenderedText);
        Inlines(preview.Document).OfType<Hyperlink>().Single().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.NotNull(requested);
        Assert.Equal("词根/duc", requested.Target);
        Assert.True(requested.IsWikiLink);
    });

    [Theory]
    [InlineData("[see [[foo]]](https://example.com)", "see foo")]
    [InlineData("[see **[[词根/duc|duc / duct]]**](https://example.com)", "see duc / duct")]
    [InlineData("[see *[[foo|the word]]* and `[[literal]]`](https://example.com)", "see the word and [[literal]]")]
    public void WikiLinksInsideMarkdownLinkLabelsRemainTextWithoutNestedHyperlinks(string markdown, string expectedLabel) => OnSta(() =>
    {
        PreviewLink? clicked = null;
        var document = MarkdownDocumentRenderer.Render(markdown, link => clicked = link);
        Assert.Contains(expectedLabel, Text(document));
        var onlyLink = Assert.Single(Inlines(document).OfType<Hyperlink>());
        Assert.DoesNotContain(Inlines(onlyLink.Inlines), inline => inline is Hyperlink);
        onlyLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Assert.Equal(new PreviewLink("https://example.com", false), clicked);
    });

    internal static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF rendering timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static string Text(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;
    private static IEnumerable<Block> Blocks(FlowDocument document) => Blocks(document.Blocks);
    private static IEnumerable<Block> Blocks(BlockCollection collection)
    {
        foreach (var block in collection)
        {
            yield return block;
            if (block is Section section) foreach (var child in Blocks(section.Blocks)) yield return child;
            if (block is System.Windows.Documents.List list) foreach (var item in list.ListItems) foreach (var child in Blocks(item.Blocks)) yield return child;
        }
    }
    private static IEnumerable<Inline> Inlines(FlowDocument document)
    {
        foreach (var block in Blocks(document).OfType<Paragraph>()) foreach (var inline in Inlines(block.Inlines)) yield return inline;
    }
    private static IEnumerable<Inline> Inlines(InlineCollection collection)
    {
        foreach (var inline in collection)
        {
            yield return inline;
            if (inline is Span span) foreach (var child in Inlines(span.Inlines)) yield return child;
        }
    }
}
