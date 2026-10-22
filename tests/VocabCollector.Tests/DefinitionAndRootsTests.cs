using VocabCollector.Core;
using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;

public class DefinitionAndRootsTests
{
    [Fact] public void DefinitionsUseOneQuotedLinePerMeaningLikeTheReference()
    {
        var analysis = TestData.Analysis with { Meanings = [new(PartOfSpeech.Verb, "介绍；把某人介绍给某人认识"), new(PartOfSpeech.Verb, "引进；推行；采用（新事物、政策等）")] };
        var text = new NoteRenderer().Render("introduced", "", analysis, [], DateTimeOffset.Now);
        Assert.Contains("**释义**：\n\n> v. 介绍；把某人介绍给某人认识  \n> v. 引进；推行；采用（新事物、政策等）", text);
        Assert.DoesNotContain("**释义**：v.", text);
    }

    [Fact] public void RootInformationIsPlacedBelowPronunciationAndAboveDefinitions()
    {
        var analysis = TestData.Analysis with { Roots = [new("duc", "引导", ["duct"])] };
        var text = new NoteRenderer().Render("introduced", "", analysis, [], DateTimeOffset.Now);
        Assert.Contains("**词根**：duc / duct（引导）", text);
        Assert.True(text.IndexOf("**发音**", StringComparison.Ordinal) < text.IndexOf("**词根**", StringComparison.Ordinal));
        Assert.True(text.IndexOf("**词根**", StringComparison.Ordinal) < text.IndexOf("**释义**", StringComparison.Ordinal));
        Assert.DoesNotContain("<!--", text);
        Assert.DoesNotContain("sha256=", text);
    }

    [Fact] public void UncertainEtymologyIsExplicitWithoutInventingARoot()
    {
        var text = new NoteRenderer().Render("word", "", TestData.Analysis with { Roots = [] }, [], DateTimeOffset.Now);
        Assert.Contains("**词根**：暂无可靠词根信息。", text);
    }

    [Fact] public void RootGroupLinksTheDedicatedRootCardRatherThanAnOrdinaryWord()
    {
        using var vault = new TempVault();
        var ordinary = vault.Note("英语单词/名词/duc.md");
        var root = vault.Note("英语单词/词根/duct.md");
        var analysis = TestData.Analysis with { Roots = [new("duc", "引导", ["duct"])] };
        var text = new NoteRenderer().Render("introduced", "", analysis, new VaultIndex([ordinary], [root]), DateTimeOffset.Now);
        Assert.Contains("**词根**：[[英语单词/词根/duct|duc / duct]]（引导）", text);
        Assert.DoesNotContain("[[英语单词/名词/duc", text);
    }

    [Fact] public async Task NewlyAddedRootCardBackfillsTheSavedWordWithoutAnotherModelCall()
    {
        using var vault = new TempVault(); var store = new VaultStore();
        var analyzer = new FakeWordAnalyzer { Analyze = _ => Task.FromResult(TestData.Analysis with { Roots = [new("duc", "引导", ["duct"])] }) };
        var service = new CaptureService(analyzer, store, TimeProvider.System);
        var result = await service.CaptureAsync(new("introduced", ""), new(new("http://localhost:1234/v1", "test-key", "model"), vault.Options), null, default);
        Assert.Equal(CaptureStatus.Saved, result.Status);
        var savedPath = Assert.Single(result.Notes).AbsolutePath;
        Assert.Contains("**词根**：duc / duct（引导）", await File.ReadAllTextAsync(savedPath));
        vault.Note("英语单词/词根/duc.md", "# duc\n引导\n");
        await store.RefreshLinksAsync(vault.Options, default);
        var linked = await File.ReadAllTextAsync(savedPath);
        Assert.Contains("**词根**：[[英语单词/词根/duc|duc / duct]]（引导）", linked);
        Assert.DoesNotContain("<!--", linked);
        Assert.Equal(1, analyzer.Calls);
    }
}
