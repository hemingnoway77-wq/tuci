using System.Text.Json;
using VocabCollector.Core;
using VocabCollector.Core.Markdown;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public class NestedObsidianVaultTests
{
    [Fact]
    public async Task Scan_UsesNestedObsidianRootWithoutChangingStorageIdentity()
    {
        using var vault = new TempVault();
        Directory.CreateDirectory(Path.Combine(vault.Root, "英语单词", ".obsidian"));
        var target = vault.Note("英语单词/动词/introduce.md");
        var rootCard = vault.Note("英语单词/词根/duc.md");
        var index = await new VaultStore().ScanAsync(vault.Options, default);

        Assert.Equal(target.VaultRelativePath, Assert.Single(index.Notes).VaultRelativePath);
        Assert.Equal("动词/introduce.md", Assert.Single(index.Notes).ObsidianRelativePath);
        Assert.Equal(rootCard.VaultRelativePath, Assert.Single(index.RootNotes).VaultRelativePath);
        Assert.Equal("词根/duc.md", Assert.Single(index.RootNotes).ObsidianRelativePath);
        Assert.Equal("#### 例句\n1. They [[动词/introduce|introduce]] new ideas.\n",
            ManagedLinks.RefreshNew("#### 例句\n1. They introduce new ideas.\n", "learn", index));
        Assert.Equal("**词根**：[[词根/duc|duc / duct]]（引导）\n",
            ManagedLinks.RefreshNew("**词根**：duc / duct（引导）\n", "introduce", index));
    }

    [Fact]
    public async Task Save_KeepsOwnershipIdentityWhileReturningEffectiveObsidianTarget()
    {
        using var vault = new TempVault();
        Directory.CreateDirectory(Path.Combine(vault.Root, "英语单词", ".obsidian"));
        var saved = await new VaultStore().SaveNewAsync("learn", PartOfSpeech.Verb,
            _ => "#### 例句\n1. A teacher helps.\n", vault.Options, default);

        Assert.Equal("英语单词/动词/learn.md", saved.Note.VaultRelativePath);
        Assert.Equal("动词/learn.md", saved.Note.ObsidianRelativePath);
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json")));
        Assert.True(state.RootElement.GetProperty("Notes").TryGetProperty("英语单词/动词/learn.md", out _));
    }

    [Fact]
    public async Task Scan_ConfiguredObsidianRootRetainsExistingIdentityAndOptionalTargetIsNull()
    {
        using var vault = new TempVault();
        Directory.CreateDirectory(Path.Combine(vault.Root, ".obsidian"));
        var expected = vault.Note("英语单词/动词/introduce.md");

        Assert.Equal(expected, Assert.Single((await new VaultStore().ScanAsync(vault.Options, default)).Notes));
        Assert.Null(expected.ObsidianRelativePath);
    }

    [Fact]
    public async Task Refresh_ReadsFourRegionOwnershipWithoutAdoptingAnUnmatchedFormRegion()
    {
        using var vault = new TempVault();
        const string clean = "**词根**：duc（引导）\n\n#### 例句\n1. A teacher helps.\n\n#### 语义关系\n##### 近义词\n- study：学习。\n";
        var source = vault.Note("英语单词/learn.md", clean);
        await new LinkRefresher().RegisterAsync(vault.Options, source, clean, default);
        var ownershipPath = Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json");
        var state = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(ownershipPath))!;
        var regions = state["Notes"]![source.VaultRelativePath]!["Regions"]!.AsArray();
        Assert.Equal(3, regions.Count);
        regions.Add(new System.Text.Json.Nodes.JsonObject { ["Mode"] = "forms", ["BodyHash"] = new string('0', 64) });
        await File.WriteAllTextAsync(ownershipPath, state.ToJsonString());
        vault.Note("英语单词/teacher.md");

        Assert.Equal(new LinkRefreshResult(1, 1, 0), await new VaultStore().RefreshLinksAsync(vault.Options, default));
        Assert.Contains("[[英语单词/teacher|teacher]]", await File.ReadAllTextAsync(source.AbsolutePath));
        using var updated = JsonDocument.Parse(await File.ReadAllTextAsync(ownershipPath));
        Assert.Equal(4, updated.RootElement.GetProperty("Notes").GetProperty(source.VaultRelativePath).GetProperty("Regions").GetArrayLength());
    }

    [Fact]
    public async Task Refresh_ChangingObsidianRootInvalidatesCachedTargetsButPreservesOwnershipKeys()
    {
        using var vault = new TempVault();
        var store = new VaultStore();
        var source = await store.SaveNewAsync("learn", PartOfSpeech.Verb,
            _ => "#### 例句\n1. They introduce new ideas.\n", vault.Options, default);
        vault.Note("英语单词/动词/introduce.md");
        Assert.Equal(1, (await store.RefreshLinksAsync(vault.Options, default)).Updated);
        Assert.Contains("[[英语单词/动词/introduce|introduce]]", await File.ReadAllTextAsync(source.Note.AbsolutePath));
        var marker = Path.Combine(vault.Root, "英语单词", ".obsidian");
        Directory.CreateDirectory(marker);

        Assert.Equal(1, (await new VaultStore().RefreshLinksAsync(vault.Options, default)).Updated);
        Assert.Contains("[[动词/introduce|introduce]]", await File.ReadAllTextAsync(source.Note.AbsolutePath));
        var ownershipPath = Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json");
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(ownershipPath));
        Assert.True(state.RootElement.GetProperty("Notes").TryGetProperty(source.Note.VaultRelativePath, out _));
        Directory.Delete(marker);

        Assert.Equal(1, (await new VaultStore().RefreshLinksAsync(vault.Options, default)).Updated);
        Assert.Contains("[[英语单词/动词/introduce|introduce]]", await File.ReadAllTextAsync(source.Note.AbsolutePath));
    }
}
