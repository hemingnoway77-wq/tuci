using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Library;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public sealed class PracticeContextPreparationTests
{
    private static readonly ApiOptions Options = new("https://example.test/v1", "test-only-key", "test-model");
    private static PracticeSense[] Senses() => PracticeContextGenerationTests.Senses();
    private static PracticeContext[] Contexts() => PracticeContextGenerationTests.Contexts();

    [Fact]
    public async Task CacheHitReturnsAllSensesWithoutGenerationEvenWithInvalidApiSettings()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        await new PracticeContextCache(path).SaveAsync("introduced", Senses(), Contexts());
        var generator = new Generator((_, _, _, _) => throw new InvalidOperationException("Cache hit must not generate."));
        var result = await new PracticeContextPreparation(path, generator).PrepareAsync(" INTRODUCED ", Senses(), new("", "", ""));
        Assert.Equal(Contexts(), result); Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task MissGeneratesOnceAndPersistsEverySenseForNewServiceInstances()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        var generator = new Generator((word, senses, options, _) =>
        {
            Assert.Equal("introduced", word); Assert.Equal(Senses(), senses); Assert.Equal(Options, options);
            return Task.FromResult<IReadOnlyList<PracticeContext>>(Contexts());
        });
        Assert.Equal(Contexts(), await new PracticeContextPreparation(path, generator).PrepareAsync(" Introduced ", Senses(), Options));
        Assert.Equal(Contexts(), await new PracticeContextCache(path).ReadAsync("introduced", Senses()));
        Assert.Equal(Contexts(), await new PracticeContextPreparation(path, generator).PrepareAsync("introduced", Senses(), new("", "", "")));
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task AutomaticAndManualInstancesAtEquivalentPathsShareOneGeneration()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        var alias = Path.Combine(files.Root, "not-created", "..", "contexts.json").ToUpperInvariant();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new Generator(async (_, _, _, ct) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Contexts(); });
        var first = new PracticeContextPreparation(path, generator).PrepareAsync("introduced", Senses(), Options);
        Assert.False(first.IsCompleted);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manualGenerator = new Generator((_, _, _, _) => throw new InvalidOperationException("Waiter must read the completed cache."));
        var second = new PracticeContextPreparation(alias, manualGenerator).PrepareAsync("INTRODUCED", Senses(), new("", "", ""));
        try
        {
            Assert.False(second.IsCompleted); release.TrySetResult();
            Assert.Equal(Contexts(), await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Contexts(), await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, generator.Calls); Assert.Equal(0, manualGenerator.Calls);
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, second); }
    }

    [Fact]
    public async Task CancelingAWaiterLeavesTheActiveGenerationAndCacheIntact()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeToken = default(CancellationToken);
        var generator = new Generator(async (_, _, _, ct) =>
        { activeToken = ct; entered.TrySetResult(); await release.Task.WaitAsync(ct); return Contexts(); });
        var first = new PracticeContextPreparation(path, generator).PrepareAsync("introduced", Senses(), Options);
        Assert.False(first.IsCompleted);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var canceled = new CancellationTokenSource();
        var waiter = new PracticeContextPreparation(path, generator).PrepareAsync("introduced", Senses(), Options, canceled.Token);
        try
        {
            canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            Assert.False(first.IsCompleted); Assert.False(activeToken.IsCancellationRequested);
            Assert.Equal(1, generator.Calls); release.TrySetResult();
            Assert.Equal(Contexts(), await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(Contexts(), await new PracticeContextCache(path).ReadAsync("introduced", Senses()));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrIncompleteGenerationDoesNotRetryOrCacheAndALaterExplicitCallCanSucceed(bool incomplete)
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json"); var fail = true;
        var generator = new Generator((_, _, _, _) =>
        {
            if (fail && !incomplete) throw new AnalysisException("Fixture generation failed.");
            return Task.FromResult<IReadOnlyList<PracticeContext>>(fail ? Contexts()[..1] : Contexts());
        });
        var service = new PracticeContextPreparation(path, generator);
        if (incomplete) await Assert.ThrowsAnyAsync<ArgumentException>(() => service.PrepareAsync("introduced", Senses(), Options));
        else await Assert.ThrowsAsync<AnalysisException>(() => service.PrepareAsync("introduced", Senses(), Options));
        Assert.Equal(1, generator.Calls); Assert.False(File.Exists(path));
        fail = false;
        Assert.Equal(Contexts(), await service.PrepareAsync("introduced", Senses(), Options));
        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public async Task CanceledRequestCannotGenerateOrWriteAndBrokenCacheCannotBeTreatedAsAMiss()
    {
        using var files = new TempVault(); var path = Path.Combine(files.Root, "contexts.json");
        var generator = new Generator((_, _, _, _) => throw new InvalidOperationException("Generation is not authorized by a cache error."));
        var service = new PracticeContextPreparation(path, generator);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync("introduced", Senses(), Options, canceled.Token));
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, "{broken");
        await Assert.ThrowsAnyAsync<IOException>(() => service.PrepareAsync("introduced", Senses(), Options));
        Assert.Equal("{broken", await File.ReadAllTextAsync(path)); Assert.Equal(0, generator.Calls);
    }

    private sealed class Generator(Func<string, IReadOnlyList<PracticeSense>, ApiOptions, CancellationToken, Task<IReadOnlyList<PracticeContext>>> generate) : IPracticeContextGenerator
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<IReadOnlyList<PracticeContext>> GenerateAsync(string word, IReadOnlyList<PracticeSense> senses, ApiOptions options, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return generate(word, senses, options, ct); }
    }
}
