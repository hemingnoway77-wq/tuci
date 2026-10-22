using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class VaultPathsTests
{
    [Fact] public void ObsidianRoot_ChoosesNearestOrdinaryMarkerWithinConfiguredRoot()
    {
        using var vault = new TempVault();
        var options = vault.Options with { OutputSubfolder = "English/words" };
        var intermediate = Path.Combine(vault.Root, "English");
        var wordRoot = VaultPaths.WordRoot(options);
        Assert.Equal(vault.Root, VaultPaths.ObsidianRoot(options));
        Directory.CreateDirectory(Path.Combine(vault.Root, ".obsidian"));
        Assert.Equal(vault.Root, VaultPaths.ObsidianRoot(options));
        Directory.CreateDirectory(Path.Combine(intermediate, ".obsidian"));
        Assert.Equal(intermediate, VaultPaths.ObsidianRoot(options));
        Directory.CreateDirectory(Path.Combine(wordRoot, ".obsidian"));
        Assert.Equal(wordRoot, VaultPaths.ObsidianRoot(options));
    }
    [Fact] public void ObsidianRoot_DoesNotSearchAboveConfiguredRootOrCreateMarkers()
    {
        using var vault = new TempVault();
        Directory.CreateDirectory(Path.Combine(vault.Root, ".obsidian"));
        var configuredRoot = Path.Combine(vault.Root, "child");
        Directory.CreateDirectory(configuredRoot);
        var options = vault.Options with { RootPath = configuredRoot };

        Assert.Equal(configuredRoot, VaultPaths.ObsidianRoot(options));
        Assert.False(Directory.Exists(VaultPaths.WordRoot(options)));
        Assert.False(Directory.Exists(Path.Combine(configuredRoot, ".obsidian")));
    }
    [Fact] public void ObsidianRoot_IgnoresJunctionMarker()
    {
        using var vault = new TempVault(); using var outside = new TempVault();
        var wordRoot = VaultPaths.WordRoot(vault.Options);
        Directory.CreateDirectory(wordRoot);
        var marker = Path.Combine(wordRoot, ".obsidian");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", marker, outside.Root }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try { Assert.Equal(vault.Root, VaultPaths.ObsidianRoot(vault.Options)); }
        finally { Directory.Delete(marker); }
    }
    [Fact] public void IsLinkable_ChecksEffectiveObsidianTargetRatherThanStorageIdentity()
    {
        using var vault = new TempVault();
        var note = vault.Note("old#vault/teacher.md");
        Assert.False(VaultPaths.IsLinkable(note));
        Assert.True(VaultPaths.IsLinkable(note with { ObsidianRelativePath = "teacher.md" }));
        var ordinary = vault.Note("英语单词/teacher.md");
        Assert.False(VaultPaths.IsLinkable(ordinary with { ObsidianRelativePath = "bad#target/teacher.md" }));
        Assert.False(VaultPaths.IsLinkable(ordinary with { ObsidianRelativePath = "../teacher.md" }));
        Assert.Equal(ordinary.VaultRelativePath, VaultPaths.LinkTarget(ordinary));
    }
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
