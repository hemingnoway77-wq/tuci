using VocabCollector.Core;
using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;

public class CleanNoteOutputTests
{
    [Fact] public void RenderedNoteContainsOnlyLearningContentAndOrdinaryMarkdown()
    {
        var text = new NoteRenderer().Render("resilient", "", TestData.Analysis, [], DateTimeOffset.Now);
        Assert.DoesNotContain("<!--", text);
        Assert.DoesNotContain("vocabcollector:", text);
        Assert.DoesNotContain("sha256=", text);
        Assert.Contains("#### 例句", text);
        Assert.Contains("#### 语义关系", text);
        Assert.Contains("#### 我的笔记", text);
    }

    [Fact] public async Task SuccessfulSaveStillShowsItsTrackingWarning()
    {
        using var vault = new TempVault();
        var service = new CaptureService(new FakeWordAnalyzer(), new WarningStore(vault), TimeProvider.System);
        var settings = new AppSettings(new("http://localhost:1234/v1", "test-key", "model"), vault.Options);
        var result = await service.CaptureAsync(new("resilient", ""), settings, null, default);
        Assert.Equal(CaptureStatus.Saved, result.Status);
        Assert.Contains("补链记录未能保存", result.Message);
        Assert.True(File.Exists(Assert.Single(result.Notes).AbsolutePath));
    }

    private sealed class WarningStore(TempVault vault) : IVocabularyStore
    {
        public Task<VaultIndex> ScanAsync(VaultOptions options, CancellationToken ct) => Task.FromResult(new VaultIndex([]));
        public Task<SavedNote> SaveNewAsync(string word, PartOfSpeech pos, Func<VaultIndex, string> render, VaultOptions options, CancellationToken ct)
        {
            var markdown = render(new([]));
            var note = vault.Note("英语单词/resilient.md", markdown);
            return Task.FromResult(new SavedNote(note, markdown) { Warning = "笔记已保存，但补链记录未能保存。" });
        }
    }
}
