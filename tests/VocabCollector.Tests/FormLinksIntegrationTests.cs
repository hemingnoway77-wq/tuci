using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;

public class FormLinksIntegrationTests
{
    [Fact]
    public async Task IntroduceAddedAfterIntroducedBackfillsItsFormInTheActualNestedVaultWithoutAi()
    {
        using var vault = new TempVault();
        Directory.CreateDirectory(Path.Combine(vault.Root, "英语单词", ".obsidian"));
        var store = new VaultStore();
        var analyzer = new FakeWordAnalyzer { Analyze = _ => Task.FromResult(TestData.Analysis with
        {
            PrimaryPartOfSpeech = PartOfSpeech.Verb,
            Meanings = [new(PartOfSpeech.Verb, "介绍")],
            Lemma = "introduce", FormNote = "introduced是introduce的过去式和过去分词",
            Roots = [new("duc", "引导", ["duct"])]
        }) };
        var service = new CaptureService(analyzer, store, TimeProvider.System);
        var captured = await service.CaptureAsync(new("introduced", ""), new(new("http://localhost:1234/v1", "test-key", "mock"), vault.Options), null, default);
        Assert.Equal(CaptureStatus.Saved, captured.Status);
        var note = Assert.Single(captured.Notes);
        var plain = await File.ReadAllTextAsync(note.AbsolutePath);
        Assert.Contains("**词形**：introduced是introduce的过去式和过去分词", plain);
        await File.AppendAllTextAsync(note.AbsolutePath, "我的例子 introduce 保持原样。\n");
        vault.Note("英语单词/动词/introduce.md", "# introduce\n我的原形卡片。\n");

        var refresh = await store.RefreshLinksAsync(vault.Options, default);
        Assert.Equal(1, refresh.Updated);
        var linked = await File.ReadAllTextAsync(note.AbsolutePath);
        Assert.Contains("**词形**：introduced是[[动词/introduce|introduce]]的过去式和过去分词", linked);
        Assert.EndsWith("我的例子 introduce 保持原样。\n", linked);
        Assert.DoesNotContain("[[英语单词/", linked);
        Assert.DoesNotContain("|introduced]]", linked);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(new LinkRefreshResult(0, 0, 0), await new VaultStore().RefreshLinksAsync(vault.Options, default));
        var ownership = await File.ReadAllTextAsync(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json"));
        Assert.Contains("forms", ownership);
        Assert.Contains("introduced.md", ownership);
    }
}
