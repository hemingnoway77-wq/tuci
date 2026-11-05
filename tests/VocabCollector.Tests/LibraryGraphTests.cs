using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public class LibraryGraphTests
{
    [Fact]
    public async Task Links_RespectPathsAndDeduplicateWikiAndMarkdownLinks()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("名词/apple.md", "# apple\n[[../动词/record]] [another](../动词/record.md#usage) [[missing/apple]] [web](https://example.com/apple)");
        fixture.Write("动词/record.md", "[[名词/apple]] [[名词/apple|苹果]]");
        fixture.Write("名词/record.md", "unrelated namesake");
        var catalog = fixture.Catalog;
        var notes = (await catalog.ScanAsync()).Notes;
        var apple = Assert.Single(notes, n => n.Title == "apple");
        var verb = Assert.Single(notes, n => n.RelativePath == "动词/record.md");
        var graph = new LibraryGraph(catalog, notes);
        Assert.Equal(verb.Id, Assert.Single(graph.Outgoing(apple)).Id);
        Assert.Equal(apple.Id, Assert.Single(graph.Backlinks(verb)).Id);
        Assert.Empty(graph.Backlinks(Assert.Single(notes, n => n.RelativePath == "名词/record.md")));
    }

    [Fact]
    public async Task Links_IgnoreCodeFrontMatterImagesAndUnresolvedAmbiguities()
    {
        using var fixture = new LibraryFixture();
        fixture.Write("source.md", "---\nlink: '[[apple]]'\n---\n`[[apple]]`\n```md\n[[apple]]\n```\n    [[apple]]\n![image](apple.md)\n![[apple]]\n<!-- [[apple]] -->\n[[record]]");
        fixture.Write("apple.md", "# apple");
        fixture.Write("名词/record.md", "noun");
        fixture.Write("动词/record.md", "verb");
        var catalog = fixture.Catalog;
        var notes = (await catalog.ScanAsync()).Notes;
        var graph = new LibraryGraph(catalog, notes);
        Assert.Empty(graph.Outgoing(Assert.Single(notes, n => n.Title == "source")));
    }

    [Fact]
    public void Headings_IgnoreCodeAndFrontMatterAndKeepVisibleOutlineOrder()
    {
        var markdown = "---\ntitle: ignored\n---\n# Top *heading*\n```md\n## not a heading\n```\n  ## Child [label](apple.md) ##\nSetext title\n---\n    # code\n### Last `word`\n";
        Assert.Equal(new[] { new LibraryHeading(1, "Top heading", 0), new LibraryHeading(2, "Child label", 1), new LibraryHeading(2, "Setext title", 2), new LibraryHeading(3, "Last word", 3) }, LibraryGraph.Headings(markdown));
    }
}
