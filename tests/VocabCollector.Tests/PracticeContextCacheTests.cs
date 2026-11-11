using System.Text.Json;
using VocabCollector.Core.Library;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public sealed class PracticeContextCacheTests
{
    [Fact]
    public async Task CacheMissDoesNotWriteAnythingOrInventExampleSentences()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "account", "contexts.json");
        Assert.Empty(await new PracticeContextCache(path).ReadAsync("introduced", PracticeContextGenerationTests.Senses()));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task AllSensesSurviveRestartAndContentChangesMissWhileSenseOrderingDoesNot()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        var senses = PracticeContextGenerationTests.Senses(); var contexts = PracticeContextGenerationTests.Contexts();
        await new PracticeContextCache(path).SaveAsync(" Introduced ", senses, contexts);
        var cache = new PracticeContextCache(path);
        Assert.Equal(contexts, await cache.ReadAsync("introduced", senses));
        Assert.Equal(contexts, await cache.ReadAsync("introduced", senses.Reverse().ToArray()));
        var changed = senses.Select(sense => sense with { Meaning = sense.Meaning + "；人工新增义项内容" }).ToArray();
        Assert.Empty(await cache.ReadAsync("introduced", changed));
        Assert.Equal(contexts, await cache.ReadAsync("introduced", senses));
    }

    [Fact]
    public async Task DistinctAccountPathsAndConcurrentWordsCannotMixOrOverwriteEachOther()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "a", "contexts.json");
        var accountA = new PracticeContextCache(path); var accountB = new PracticeContextCache(Path.Combine(files.Root, "b", "contexts.json"));
        var senses = PracticeContextGenerationTests.Senses(); var contexts = PracticeContextGenerationTests.Contexts();
        var apple = new[] { new PracticeSense("apple-sense", "apple", null, "n. 苹果", "", "", 1, 1) };
        var appleContexts = new[] { new PracticeContext("apple-sense", "An apple fell from the tree yesterday.", "昨天一个苹果从树上掉了下来。") };
        await Task.WhenAll(accountA.SaveAsync("introduced", senses, contexts), accountA.SaveAsync("apple", apple, appleContexts));
        Assert.Equal(contexts, await accountA.ReadAsync("introduced", senses));
        Assert.Equal(appleContexts, await accountA.ReadAsync("apple", apple));
        Assert.Empty(await accountB.ReadAsync("introduced", senses));
        await accountB.SaveAsync("introduced", senses, contexts.Select(item => item with { Chinese = "另一个账户的自然中文译文。" }).ToArray());
        Assert.Equal(contexts, await accountA.ReadAsync("introduced", senses));
    }

    [Fact]
    public async Task ConcurrentInstancesAtEquivalentPathsDoNotLoseOtherWords()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        var alias = Path.Combine(files.Root, "not-created", "..", "contexts.json").ToUpperInvariant();
        await new PracticeContextCache(path).SaveAsync("introduced", PracticeContextGenerationTests.Senses(), PracticeContextGenerationTests.Contexts());
        var words = new[] { "apple", "banana", "book", "cat", "dog", "egg", "fish", "goat", "hat", "ice", "juice", "kite" };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = words.Select((word, index) => Task.Run(async () =>
        {
            await start.Task;
            var senses = new[] { new PracticeSense("sense-" + word, word, null, "n. 同时写入的词条", "", "", 1, 1) };
            var contexts = new[] { new PracticeContext("sense-" + word, "The teacher wrote " + word + " on the board today.", "老师今天在黑板上写下了这个单词。") };
            await new PracticeContextCache(index % 2 == 0 ? path : alias).SaveAsync(word, senses, contexts);
        })).ToArray();
        start.SetResult(); await Task.WhenAll(writes);
        foreach (var word in words)
        {
            var senses = new[] { new PracticeSense("sense-" + word, word, null, "n. 同时写入的词条", "", "", 1, 1) };
            Assert.Single(await new PracticeContextCache(path).ReadAsync(word, senses));
        }
        Assert.Equal(PracticeContextGenerationTests.Contexts(), await new PracticeContextCache(alias).ReadAsync("introduced", PracticeContextGenerationTests.Senses()));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("version")]
    [InlineData("invalid-context")]
    public async Task BrokenCacheIsReportedAndPreservedOnReadAndSave(string damage)
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        var cache = new PracticeContextCache(path); var senses = PracticeContextGenerationTests.Senses(); var contexts = PracticeContextGenerationTests.Contexts();
        await cache.SaveAsync("introduced", senses, contexts);
        var saved = await File.ReadAllTextAsync(path);
        var broken = damage switch { "malformed" => "{bad json", "version" => saved.Replace("\"FormatVersion\": 1", "\"FormatVersion\": 99"),
            _ => saved.Replace("teacher-sense", "unknown-sense") };
        await File.WriteAllTextAsync(path, broken);
        await Assert.ThrowsAnyAsync<IOException>(() => new PracticeContextCache(path).ReadAsync("introduced", senses));
        await Assert.ThrowsAnyAsync<IOException>(() => new PracticeContextCache(path).SaveAsync("introduced", senses, contexts));
        Assert.Equal(broken, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvalidGeneratedSetCannotReplaceThePreviouslyValidatedCache()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        var cache = new PracticeContextCache(path); var senses = PracticeContextGenerationTests.Senses(); var contexts = PracticeContextGenerationTests.Contexts();
        await cache.SaveAsync("introduced", senses, contexts); var saved = await File.ReadAllTextAsync(path);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => cache.SaveAsync("introduced", senses, contexts[..1]));
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        Assert.Equal(contexts, await cache.ReadAsync("introduced", senses));
    }

    [Fact]
    public async Task CancellationAndAtomicWriteFailureKeepThePreviousDataUsable()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        var cache = new PracticeContextCache(path); var senses = PracticeContextGenerationTests.Senses(); var contexts = PracticeContextGenerationTests.Contexts();
        await cache.SaveAsync("introduced", senses, contexts); var saved = await File.ReadAllTextAsync(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SaveAsync("introduced", senses, contexts, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ReadAsync("introduced", senses, cancellation.Token));
        Assert.Equal(saved, await File.ReadAllTextAsync(path));
        File.Delete(path); Directory.CreateDirectory(path);
        await Assert.ThrowsAnyAsync<IOException>(() => cache.SaveAsync("introduced", senses, contexts));
        Directory.Delete(path); await File.WriteAllTextAsync(path, saved);
        Assert.Equal(contexts, await cache.ReadAsync("introduced", senses));
        Assert.DoesNotContain(Directory.GetFiles(files.Root), file => file.EndsWith(".tmp"));
    }
}
