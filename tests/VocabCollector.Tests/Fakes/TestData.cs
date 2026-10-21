using VocabCollector.Core;
namespace VocabCollector.Tests.Fakes;
public static class TestData
{
    public static WordAnalysis Analysis => new(true, "", "/rɪˈzɪliənt/", "", PartOfSpeech.Adjective,
        [new(PartOfSpeech.Adjective, "有韧性的；能迅速恢复的")],
        [new("She remained resilient after a very difficult year.", "经历了非常艰难的一年后，她依然坚韧。"), new("The local economy is resilient in difficult times.", "当地经济在困难时期仍有韧性。")],
        [new("a resilient economy", "韧性强的经济体"), new("resilient communities", "具有韧性的社区"), new("resilient materials", "有回弹性的材料")],
        [new("resilience", RelationKind.SameRoot, "对应的名词，表示恢复力或韧性。")])
        {
            Lemma = "resilient",
            FormNote = "resilient 是形容词原形。",
            Grammar = [new("be + resilient", "表示某人或某物有韧性、能够恢复。"), new("resilient + 名词", "放在名词前，描述有恢复能力的人或事物。")]
        };
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
