using System.Net;
using System.Text;
using VocabCollector.Core.Reading;

namespace VocabCollector.Tests;

public class WordTranslationServiceTests
{
    [Fact]
    public async Task SendsOnlyTheNormalizedSelectedWordWithoutCredentialsAndReturnsPlainTranslation()
    {
        using var handler = new TranslationHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.mymemory.translated.net/get?q=don%27t&langpair=en%7Czh-CN", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(Json("""{"responseStatus":200,"quotaFinished":false,"responseData":{"translatedText":"  不要\u0000  "}}"""));
        });
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client);
        var result = await service.TranslateAsync("  Don’t  ", default);
        Assert.Equal(new WordTranslation("don't", "不要"), result);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("the whole sentence is private.")]
    [InlineData("../private")]
    [InlineData("<script>")]
    [InlineData("中文")]
    public async Task InvalidInputNeverLeavesTheProcess(string word)
    {
        using var handler = new TranslationHandler((_, _) => throw new InvalidOperationException("Unexpected network request."));
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.TranslateAsync(word, default));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.TranslateAsync(new string('a', 65), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SuccessfulCacheUsesNormalizedKeysAndAlreadyCancelledCallsDoNotReadIt()
    {
        using var handler = new TranslationHandler((_, _) => Task.FromResult(Success("苹果")));
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client);
        Assert.False((await service.TranslateAsync("Apple", default)).IsFromCache);
        Assert.Equal(new WordTranslation("apple", "苹果", true), await service.TranslateAsync(" APPLE ", default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync("apple", cancelled.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("[]")]
    [InlineData("{\"responseData\":{\"translatedText\":\"缺少状态\"}}")]
    [InlineData("{\"responseStatus\":\"200\",\"responseData\":{\"translatedText\":\"类型错误\"}}")]
    [InlineData("{\"responseStatus\":200,\"responseData\":{\"translatedText\":0}}")]
    [InlineData("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"   \"}}")]
    public async Task MalformedResponseIsFriendlyAndNeverCached(string payload)
    {
        using var handler = new TranslationHandler((_, _) => Task.FromResult(Json(payload)));
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client);
        var error = await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("broken", default));
        Assert.Contains("翻译", error.Message);
        handler.Reply = (_, _) => Task.FromResult(Success("有效译文"));
        Assert.Equal("有效译文", (await service.TranslateAsync("broken", default)).TranslatedText);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("{\"responseStatus\":403,\"quotaFinished\":true}")]
    [InlineData("{\"responseStatus\":429}")]
    [InlineData("{\"responseStatus\":200,\"quotaFinished\":true,\"responseData\":{\"translatedText\":\"quota\"}}")]
    [InlineData("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\" MYMEMORY WARNING: QUOTA EXHAUSTED\"}}")]
    public async Task EmbeddedQuotaErrorsAreNotShownAsTranslations(string payload)
    {
        using var handler = new TranslationHandler((_, _) => Task.FromResult(Json(payload)));
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client);
        var error = await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("limited", default));
        Assert.True(error.Message.Contains("额度") || error.Message.Contains("受限"));
    }

    [Theory]
    [InlineData(302)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpErrorsAreFriendlyAndRedirectsAreNotAccepted(int status)
    {
        using var handler = new TranslationHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Headers = { Location = new Uri("https://unexpected.example/") } }));
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client);
        var error = await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("unavailable", default));
        Assert.Contains("翻译", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TranslationAndResponseSizesAreBoundedIncludingUndeclaredStreamSizes()
    {
        using var handler = new TranslationHandler((_, _) => Task.FromResult(Success(new string('中', 2001))));
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client);
        await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("large", default));
        var oversizedJson = "{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"ignored\"},\"padding\":\"" + new string('a', 1024 * 1024) + "\"}";
        handler.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UndeclaredStream(Encoding.UTF8.GetBytes(oversizedJson))) });
        await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("streamed", default));
        handler.Reply = (_, _) =>
        {
            var response = Success("ignored"); response.Content.Headers.ContentLength = 1024 * 1024;
            return Task.FromResult(response);
        };
        await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("declared", default));
    }

    [Fact]
    public async Task TimeoutIsFriendlyButCallerCancellationRemainsCancellation()
    {
        using var handler = new TranslationHandler(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Success("late"); });
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client, TimeSpan.FromMilliseconds(60));
        var timeout = await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("slow", default));
        Assert.Contains("超时", timeout.Message);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranslateAsync("cancelled", cancellation.Token));
    }

    [Fact]
    public async Task LateSuccessAfterCancellationCannotPopulateTheCache()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new TranslationHandler((_, _) => { started.SetResult(); return reply.Task; });
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client);
        using var cancellation = new CancellationTokenSource();
        var request = service.TranslateAsync("late", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel(); reply.SetResult(Success("过期"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        handler.Reply = (_, _) => Task.FromResult(Success("重新查询"));
        Assert.Equal("重新查询", (await service.TranslateAsync("late", default)).TranslatedText);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CacheIsBoundedTo128AndEvictsTheLeastRecentlyUsedSuccess()
    {
        using var handler = new TranslationHandler((_, _) => Task.FromResult(Success("缓存")));
        using var client = new HttpClient(handler);
        using var service = new MyMemoryWordTranslationService(client);
        static string Word(int number) => "cache" + (char)('a' + number / 26) + (char)('a' + number % 26);
        for (var i = 0; i < 128; i++) await service.TranslateAsync(Word(i), default);
        Assert.True((await service.TranslateAsync(Word(0), default)).IsFromCache);
        await service.TranslateAsync(Word(128), default);
        Assert.True((await service.TranslateAsync(Word(0), default)).IsFromCache);
        Assert.False((await service.TranslateAsync(Word(1), default)).IsFromCache);
        Assert.Equal(130, handler.Calls);
    }

    [Fact]
    public async Task NetworkErrorsAreWrappedAndDisposalDoesNotOwnInjectedHttpClient()
    {
        using var handler = new TranslationHandler((_, _) => throw new HttpRequestException("network failure"));
        using var client = new HttpClient(handler);
        var service = new MyMemoryWordTranslationService(client);
        await Assert.ThrowsAsync<WordTranslationException>(() => service.TranslateAsync("offline", default));
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TranslateAsync("offline", default));
        handler.Reply = (_, _) => Task.FromResult(Success("可用"));
        using var response = await client.GetAsync("https://example.test/");
        Assert.True(response.IsSuccessStatusCode);
    }

    private static HttpResponseMessage Success(string value) => Json(System.Text.Json.JsonSerializer.Serialize(new { responseStatus = 200, responseData = new { translatedText = value }, quotaFinished = false }));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class TranslationHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = reply;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Reply(request, cancellationToken); }
    }
    private sealed class UndeclaredStream(byte[] data) : MemoryStream(data)
    { public override bool CanSeek => false; }
}
