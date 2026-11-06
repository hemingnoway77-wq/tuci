using System.Net;
using System.Text;
using VocabCollector.Core.Reading;

namespace VocabCollector.Tests;

public sealed class ArticleContentServiceTests
{
    private const string Science = "https://www.sciencedaily.com/releases/2026/11/example.htm";
    private static readonly string[] Paragraphs =
    [
        "Researchers found that a careful reading of the original evidence reveals more detail than a short feed summary. Their published observations describe the methods and the results clearly.",
        "The second part of the article explains how the work was carried out and why the findings matter. Readers can follow the full account without advertisements or unrelated recommendations.",
        "Finally, the team discusses the limitations of the study and possible next steps. This final paragraph belongs to the real article body and must remain available in the reading view."
    ];

    [Theory]
    [InlineData("www.sciencedaily.com", "<div id='story_text'>", "</div>")]
    [InlineData("blog.cloudflare.com", "<div class='article-content'>", "</div>")]
    [InlineData("www.bbc.com", "<div class='ssrcss-11r1m41-RichTextContainer'>", "</div>")]
    public async Task PublisherContainersYieldOnlyCompletePlainParagraphs(string host, string open, string close)
    {
        var html = "<nav><p>Do not collect navigation or recommendations.</p></nav>" + open +
            "<script>doNotRun()</script><style>.secret{}</style><div class='advertisement'><p>Buy a sponsored product right now. This is not part of the article.</p></div>" +
            string.Join("", Paragraphs.Select(paragraph => "<p>" + paragraph + "</p>")) +
            "<aside><p>Sign up for this newsletter and read more stories.</p></aside><p hidden>Hidden tracking text is not article content.</p>" + close;
        var result = await ArticleExtractor.ExtractAsync(html, new Uri("https://" + host + "/article"));
        Assert.Equal(Paragraphs, result);
    }

    [Fact]
    public async Task BbcTextBlocksPreserveNestedInlineTextAndDecodeEntities()
    {
        var result = await ArticleExtractor.ExtractAsync("<article><div data-component='text-block'><p>" + Paragraphs[0].Replace("Researchers found", "Researchers <strong>found</strong>") +
            " &amp; more.</p></div><div data-component='text-block'><p>" + Paragraphs[1] + "</p></div></article>", new Uri("https://www.bbc.co.uk/news/articles/example"));
        Assert.Equal(2, result.Count);
        Assert.StartsWith("Researchers found", result[0]);
        Assert.EndsWith(" & more.", result[0]);
        Assert.DoesNotContain("<strong>", result[0]);
    }

    [Fact]
    public async Task HtmlBreaksPreserveWordBoundariesAndHiddenStyleWhitespaceIsIgnored()
    {
        var html = "<div id='story_text'><p>Researchers<br>found " + Paragraphs[0] + "</p>" +
            "<p style=' DISPLAY :\n none '>This hidden paragraph must never become visible article content.</p>" +
            "<p>" + Paragraphs[1] + "</p></div>";
        var result = await ArticleExtractor.ExtractAsync(html, new Uri(Science));
        Assert.Equal(2, result.Count);
        Assert.StartsWith("Researchers found ", result[0]);
        Assert.DoesNotContain(result, paragraph => paragraph.Contains("hidden paragraph"));
    }

    [Fact]
    public async Task MissingBodyOrSummaryOnlyIsAnExplicitExtractionFailure()
    {
        foreach (var html in new[] { "<meta name='description' content='feed summary'><p>" + string.Join(" ", Paragraphs) + "</p>", "<div id='story_text'><p>A short summary only.</p></div>" })
            await Assert.ThrowsAsync<ArticleContentException>(() => ArticleExtractor.ExtractAsync(html, new Uri(Science)));
    }

    [Fact]
    public async Task OversizedBodyIsRejectedInsteadOfSilentlyTruncated()
    {
        var html = "<div id='story_text'>" + string.Join("", Enumerable.Range(0, 141).Select(index => "<p>" + index + " " + Paragraphs[0] + "</p>")) + "</div>";
        await Assert.ThrowsAsync<ArticleContentException>(() => ArticleExtractor.ExtractAsync(html, new Uri(Science)));
    }

    [Theory]
    [InlineData("http://www.sciencedaily.com/article")]
    [InlineData("https://www.sciencedaily.com.evil.invalid/article")]
    [InlineData("https://user@www.sciencedaily.com/article")]
    [InlineData("https://@www.sciencedaily.com/article")]
    [InlineData("https://www.sciencedaily.com:444/article")]
    [InlineData("https://127.0.0.1/article")]
    [InlineData("file:///C:/private.html")]
    [InlineData("javascript:alert(1)")]
    public async Task InvalidOrUnsupportedAddressesNeverEnterHttpTransport(string url)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArticleContentException>(() => fixture.Service.GetAsync(Article(url), false, default));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task FullBodyIsCachedAcrossServiceInstancesAndSummaryNeverUsed()
    {
        using var fixture = new Fixture();
        var first = await fixture.Service.GetAsync(Article(Science), false, default);
        Assert.Equal(Paragraphs, first.Paragraphs);
        Assert.False(first.IsFromCache);
        using var second = new ArticleContentService(fixture.Client, fixture.Cache, fixture.Clock);
        var cached = await second.GetAsync(Article(Science), false, default);
        Assert.Equal(Paragraphs, cached.Paragraphs);
        Assert.True(cached.IsFromCache);
        Assert.False(cached.IsOffline);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.DoesNotContain("FEED SUMMARY MUST NEVER BECOME BODY", string.Join(" ", cached.Paragraphs));
    }

    [Theory]
    [InlineData("https://evil.invalid/outside")]
    [InlineData("http://www.sciencedaily.com/downgrade")]
    [InlineData("https://www.bbc.com/news/other-publisher")]
    public async Task RedirectMustRemainInTheOriginalPublisherFamily(string target)
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = _ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(target) } };
        await Assert.ThrowsAsync<ArticleContentException>(() => fixture.Service.GetAsync(Article(Science), false, default));
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Fact]
    public async Task BbcMayRedirectBetweenItsTwoApprovedHosts()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = request => request.RequestUri!.Host == "www.bbc.co.uk"
            ? new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://www.bbc.com/news/article") } }
            : Html("<article><div data-component='text-block'>" + Body() + "</div></article>");
        var result = await fixture.Service.GetAsync(Article("https://www.bbc.co.uk/news/article"), false, default);
        Assert.Equal(Paragraphs, result.Paragraphs);
        Assert.Equal(2, fixture.Handler.Requests);
    }

    [Fact]
    public async Task RedirectLoopAndNonHtmlAreExplicitFailures()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = _ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/again", UriKind.Relative) } };
        await Assert.ThrowsAsync<ArticleContentException>(() => fixture.Service.GetAsync(Article(Science), false, default));
        Assert.InRange(fixture.Handler.Requests, 1, 4);
        fixture.Handler.Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        await Assert.ThrowsAsync<ArticleContentException>(() => fixture.Service.GetAsync(Article(Science), false, default));
        Assert.Empty(Directory.Exists(fixture.Cache) ? Directory.GetFiles(fixture.Cache, "*.json") : []);
    }

    [Fact]
    public async Task DownloadIsBoundedEvenWithoutContentLength()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = _ => new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[2 * 1024 * 1024 + 1])) };
        fixture.Handler.ReplyContentType = "text/html";
        await Assert.ThrowsAsync<ArticleContentException>(() => fixture.Service.GetAsync(Article(Science), false, default));
    }

    [Fact]
    public async Task ExpiredValidFullBodyCanBeUsedOfflineButCallerCancellationStillPropagates()
    {
        using var fixture = new Fixture();
        await fixture.Service.GetAsync(Article(Science), false, default);
        fixture.Clock.Now += TimeSpan.FromDays(1);
        fixture.Handler.Fail = true;
        var offline = await fixture.Service.GetAsync(Article(Science), false, default);
        Assert.True(offline.IsFromCache && offline.IsOffline);
        Assert.Equal(Paragraphs, offline.Paragraphs);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetAsync(Article(Science), false, canceled.Token));
    }

    [Fact]
    public async Task TimeoutIsFriendlyAndDoesNotCacheAnEmptyOrSummaryBody()
    {
        using var fixture = new Fixture();
        fixture.Handler.Block = true;
        using var service = new ArticleContentService(fixture.Client, fixture.Cache, fixture.Clock, TimeSpan.FromMilliseconds(40));
        var error = await Assert.ThrowsAsync<ArticleContentException>(() => service.GetAsync(Article(Science), false, default));
        Assert.Contains("超时", error.Message);
        Assert.Empty(Directory.Exists(fixture.Cache) ? Directory.GetFiles(fixture.Cache, "*.json") : []);
    }

    [Fact]
    public async Task CorruptCacheIsIgnoredAndFailedCacheWriteDoesNotHideSuccessfulFullBody()
    {
        using var fixture = new Fixture();
        await fixture.Service.GetAsync(Article(Science), false, default);
        await File.WriteAllTextAsync(Directory.GetFiles(fixture.Cache, "*.json").Single(), "not-json");
        using var reload = new ArticleContentService(fixture.Client, fixture.Cache, fixture.Clock);
        Assert.Equal(Paragraphs, (await reload.GetAsync(Article(Science), false, default)).Paragraphs);
        var blocker = Path.Combine(fixture.Root, "file-not-directory");
        await File.WriteAllTextAsync(blocker, "keep");
        using var noCache = new ArticleContentService(fixture.Client, blocker, fixture.Clock);
        var content = await noCache.GetAsync(Article(Science), false, default);
        Assert.Equal(Paragraphs, content.Paragraphs);
        Assert.Contains("缓存", content.Message);
    }

    [Fact]
    public async Task CachedArticlesAreBoundedToEightyFiles()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 83; index++)
            await fixture.Service.GetAsync(Article(Science + "?edition=" + index), false, default);
        Assert.Equal(80, Directory.GetFiles(fixture.Cache, "*.json").Length);
        Assert.Empty(Directory.GetFiles(fixture.Cache, "*.tmp"));
    }

    [Fact]
    public async Task UnexpectedAutomaticRedirectResultIsNotAccepted()
    {
        using var fixture = new Fixture();
        fixture.Handler.Reply = _ =>
        {
            var response = Html();
            response.RequestMessage = new(HttpMethod.Get, "https://evil.invalid/article");
            return response;
        };
        await Assert.ThrowsAsync<ArticleContentException>(() => fixture.Service.GetAsync(Article(Science), false, default));
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Fact]
    public async Task CallerCancellationDuringRequestIsNeverConvertedToSummaryOrTimeout()
    {
        using var fixture = new Fixture();
        fixture.Handler.Block = true;
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetAsync(Article(Science), false, canceled.Token));
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Fact]
    public async Task CallerCancellationDuringRefreshDoesNotReturnAnAvailableStaleCache()
    {
        using var fixture = new Fixture();
        await fixture.Service.GetAsync(Article(Science), false, default);
        fixture.Clock.Now += TimeSpan.FromDays(1);
        fixture.Handler.Block = true;
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GetAsync(Article(Science), true, canceled.Token));
    }

    private static ReadingArticle Article(string url) => new("Article title", "FEED SUMMARY MUST NEVER BECOME BODY", url, "ScienceDaily", ReadingCategory.General);
    private static string Body() => string.Join("", Paragraphs.Select(text => "<p>" + text + "</p>"));
    private static HttpResponseMessage Html(string? html = null) => new(HttpStatusCode.OK) { Content = new StringContent(html ?? "<div id='story_text'>" + Body() + "</div>", Encoding.UTF8, "text/html") };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tuci-article-tests-" + Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Root, "cache");
        public Handler Handler { get; } = new();
        public Clock Clock { get; } = new();
        public HttpClient Client { get; }
        public ArticleContentService Service { get; }
        public Fixture() { Directory.CreateDirectory(Root); Client = new(Handler); Service = new(Client, Cache, Clock); }
        public void Dispose() { Service.Dispose(); Client.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        public bool Block, Fail;
        public string? ReplyContentType;
        public Func<HttpRequestMessage, HttpResponseMessage> Reply = _ => Html();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Block) await Task.Delay(Timeout.Infinite, token);
            if (Fail) throw new HttpRequestException("offline");
            var response = Reply(request);
            if (ReplyContentType != null) response.Content.Headers.ContentType = new(ReplyContentType);
            response.RequestMessage ??= request;
            return response;
        }
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T00:00:00Z"); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
}
