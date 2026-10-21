using VocabCollector.Core;
using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;

public class BeginnerTemplateTests
{
    [Fact] public void Renderer_UsesTheLearnersExampleStructure()
    {
        var text = new NoteRenderer().Render("resilient", "", TestData.Analysis, [], DateTimeOffset.Now);
        Assert.Contains("# resilient\n---", text);
        Assert.Contains("**发音**", text);
        Assert.Contains("**释义**", text);
        Assert.Contains("#### 例句", text);
        Assert.Contains("#### 常见搭配", text);
        Assert.Contains("##### 近义词", text);
        Assert.Contains("##### 反义词", text);
        Assert.Contains("##### 句法模式", text);
    }

    [Fact] public void SemanticLearningContentRemainsVisibleWithoutExistingCards()
    {
        var analysis = TestData.Analysis with { Related = [new("launch", RelationKind.Synonym)] };
        var text = new NoteRenderer().Render("introduce", "", analysis, [], DateTimeOffset.Now);
        Assert.Contains("- launch", text);
        Assert.DoesNotContain("[[", text);
        Assert.Contains("反义词", text);
    }

    [Fact] public void ExampleWordsLinkToActualExistingCards()
    {
        using var vault = new TempVault(); var policy = vault.Note("英语单词/名词/policy.md");
        var analysis = TestData.Analysis with { Examples = [new("The company introduced a new policy.", "公司推出了一项新政策。"), new("A policy can help a company.", "政策可以帮助公司。")], Related = [] };
        var text = new NoteRenderer().Render("introduced", "", analysis, [new(RelationKind.Confusable, policy)], DateTimeOffset.Now);
        Assert.Contains("The company **introduced** a new [[英语单词/名词/policy|policy]].", text);
    }

    [Fact] public async Task SavingAFutureCardBackfillsAnOldExampleWithoutAnotherAiCall()
    {
        using var vault = new TempVault(); var analyzer = new FakeWordAnalyzer
        {
            Analyze = _ => Task.FromResult(TestData.Analysis with { Examples = [new("The company introduced a new policy.", "公司推出了一项新政策。"), new("Our policy helps people.", "我们的政策帮助人们。")], Related = [] })
        };
        var store = new VaultStore(); var service = new CaptureService(analyzer, store, TimeProvider.System);
        var settings = new AppSettings(new("http://localhost:1234/v1", "test", "model"), vault.Options);
        var first = await service.CaptureAsync(new("introduced", ""), settings, null, default);
        Assert.Equal(CaptureStatus.Saved, first.Status);
        var oldPath = Assert.Single(first.Notes).AbsolutePath;
        await File.AppendAllTextAsync(oldPath, "\nMy personal addition.\n");
        var next = await service.CaptureAsync(new("policy", ""), settings, null, default);
        Assert.Equal(CaptureStatus.Saved, next.Status);
        var text = await File.ReadAllTextAsync(oldPath);
        Assert.Contains("[[英语单词/形容词/policy|policy]]", text);
        Assert.Contains("My personal addition.", text);
        Assert.Equal(2, analyzer.Calls);
    }

    [Fact] public async Task ExplicitRefreshDoesNotModifyUnmanagedHistoricalNotes()
    {
        using var vault = new TempVault(); var note = vault.Note("英语单词/动词/introduced.md", "# introduced\n1. A new policy.\nMy own layout.");
        vault.Note("英语单词/名词/policy.md");
        var maintenance = Assert.IsAssignableFrom<ILinkMaintenance>(new VaultStore());
        await maintenance.RefreshLinksAsync(vault.Options, default);
        Assert.Equal("# introduced\n1. A new policy.\nMy own layout.", await File.ReadAllTextAsync(note.AbsolutePath));
    }

    [Fact] public void ApostrophesStayInsideWholeWordLinkMatching()
    {
        using var vault = new TempVault(); var term = vault.Note("英语单词/don't.md"); var fragment = vault.Note("英语单词/don.md");
        var analysis = TestData.Analysis with { Examples = [new("I don't think so.", "我不这么认为。"), new("We can learn.", "我们可以学习。")], Related = [] };
        var text = new NoteRenderer().Render("learn", "", analysis, new VaultIndex([term, fragment]), DateTimeOffset.Now);
        Assert.Contains("I [[英语单词/don't|don't]] think so.", text);
        Assert.DoesNotContain("[[英语单词/don|don]]", text);
    }
}
