using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class VaultPathsTests
{
    [Theory][InlineData("../escape")][InlineData("C:words")][InlineData("C:\\words")][InlineData("\\words")]
    [InlineData("英语#词库")][InlineData("notes[old]")][InlineData("notes^")]
    public void Root_RejectsUnsafeSubfolder(string subfolder)
    {
        using var vault = new TempVault();
        Assert.Throws<ArgumentException>(() => VaultPaths.WordRoot(vault.Options with { OutputSubfolder = subfolder }));
    }
    [Fact] public void Root_AcceptsChineseAndSpaces()
    {
        using var vault = new TempVault();
        Assert.Equal(Path.Combine(vault.Root, "English 学习", "words"), VaultPaths.WordRoot(vault.Options with { OutputSubfolder = "English 学习/words" }));
        Assert.Throws<ArgumentException>(() => VaultPaths.WordRoot(vault.Options with { RootPath = Path.Combine(vault.Root, "absent") }));
    }
    [Fact] public void Root_RejectsJunctionToOutside()
    {
        using var vault = new TempVault(); using var other = new TempVault();
        var link = Path.Combine(vault.Root, "jump");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(other.Root);
        using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<ArgumentException>(() => VaultPaths.WordRoot(vault.Options with { OutputSubfolder = "jump/words" })); }
        finally { Directory.Delete(link); }
    }
    [Fact] public void EveryPartOfSpeechHasFixedDirectory() => Assert.Equal(11, Enum.GetValues<PartOfSpeech>().Select(VaultPaths.PartFolder).Distinct().Count());
}
