using VocabCollector.Core.Library;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public sealed class PracticeBankImportTests
{
    [Fact]
    public async Task ImportFillsMissingAndStaleBanksWithoutTouchingLearningHistory()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "practice-contexts.json");
        var senses = PracticeContextGenerationTests.Senses(); var contexts = PracticeContextGenerationTests.Contexts();
        var cache = new PracticeContextCache(path);
        var practice = Path.Combine(files.Root, "practice.json"); var roaming = Path.Combine(files.Root, "roaming.json");
        await File.WriteAllTextAsync(practice, "practice-history-canary"); await File.WriteAllTextAsync(roaming, "roaming-history-canary");
        await cache.ImportMissingAsync("introduced", senses, contexts);
        Assert.Equal(contexts, await cache.ReadAsync("introduced", senses));
        var changed = senses.Select(sense => sense with { Meaning = sense.Meaning + "；更新后的义项" }).ToArray();
        Assert.Empty(await cache.ReadAsync("introduced", changed));
        var replacements = contexts.Select(context => context with { Chinese = "这是更新后义项对应的自然中文译文。" }).ToArray();
        await cache.ImportMissingAsync("introduced", changed, replacements);
        Assert.Equal(replacements, await cache.ReadAsync("introduced", changed));
        Assert.Equal("practice-history-canary", await File.ReadAllTextAsync(practice));
        Assert.Equal("roaming-history-canary", await File.ReadAllTextAsync(roaming));
    }

    [Fact]
    public async Task ACompleteLocalBankWinsAndItsExactOriginalBytesArePreserved()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "practice-contexts.json");
        var cache = new PracticeContextCache(path); var senses = PracticeContextGenerationTests.Senses();
        var local = PracticeContextGenerationTests.Contexts();
        await cache.SaveAsync("introduced", senses, local);
        await File.AppendAllTextAsync(path, "\n  \n"); var original = await File.ReadAllBytesAsync(path);
        var cloud = local.Select(context => context with { Chinese = "云端的另一条有效译文。" }).ToArray();
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await new PracticeContextCache(path).ImportMissingAsync("introduced", senses.Reverse().ToArray(), cloud);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(local, await cache.ReadAsync("introduced", senses));
    }

    [Fact]
    public async Task ImportStillValidatesTheWholeIncomingSetAndPreservesGoodDataOnFailure()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "practice-contexts.json");
        var cache = new PracticeContextCache(path); var senses = PracticeContextGenerationTests.Senses(); var contexts = PracticeContextGenerationTests.Contexts();
        await cache.SaveAsync("introduced", senses, contexts); var original = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.ImportMissingAsync("introduced", senses, contexts[..1]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.ImportMissingAsync("introduced", senses,
            contexts.Select(context => context with { SenseId = "unrelated-sense" }).ToArray()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ImportMissingAsync("introduced", senses, contexts, cancelled.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ConcurrentLocalSaveAndCloudImportThroughDifferentInstancesAlwaysKeepTheLocalChoice()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "practice-contexts.json");
        var local = PracticeContextGenerationTests.Contexts();
        var cloud = local.Select(context => context with { Chinese = "云端的另一条有效译文。" }).ToArray();
        await Task.WhenAll(new PracticeContextCache(path).ImportMissingAsync("introduced", PracticeContextGenerationTests.Senses(), cloud),
            new PracticeContextCache(Path.Combine(files.Root, ".", "practice-contexts.json")).SaveAsync("introduced", PracticeContextGenerationTests.Senses(), local));
        Assert.Equal(local, await new PracticeContextCache(path).ReadAsync("introduced", PracticeContextGenerationTests.Senses()));
    }
}
