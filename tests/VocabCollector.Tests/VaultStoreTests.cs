using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class VaultStoreTests
{
    [Fact] public async Task Save_RegistersCleanRegionsForLaterLinks()
    {
        using var vault = new TempVault(); var store = new VaultStore();
        var saved = await store.SaveNewAsync("learn", PartOfSpeech.Verb, _ => "#### 例句\n1. A teacher helps.\n", vault.Options, default);
        Assert.Empty(saved.Warning);
        Assert.DoesNotContain("vocabcollector", saved.Markdown);
        vault.Note("英语单词/teacher.md");

        Assert.Equal(1, (await store.RefreshLinksAsync(vault.Options, default)).Updated);
        Assert.Contains("[[英语单词/teacher|teacher]]", await File.ReadAllTextAsync(saved.Note.AbsolutePath));
    }

    [Fact] public async Task Save_MetadataFailureAfterCommitReturnsSavedNoteWithWarning()
    {
        using var vault = new TempVault();
        Directory.CreateDirectory(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json"));
        const string clean = "#### 例句\n1. A teacher helps.\n";

        var saved = await new VaultStore().SaveNewAsync("learn", PartOfSpeech.Verb, _ => clean, vault.Options, default);

        Assert.Equal(clean, await File.ReadAllTextAsync(saved.Note.AbsolutePath));
        Assert.Contains("已保存", saved.Warning);
        Assert.Contains("补链记录", saved.Warning);
    }

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
        var note = (await store.SaveNewAsync("record", PartOfSpeech.Noun, _ => "personal content", vault.Options, default)).Note;
        Assert.Equal("英语单词/名词/record.md", note.VaultRelativePath);
        await Assert.ThrowsAsync<DuplicateWordException>(() => store.SaveNewAsync("record", PartOfSpeech.Verb, _ => "overwritten", vault.Options, default));
        Assert.Equal("personal content", await File.ReadAllTextAsync(note.AbsolutePath));
        var flat = (await store.SaveNewAsync("word", PartOfSpeech.Noun, _ => "flat", vault.Options with { ClassifyByPartOfSpeech = false }, default)).Note;
        Assert.Equal("英语单词/word.md", flat.VaultRelativePath);
    }
    [Fact] public async Task ConcurrentDifferentParts_CreateOnlyOneWord()
    {
        using var vault = new TempVault();
        async Task<bool> Save(PartOfSpeech pos)
        {
            try { await new VaultStore().SaveNewAsync("record", pos, _ => pos.ToString(), vault.Options, default); return true; }
            catch (DuplicateWordException) { return false; }
        }
        var results = await Task.WhenAll(Save(PartOfSpeech.Noun), Save(PartOfSpeech.Verb));
        Assert.Single(results, x => x);
        Assert.Single(Directory.GetFiles(vault.Root, "record.md", SearchOption.AllDirectories));
    }
    [Fact] public async Task CancelledSaveLeavesNoNoteOrTemp()
    {
        using var vault = new TempVault(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VaultStore().SaveNewAsync("word", PartOfSpeech.Noun, _ => "text", vault.Options, cts.Token));
        Assert.Empty(Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(vault.Root, "*.tmp", SearchOption.AllDirectories));
    }
    [Fact] public async Task DirectoryAtTargetCausesFailureWithoutPartialNote()
    {
        using var vault = new TempVault(); Directory.CreateDirectory(Path.Combine(vault.Root, "英语单词", "名词", "word.md"));
        await Assert.ThrowsAnyAsync<IOException>(() => new VaultStore().SaveNewAsync("word", PartOfSpeech.Noun, _ => "text", vault.Options, default));
        Assert.Empty(Directory.GetFiles(vault.Root, "*.tmp", SearchOption.AllDirectories));
    }
}
