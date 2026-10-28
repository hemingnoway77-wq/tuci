using System.Net;
using System.Text;
using VocabCollector.Core.Reading;

namespace VocabCollector.Tests;

public class DailyReadingServiceTests
{
    [Fact]
    public async Task RssAndAtomProvideTwoGeneralArticlesAndOneTechnologyArticle()
    {
        using var fixture = new ReadingFixture();
        var result = await fixture.Service.GetAsync(false, default);

        Assert.Equal(new DateOnly(2026, 10, 28), result.Day);
        Assert.Equal(2, result.Articles.Count(a => a.Category == ReadingCategory.General));
        Assert.Single(result.Articles, a => a.Category == ReadingCategory.Technology);
        Assert.Contains(result.Articles, a => a.SourceName == "Science");
        Assert.Contains(result.Articles, a => a.SourceName == "Environment");
        Assert.All(result.Articles, a => Assert.NotNull(a.PublishedAt));
        Assert.False(result.IsFromCache);
        Assert.False(result.IsOffline);
    }

    [Theory]
    [InlineData("Mon, 26 Oct 2026 21:35:08 EDT", "2026-10-26T21:35:08-04:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 EST", "2026-10-26T21:35:08-05:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 CDT", "2026-10-26T21:35:08-05:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 CST", "2026-10-26T21:35:08-06:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 MDT", "2026-10-26T21:35:08-06:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 MST", "2026-10-26T21:35:08-07:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 PDT", "2026-10-26T21:35:08-07:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 PST", "2026-10-26T21:35:08-08:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 UT", "2026-10-26T21:35:08+00:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 GMT", "2026-10-26T21:35:08+00:00")]
    [InlineData("Mon, 26 Oct 2026 21:35:08 -0400", "2026-10-26T21:35:08-04:00")]
    [InlineData("2026-10-26T21:35:08+08:00", "2026-10-26T21:35:08+08:00")]
    public async Task FeedPublicationDatesKeepExplicitRfc822OrIsoTimezone(string feedDate, string expectedDate)
    {
        using var fixture = new ReadingFixture();
        fixture.Bodies[ReadingFixture.GeneralUrl] = $"""
            <rss><channel><item><title>Real ScienceDaily article</title><link>https://science.test/real</link>
            <pubDate>{feedDate}</pubDate></item></channel></rss>
            """;
        var article = Assert.Single((await fixture.Service.GetAsync(false, default)).Articles, a => a.SourceName == "Science");
        var expected = DateTimeOffset.Parse(expectedDate, System.Globalization.CultureInfo.InvariantCulture);

        Assert.NotNull(article.PublishedAt);
        Assert.Equal(expected, article.PublishedAt);
        Assert.Equal(expected.Offset, article.PublishedAt.Value.Offset);
    }

    [Fact]
    public async Task UnknownPublicationTimezoneStaysUnknownRatherThanUsingLocalTimezone()
    {
        using var fixture = new ReadingFixture();
        fixture.Bodies[ReadingFixture.GeneralUrl] = """
            <rss><channel><item><title>Unknown timezone</title><link>https://science.test/unknown</link>
            <pubDate>Mon, 26 Oct 2026 21:35:08 XYZ</pubDate></item></channel></rss>
            """;
        var article = Assert.Single((await fixture.Service.GetAsync(false, default)).Articles, a => a.SourceName == "Science");

        Assert.Null(article.PublishedAt);
    }

    [Fact]
    public async Task FeedHtmlBecomesBoundedPlainTextAndRelativeLinksUseFeedAddress()
    {
        using var fixture = new ReadingFixture();
        fixture.Bodies[ReadingFixture.GeneralUrl] = """
            <rss><channel><item>
            <title><![CDATA[ A &amp; <b>clear</b> title ]]></title>
            <description><![CDATA[<script>hidden()</script><style>.secret{}</style><p>Read &amp; learn<br/>today.</p>]]></description>
            <link>/articles/clear</link>
            </item></channel></rss>
            """;
        fixture.Bodies[ReadingFixture.EnvironmentUrl] = Rss("Long " + new string('x', 500), "https://environment.test/long", new string('s', 2000));
        var result = await fixture.Service.GetAsync(false, default);

        var article = Assert.Single(result.Articles, a => a.SourceName == "Science");
        Assert.Equal("A & clear title", article.Title);
        Assert.Equal("Read & learn today.", article.Summary);
        Assert.Equal("https://science.test/articles/clear", article.Url);
        Assert.All(result.Articles, a => Assert.True(a.Title.Length <= 200 && a.Summary.Length <= 600));
    }

    [Fact]
    public async Task AtomPrefersAlternateArticleLinkOverSelfLink()
    {
        using var fixture = new ReadingFixture();
        fixture.Bodies[ReadingFixture.TechnologyUrl] = """
            <feed xmlns="http://www.w3.org/2005/Atom"><entry>
            <title>Networking news</title><summary type="html">&lt;p&gt;A &amp;amp; B&lt;/p&gt;</summary>
            <link rel="self" href="/api/entry"/><link rel="alternate" href="/posts/networking"/>
            <updated>2026-10-27T15:00:00Z</updated></entry></feed>
            """;
        var article = Assert.Single((await fixture.Service.GetAsync(false, default)).Articles, a => a.Category == ReadingCategory.Technology);

        Assert.Equal("https://tech.test/posts/networking", article.Url);
        Assert.Equal("A & B", article.Summary);
    }

    [Fact]
    public async Task UnsafeArticleLinksAndDuplicateUrlsAreExcluded()
    {
        using var fixture = new ReadingFixture();
        fixture.Bodies[ReadingFixture.GeneralUrl] = """
            <rss><channel>
            <item><title>Unsafe script</title><link>javascript:alert(1)</link></item>
            <item><title>Local file</title><link>file:///C:/secret.txt</link></item>
            <item><title>Credentials</title><link>https://user:password@science.test/private</link></item>
            <item><title>Safe article</title><link>https://science.test/safe#top</link></item>
            <item><title>Duplicate article</title><link>https://science.test/safe#other</link></item>
            </channel></rss>
            """;
        var result = await fixture.Service.GetAsync(false, default);

        Assert.Equal(3, result.Articles.Count);
        var general = Assert.Single(result.Articles, a => a.SourceName == "Science");
        Assert.Equal("https://science.test/safe", general.Url);
        Assert.DoesNotContain(result.Articles, a => a.Title.StartsWith("Unsafe") || a.Title == "Credentials" || a.Title == "Local file");
    }

    [Fact]
    public async Task SameDayCacheSurvivesServiceRecreationWithoutAnyNetworkRequests()
    {
        using var fixture = new ReadingFixture();
        var first = await fixture.Service.GetAsync(false, default);
        var calls = fixture.Calls;
        fixture.Respond = (_, _) => throw new InvalidOperationException("Cache must prevent network access.");
        var second = await fixture.NewService().GetAsync(false, default);

        Assert.Equal(first.Articles, second.Articles);
        Assert.True(second.IsFromCache);
        Assert.False(second.IsOffline);
        Assert.Equal(calls, fixture.Calls);
    }

    [Fact]
    public async Task NewDayRotatesAllThreeArticlesWhenEnoughCandidatesExist()
    {
        using var fixture = new ReadingFixture();
        var first = await fixture.Service.GetAsync(false, default);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        var second = await fixture.Service.GetAsync(false, default);
        var sameDay = await fixture.NewService().GetAsync(false, default);

        Assert.Equal(first.Day.AddDays(1), second.Day);
        Assert.Empty(first.Articles.Select(a => a.Url).Intersect(second.Articles.Select(a => a.Url)));
        Assert.Equal(second.Articles, sameDay.Articles);
        Assert.Equal(6, fixture.Calls);
    }

    [Fact]
    public async Task ExplicitRefreshFetchesAgainWithoutChangingSameDaySelection()
    {
        using var fixture = new ReadingFixture();
        var first = await fixture.Service.GetAsync(false, default);
        var refreshed = await fixture.Service.GetAsync(true, default);

        Assert.Equal(6, fixture.Calls);
        Assert.False(refreshed.IsFromCache);
        Assert.Equal(first.Articles, refreshed.Articles);
    }

    [Fact]
    public async Task PartialFeedFailureStillShowsAvailableArticlesAndExplainsIssue()
    {
        using var fixture = new ReadingFixture();
        fixture.Bodies[ReadingFixture.EnvironmentUrl] = "not XML";
        var result = await fixture.Service.GetAsync(false, default);

        Assert.Equal(3, result.Articles.Count);
        Assert.False(result.IsOffline);
        Assert.Contains("部分", result.Message);
    }

    [Fact]
    public async Task AllFeedFailuresReturnPreviousCachedRecommendationWithoutOverwritingIt()
    {
        using var fixture = new ReadingFixture();
        var first = await fixture.Service.GetAsync(false, default);
        var path = Assert.Single(Directory.GetFiles(fixture.Directory, "*.json"));
        var original = await File.ReadAllTextAsync(path);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        fixture.Respond = (_, _) => throw new HttpRequestException("Offline");
        var fallback = await fixture.Service.GetAsync(false, default);

        Assert.Equal(first.Day, fallback.Day);
        Assert.Equal(first.Articles, fallback.Articles);
        Assert.True(fallback.IsFromCache);
        Assert.True(fallback.IsOffline);
        Assert.NotEmpty(fallback.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EmptySuccessfulFeedsDoNotEraseAnExistingCache()
    {
        using var fixture = new ReadingFixture();
        var first = await fixture.Service.GetAsync(false, default);
        fixture.Respond = (_, _) => Task.FromResult(XmlResponse("<rss><channel/></rss>"));
        var refreshed = await fixture.Service.GetAsync(true, default);

        Assert.Equal(first.Articles, refreshed.Articles);
        Assert.True(refreshed.IsFromCache);
        Assert.Equal(first.Articles, (await fixture.NewService().GetAsync(false, default)).Articles);
    }

    [Fact]
    public async Task FirstUseOfflineReturnsUnderstandableEmptyState()
    {
        using var fixture = new ReadingFixture();
        fixture.Respond = (_, _) => throw new HttpRequestException("Offline");
        var result = await fixture.Service.GetAsync(false, default);

        Assert.Empty(result.Articles);
        Assert.True(result.IsOffline);
        Assert.False(result.IsFromCache);
        Assert.Contains("网络", result.Message);
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task CorruptedCacheCanBeReplacedByFreshArticles()
    {
        using var fixture = new ReadingFixture();
        await fixture.Service.GetAsync(false, default);
        var path = Assert.Single(Directory.GetFiles(fixture.Directory, "*.json"));
        await File.WriteAllTextAsync(path, "{broken");
        var result = await fixture.NewService().GetAsync(false, default);

        Assert.Equal(3, result.Articles.Count);
        Assert.False(result.IsFromCache);
        Assert.NotEqual("{broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CacheWriteFailureStillReturnsFreshArticles()
    {
        using var fixture = new ReadingFixture();
        var blockedDirectory = Path.Combine(fixture.Directory, "a-file");
        await File.WriteAllTextAsync(blockedDirectory, "cannot create a directory here");
        var service = new DailyReadingService(fixture.Client, blockedDirectory, fixture.Clock, fixture.Feeds);
        var result = await service.GetAsync(false, default);

        Assert.Equal(3, result.Articles.Count);
        Assert.False(result.IsFromCache);
        Assert.Contains("缓存", result.Message);
        Assert.Equal("cannot create a directory here", await File.ReadAllTextAsync(blockedDirectory));
    }

    [Fact]
    public async Task DtdAndOversizedFeedsAreRejectedWithoutLosingOtherSources()
    {
        using var fixture = new ReadingFixture();
        fixture.Bodies[ReadingFixture.GeneralUrl] = """
            <!DOCTYPE rss [<!ENTITY secret SYSTEM "file:///C:/Windows/win.ini">]>
            <rss><channel><item><title>&secret;</title><link>https://science.test/secret</link></item></channel></rss>
            """;
        fixture.Bodies[ReadingFixture.EnvironmentUrl] = "<rss><channel>" + new string('x', 2_100_000) + "</channel></rss>";
        var result = await fixture.Service.GetAsync(false, default);

        Assert.Single(result.Articles);
        Assert.Equal(ReadingCategory.Technology, result.Articles[0].Category);
        Assert.Contains("部分", result.Message);
    }

    [Fact]
    public async Task OversizedFeedWithoutContentLengthIsAlsoRejected()
    {
        using var fixture = new ReadingFixture();
        fixture.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri == ReadingFixture.GeneralUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent("<rss>" + new string('x', 2_100_000) + "</rss>") }
            : XmlResponse(fixture.Bodies[request.RequestUri.AbsoluteUri]));
        var result = await fixture.Service.GetAsync(false, default);

        Assert.Equal(3, result.Articles.Count);
        Assert.DoesNotContain(result.Articles, a => a.SourceName == "Science");
        Assert.Contains("部分", result.Message);
    }

    [Fact]
    public async Task CacheContainingUnsafeArticleUrlIsNotShown()
    {
        using var fixture = new ReadingFixture();
        var first = await fixture.Service.GetAsync(false, default);
        var path = Assert.Single(Directory.GetFiles(fixture.Directory, "*.json"));
        var original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original.Replace(first.Articles[0].Url, "javascript:alert(1)", StringComparison.Ordinal));
        var result = await fixture.NewService().GetAsync(false, default);

        Assert.False(result.IsFromCache);
        Assert.Equal(6, fixture.Calls);
        Assert.All(result.Articles, a => Assert.StartsWith("https://", a.Url));
    }

    [Fact]
    public async Task FailedAtomicReplacementPreservesPreviousCacheAndRemovesTemporaryFile()
    {
        using var fixture = new ReadingFixture();
        await fixture.Service.GetAsync(false, default);
        var path = Assert.Single(Directory.GetFiles(fixture.Directory, "*.json"));
        var original = await File.ReadAllTextAsync(path);
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await fixture.NewService().GetAsync(false, default);
            Assert.Equal(3, result.Articles.Count);
            Assert.Contains("缓存", result.Message);
        }

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentSameDayCallsShareTheNewlySavedRecommendation()
    {
        using var fixture = new ReadingFixture();
        var results = await Task.WhenAll(fixture.Service.GetAsync(false, default), fixture.Service.GetAsync(false, default));

        Assert.Equal(3, fixture.Calls);
        Assert.Equal(results[0].Articles, results[1].Articles);
        Assert.True(results[1].IsFromCache);
    }

    [Theory]
    [InlineData("../posts/relative", "https://tech.test/posts/relative")]
    [InlineData("//other.test/post", "https://other.test/post")]
    public async Task AtomRelativeAndProtocolRelativeLinksResolveSafely(string link, string expected)
    {
        using var fixture = new ReadingFixture();
        fixture.Bodies[ReadingFixture.TechnologyUrl] = $"""
            <feed xmlns="http://www.w3.org/2005/Atom"><entry>
            <title>Article</title><summary type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>First.</p><p>Second.</p></div></summary>
            <link href="{link}"/></entry></feed>
            """;
        var article = Assert.Single((await fixture.Service.GetAsync(false, default)).Articles, a => a.Category == ReadingCategory.Technology);

        Assert.Equal(expected, article.Url);
        Assert.Equal("First. Second.", article.Summary);
    }

    [Fact]
    public async Task FeedRequestsStartConcurrentlyAndOneFailureDoesNotCancelOthers()
    {
        using var fixture = new ReadingFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Respond = async (request, token) =>
        {
            if (fixture.Calls == 3) started.TrySetResult();
            await release.Task.WaitAsync(token);
            return request.RequestUri!.AbsoluteUri == ReadingFixture.EnvironmentUrl
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : XmlResponse(fixture.Bodies[request.RequestUri.AbsoluteUri]);
        };
        var pending = fixture.Service.GetAsync(false, default);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { release.TrySetResult(); }
        var result = await pending;

        Assert.Equal(3, result.Articles.Count);
        Assert.False(result.IsOffline);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndLeavesNoCache()
    {
        using var fixture = new ReadingFixture();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Respond = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return XmlResponse("<rss/>");
        };
        var pending = fixture.Service.GetAsync(false, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.GetFiles(fixture.Directory));
    }

    private static HttpResponseMessage XmlResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/xml")
    };

    private static string Rss(string title, string url, string summary = "A useful article.") =>
        $"<rss><channel><item><title>{title}</title><description>{summary}</description><link>{url}</link><pubDate>Tue, 27 Oct 2026 10:00:00 GMT</pubDate></item></channel></rss>";

    private static string RssPool(string host) => "<rss><channel>" + string.Concat(Enumerable.Range(1, 6).Select(i =>
        $"<item><title>Article {i}</title><description>English reading {i}.</description><link>https://{host}/article-{i}</link><pubDate>Tue, 27 Oct 2026 10:00:00 GMT</pubDate></item>")) + "</channel></rss>";

    private sealed class ReadingFixture : IDisposable
    {
        public const string GeneralUrl = "https://science.test/rss/feed.xml";
        public const string EnvironmentUrl = "https://environment.test/feed.xml";
        public const string TechnologyUrl = "https://tech.test/rss/";
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "vocab-reading-tests-" + Guid.NewGuid().ToString("N"));
        public FixedClock Clock { get; } = new();
        public IReadOnlyList<ReadingFeed> Feeds { get; } =
        [new("Science", GeneralUrl, ReadingCategory.General), new("Environment", EnvironmentUrl, ReadingCategory.General), new("Technology", TechnologyUrl, ReadingCategory.Technology)];
        public Dictionary<string, string> Bodies { get; } = new()
        {
            [GeneralUrl] = RssPool("science.test"),
            [EnvironmentUrl] = RssPool("environment.test"),
            [TechnologyUrl] = "<feed xmlns=\"http://www.w3.org/2005/Atom\">" + string.Concat(Enumerable.Range(1, 4).Select(i =>
                $"<entry><title>Tech {i}</title><summary>Networking {i}.</summary><link href=\"https://tech.test/post-{i}\"/><published>2026-10-27T10:00:00Z</published></entry>")) + "</feed>"
        };
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
        public HttpClient Client { get; }
        public DailyReadingService Service { get; }
        private int calls;
        public int Calls => calls;

        public ReadingFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Respond = (request, _) => Task.FromResult(XmlResponse(Bodies[request.RequestUri!.AbsoluteUri]));
            Client = new HttpClient(new ReadingHandler(async (request, token) =>
            {
                Interlocked.Increment(ref calls);
                return await Respond(request, token);
            }));
            Service = NewService();
        }

        public DailyReadingService NewService() => new(Client, Directory, Clock, Feeds);
        public void Dispose() { Client.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }

    private sealed class ReadingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class UnknownLengthContent(string body) : HttpContent
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes(body);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, false));
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
