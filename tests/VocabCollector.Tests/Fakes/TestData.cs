using VocabCollector.Core;
namespace VocabCollector.Tests.Fakes;
public static class TestData
{
    public static WordAnalysis Analysis => new(true, "", "/rɪˈzɪliənt/", "", PartOfSpeech.Adjective,
        [new(PartOfSpeech.Adjective, "有韧性的；能迅速恢复的")],
        [new("She remained resilient.", "她依然坚韧。"), new("The economy is resilient.", "经济具有韧性。")],
        [new("a resilient economy", "韧性强的经济体"), new("resilient communities", "具有韧性的社区")],
        [new("resilience", RelationKind.SameRoot)]);
}
public sealed class TempVault : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "VocabCollectorTests", Guid.NewGuid().ToString("N"));
    public VaultOptions Options => new(Root, "英语单词", true);
    public TempVault() => Directory.CreateDirectory(Root);
    public NoteLocation Note(string relative, string contents = "historical personal notes")
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return new(Path.GetFileNameWithoutExtension(path), path, relative);
    }
    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
