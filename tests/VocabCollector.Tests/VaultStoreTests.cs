using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class VaultStoreTests
{
    [Fact] public async Task Scan_HandlesMissingDirectoryAndReservedNames()
    {
        using var vault = new TempVault(); var store = new VaultStore();
        Assert.Empty((await store.ScanAsync(vault.Options, default)).Find("word"));
        vault.Note("英语单词/名词/_con.md");
        Assert.Single((await store.ScanAsync(vault.Options with { ClassifyByPartOfSpeech = false }, default)).Find("con"));
    }
    [Fact] public async Task Save_ClassifiesOrSavesFlatAndNeverOverwrites()
    {
        using var vault = new TempVault(); var store = new VaultStore();
        var note = await store.SaveNewAsync("record", PartOfSpeech.Noun, "personal content", vault.Options, default);
        Assert.Equal("英语单词/名词/record.md", note.VaultRelativePath);
        await Assert.ThrowsAsync<DuplicateWordException>(() => store.SaveNewAsync("record", PartOfSpeech.Verb, "overwritten", vault.Options, default));
        Assert.Equal("personal content", await File.ReadAllTextAsync(note.AbsolutePath));
        var flat = await store.SaveNewAsync("word", PartOfSpeech.Noun, "flat", vault.Options with { ClassifyByPartOfSpeech = false }, default);
        Assert.Equal("英语单词/word.md", flat.VaultRelativePath);
    }
    [Fact] public async Task ConcurrentDifferentParts_CreateOnlyOneWord()
    {
        using var vault = new TempVault();
        async Task<bool> Save(PartOfSpeech pos)
        {
            try { await new VaultStore().SaveNewAsync("record", pos, pos.ToString(), vault.Options, default); return true; }
            catch (DuplicateWordException) { return false; }
        }
        var results = await Task.WhenAll(Save(PartOfSpeech.Noun), Save(PartOfSpeech.Verb));
        Assert.Single(results, x => x);
        Assert.Single(Directory.GetFiles(vault.Root, "record.md", SearchOption.AllDirectories));
    }
    [Fact] public async Task CancelledSaveLeavesNoNoteOrTemp()
    {
        using var vault = new TempVault(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VaultStore().SaveNewAsync("word", PartOfSpeech.Noun, "text", vault.Options, cts.Token));
        Assert.Empty(Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(vault.Root, "*.tmp", SearchOption.AllDirectories));
    }
    [Fact] public async Task DirectoryAtTargetCausesFailureWithoutPartialNote()
    {
        using var vault = new TempVault(); Directory.CreateDirectory(Path.Combine(vault.Root, "英语单词", "名词", "word.md"));
        await Assert.ThrowsAnyAsync<IOException>(() => new VaultStore().SaveNewAsync("word", PartOfSpeech.Noun, "text", vault.Options, default));
        Assert.Empty(Directory.GetFiles(vault.Root, "*.tmp", SearchOption.AllDirectories));
    }
}
