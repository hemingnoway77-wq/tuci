using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class VaultIndexTests
{
    [Fact] public void ResolveRelated_DropsMissingAmbiguousSelfAndRemovedTargets()
    {
        using var vault = new TempVault();
        var a = vault.Note("英语单词/名词/record.md"); var b = vault.Note("英语单词/动词/record.md");
        var good = vault.Note("英语单词/名词/resilience.md"); var removed = vault.Note("英语单词/名词/removed.md"); File.Delete(removed.AbsolutePath);
        var unsafeNote = vault.Note("英语单词/old#folder/unsafe.md");
        var index = new VaultIndex([a,b,good,removed,unsafeNote]);
        Assert.Equal(2, index.Find("Record").Count);
        var results = index.ResolveRelated("resilient", [new("record", RelationKind.Synonym), new("missing", RelationKind.Synonym), new("resilient", RelationKind.Synonym), new("removed", RelationKind.Synonym), new("unsafe", RelationKind.Synonym), new("resilience", RelationKind.SameRoot)]);
        Assert.Equal(good, Assert.Single(results).Note);
    }
    [Fact] public void ResolveRelated_CapsAndDeduplicates()
    {
        using var vault = new TempVault();
        string[] words = ["one","two","three","four","five","six","seven","eight","nine","ten","eleven","twelve","thirteen"];
        var index = new VaultIndex(words.Select(w => vault.Note("英语单词/" + w + ".md")).ToArray());
        Assert.Equal(8, index.ResolveRelated("test", words.Select(w => new RelatedCandidate(w, RelationKind.Synonym)).ToArray()).Count);
        Assert.Single(index.ResolveRelated("test", [new("one", RelationKind.Synonym), new("ONE", RelationKind.Antonym)]));
    }
}
