using System.Collections.Concurrent;
using VocabCollector.Core.Reading;

namespace VocabCollector.Tests;

public sealed class OfflineWordTranslationServiceTests
{
    [Fact]
    public async Task EngineReceivesOnlyTheNormalizedSelectedWordAndSuccessfulResultsAreCached()
    {
        var engine = new ControlledEngine();
        using var service = new OfflineWordTranslationService(engine);
        var pending = service.TranslateAsync("  Don’t  ", default);
        var request = Assert.Single(engine.Requests);
        Assert.Equal("don't", request.Text);
        request.Complete("  不要\0  ");
        Assert.Equal(new WordTranslation("don't", "不要"), await pending);
        Assert.Equal(new WordTranslation("don't", "不要", true), await service.TranslateAsync("DON'T", default));
        Assert.Single(engine.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("the whole sentence is private.")]
    [InlineData("../private")]
    [InlineData("<script>")]
    [InlineData("中文")]
    public async Task InvalidInputDoesNotStartTheEngine(string? word)
    {
        var engine = new ControlledEngine();
        using var service = new OfflineWordTranslationService(engine);
        await Assert.ThrowsAsync<ArgumentException>(() => service.TranslateAsync(word!, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.TranslateAsync(new string('a', 65), default));
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task AlreadyCancelledCallCannotUseAWarmCacheOrStartTheEngine()
    {
        var engine = new DelegateEngine((_, _) => Task.FromResult("苹果"));
        using var service = new OfflineWordTranslationService(engine);
        await service.TranslateAsync("apple", default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync("apple", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync("banana", cancellation.Token));
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task CancellationIsPromptEvenWhenEngineIgnoresItAndLateSuccessCannotWarmTheCache()
    {
        var engine = new ControlledEngine();
        using var service = new OfflineWordTranslationService(engine);
        using var cancellation = new CancellationTokenSource();
        var pending = service.TranslateAsync("apple", cancellation.Token);
        var abandoned = Assert.Single(engine.Requests);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(abandoned.Token.IsCancellationRequested);
        abandoned.Complete("迟到的旧苹果");
        var retry = service.TranslateAsync("APPLE", default);
        Assert.Equal(2, engine.Requests.Count);
        engine.Requests.Last().Complete("新的苹果");
        Assert.Equal(new WordTranslation("apple", "新的苹果"), await retry);
    }

    [Fact]
    public async Task EngineErrorsAreFriendlyRetryableAndNeverCached()
    {
        var engine = new DelegateEngine((_, _) => Task.FromException<string>(new InvalidOperationException("internal runtime canary")));
        using var service = new OfflineWordTranslationService(engine);
        var error = await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("apple", default));
        Assert.Contains("离线", error.Message);
        Assert.DoesNotContain("internal runtime canary", error.Message);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        engine.Reply = (_, _) => Task.FromResult("苹果");
        Assert.Equal(new WordTranslation("apple", "苹果"), await service.TranslateAsync("apple", default));
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task EngineTimeoutIsAFriendlyFailureWhileCallerCancellationKeepsItsToken()
    {
        var engine = new DelegateEngine((_, _) => Task.FromCanceled<string>(new CancellationToken(true)));
        using var service = new OfflineWordTranslationService(engine);
        await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("apple", default));
        engine.Reply = (_, _) => Task.FromResult("苹果");
        Assert.False((await service.TranslateAsync("apple", default)).IsFromCache);
    }

    [Fact]
    public async Task SpecificOfflineFailureMessageIsPreservedForTheReadingPage()
    {
        var unavailable = new WordTranslationException("离线翻译包缺失，请重新安装应用。");
        var engine = new DelegateEngine((_, _) => Task.FromException<string>(unavailable));
        using var service = new OfflineWordTranslationService(engine);
        Assert.Same(unavailable, await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("apple", default)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \0\t\r\n ")]
    public async Task EmptyEngineResultsCannotBecomeSuccessfulTranslations(string? result)
    {
        var engine = new DelegateEngine((_, _) => Task.FromResult(result!));
        using var service = new OfflineWordTranslationService(engine);
        await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("apple", default));
        engine.Reply = (_, _) => Task.FromResult("苹果");
        Assert.False((await service.TranslateAsync("apple", default)).IsFromCache);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task UnboundedOutputIsRejectedAndSuccessfulCacheEvictsTheLeastRecentlyUsedEntryAt128()
    {
        var engine = new DelegateEngine((_, _) => Task.FromResult(new string('中', 2001)));
        using var service = new OfflineWordTranslationService(engine);
        await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("apple", default));
        engine.Reply = (_, _) => Task.FromResult("译文");
        for (var i = 0; i < 128; i++) Assert.False((await service.TranslateAsync(Word(i), default)).IsFromCache);
        Assert.True((await service.TranslateAsync(Word(0), default)).IsFromCache);
        Assert.False((await service.TranslateAsync(Word(128), default)).IsFromCache);
        Assert.True((await service.TranslateAsync(Word(0), default)).IsFromCache);
        Assert.False((await service.TranslateAsync(Word(1), default)).IsFromCache);
        Assert.Equal(131, engine.Calls);
    }

    [Fact]
    public async Task IndependentRequestsReachTheEngineWithoutAnExtraServiceGateAndCompleteOutOfOrder()
    {
        var engine = new ControlledEngine();
        using var service = new OfflineWordTranslationService(engine);
        var tasks = Enumerable.Range(0, 32).Select(index => service.TranslateAsync(Word(index), default)).ToArray();
        Assert.Equal(32, engine.Requests.Count);
        foreach (var request in engine.Requests.Reverse()) request.Complete("译文 " + request.Text);
        var results = await Task.WhenAll(tasks);
        for (var index = 0; index < tasks.Length; index++)
        {
            Assert.Equal("译文 " + Word(index), results[index].TranslatedText);
            Assert.True((await service.TranslateAsync(Word(index), default)).IsFromCache);
        }
        Assert.Equal(32, engine.Requests.Count);
    }

    [Fact]
    public async Task DisposalOwnsTheEngineExactlyOnceAndPreventsAnOutstandingResultFromBeingReturnedOrCached()
    {
        var engine = new ControlledEngine();
        var service = new OfflineWordTranslationService(engine);
        var pending = service.TranslateAsync("apple", default);
        var request = Assert.Single(engine.Requests);
        Parallel.For(0, 12, _ => service.Dispose());
        Assert.Equal(1, engine.Disposals);
        request.Complete("释放后迟到的苹果");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TranslateAsync("apple", default));
        Assert.Single(engine.Requests);
    }

    [Fact]
    public async Task DisposedServiceCannotReadAPreviouslySuccessfulCache()
    {
        var engine = new DelegateEngine((_, _) => Task.FromResult("苹果"));
        var service = new OfflineWordTranslationService(engine);
        await service.TranslateAsync("apple", default);
        service.Dispose(); service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TranslateAsync("apple", default));
        Assert.Equal(1, engine.Calls);
        Assert.Equal(1, engine.Disposals);
    }

    private static string Word(int index) => "word" + (char)('a' + index / 26) + (char)('a' + index % 26);
    private sealed class ControlledEngine : IOfflineTranslationEngine
    {
        public ConcurrentQueue<Request> Requests { get; } = new();
        public int Disposals;
        public Task<string> TranslateAsync(string text, CancellationToken ct)
        { var request = new Request(text, ct); Requests.Enqueue(request); return request.Result.Task; }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    private sealed record Request(string Text, CancellationToken Token)
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(string result) => Result.SetResult(result);
    }
    private sealed class DelegateEngine(Func<string, CancellationToken, Task<string>> reply) : IOfflineTranslationEngine
    {
        public Func<string, CancellationToken, Task<string>> Reply { get; set; } = reply;
        public int Calls, Disposals;
        public Task<string> TranslateAsync(string text, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Reply(text, ct); }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
