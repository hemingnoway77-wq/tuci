using VocabCollector.Core;
using VocabCollector.Desktop;
namespace VocabCollector.Tests;

public sealed class PreviewLinkResolverTests : IDisposable
{
    private readonly string vault = Path.Combine(Path.GetTempPath(), "preview-links-" + Guid.NewGuid().ToString("N"));
    public PreviewLinkResolverTests() => Directory.CreateDirectory(Path.Combine(vault, "单词", "词根"));
    [Fact]
    public void ResolvesExistingRootAndWordLinksWithoutCreatingFiles()
    {
        var rootPath = Path.Combine(vault, "单词", "词根", "duc.md");
        File.WriteAllText(rootPath, "# duc");
        var options = new VaultOptions(vault, "单词", true);
        Assert.Equal(rootPath, PreviewLinkResolver.Resolve("单词/词根/duc#词源", options, null)?.AbsolutePath);
        Assert.Null(PreviewLinkResolver.Resolve("单词/词根/missing", options, null));
        Assert.Single(Directory.GetFiles(vault, "*.md", SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData("../other.md")][InlineData("C:/secret.md")][InlineData("file:///secret.md")]
    [InlineData("https://example.com/a.md")][InlineData("单词/%2e%2e/other.md")]
    [InlineData("单词/tool.exe")][InlineData("单词/词根/../duc.md")]
    public void RejectsPathsThatCanEscapeTheVocabularyOrExecuteAFile(string target)
    {
        Assert.Null(PreviewLinkResolver.Resolve(target, new(vault, "单词", true), null));
    }
    [Fact]
    public void ResolvesPathsRelativeToTheActualNestedObsidianVault()
    {
        Directory.CreateDirectory(Path.Combine(vault, "单词", ".obsidian"));
        Directory.CreateDirectory(Path.Combine(vault, "单词", "动词"));
        var path = Path.Combine(vault, "单词", "动词", "introduce.md");
        File.WriteAllText(path, "# introduce");
        var options = new VaultOptions(vault, "单词", true);
        Assert.Equal(path, PreviewLinkResolver.Resolve("动词/introduce", options, null)?.AbsolutePath);
        Assert.Equal(path, PreviewLinkResolver.Resolve("单词/动词/introduce", options, null)?.AbsolutePath);
        Assert.Null(PreviewLinkResolver.Resolve("动词/missing", options, null));
    }
    public void Dispose() => Directory.Delete(vault, true);
}
