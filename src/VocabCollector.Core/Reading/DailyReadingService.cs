using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VocabCollector.Core.Reading;

public sealed class DailyReadingService : IDailyReadingService
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxPoolSize = 300;
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(8);
    private static readonly Regex HiddenHtml = new(@"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex HtmlTags = new("<[^>]*>", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly ReadingFeed[] DefaultFeeds =
    [
        new("ScienceDaily · Society", "https://www.sciencedaily.com/rss/top/society.xml", ReadingCategory.General),
        new("ScienceDaily · Environment", "https://www.sciencedaily.com/rss/earth_climate/environmental_science.xml", ReadingCategory.General),
        new("Cloudflare Blog", "https://blog.cloudflare.com/rss/", ReadingCategory.Technology),
        new("BBC · Technology", "https://feeds.bbci.co.uk/news/technology/rss.xml", ReadingCategory.Technology)
    ];

    private readonly HttpClient client;
    private readonly string cacheDirectory;
    private readonly TimeProvider clock;
    private readonly IReadOnlyList<ReadingFeed> feeds;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ReadingCache? memoryCache;

    public DailyReadingService(HttpClient client, string cacheDirectory, TimeProvider? timeProvider = null, IReadOnlyList<ReadingFeed>? feeds = null)
    {
        this.client = client;
        this.cacheDirectory = cacheDirectory;
        clock = timeProvider ?? TimeProvider.System;
        this.feeds = feeds ?? DefaultFeeds;
    }

    public async Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
            var cache = memoryCache ?? await ReadCacheAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!forceRefresh && cache?.Day == today)
                return new(cache.Day, cache.Articles, true, false, "今日推荐已从本地缓存加载。");

            var results = await Task.WhenAll(feeds.Select(feed => FetchAsync(feed, cancellationToken)));
            cancellationToken.ThrowIfCancellationRequested();
            var fresh = results.SelectMany(result => result.Articles).ToArray();
            var hasFailures = results.Any(result => result.Failed);
            if (fresh.Length == 0)
            {
                if (cache is not null)
                    return new(cache.Day, cache.Articles, true, hasFailures,
                        hasFailures ? "网络或阅读来源暂不可用，正在显示上次缓存的推荐。" : "来源暂未提供可用文章，保留上次缓存的推荐。");
                return new(today, [], false, hasFailures,
                    hasFailures ? "暂时无法获取文章。请检查网络后刷新；首次使用还没有本地缓存。" : "来源暂未提供可用的英文文章，请稍后刷新。");
            }

            var pool = fresh.Concat(cache?.Pool ?? []).DistinctBy(a => a.Url).Take(MaxPoolSize).ToArray();
            var selected = SelectArticles(pool, today, cache);
            var updated = new ReadingCache(1, today, selected, pool);
            var saved = await WriteCacheAsync(updated, cancellationToken);
            memoryCache = updated;
            var message = hasFailures ? "部分阅读来源暂不可用，已展示可获取的文章。" : "今日阅读已更新。";
            if (selected.Length < 3) message += " 部分分类暂缺文章，可稍后刷新。";
            if (!saved) message += " 本次未能保存本地缓存，下次可重试。";
            return new(today, selected, false, false, message);
        }
        finally { gate.Release(); }
    }

    private async Task<FeedResult> FetchAsync(ReadingFeed feed, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FeedTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, feed.Url);
            request.Headers.UserAgent.ParseAdd("VocabCollector/1.0 (+daily-reading)");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InvalidDataException("Reading feed is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = await ReadBoundedAsync(stream, timeout.Token);
            using var reader = XmlReader.Create(body, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxResponseBytes
            });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, timeout.Token);
            var feedUri = new Uri(feed.Url, UriKind.Absolute);
            var articles = document.Descendants()
                .Where(element => element.Name.LocalName is "item" or "entry")
                .Select(element => ParseArticle(element, feed, feedUri))
                .OfType<ReadingArticle>().DistinctBy(article => article.Url).Take(80).ToArray();
            return new(articles, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new([], true); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or XmlException or UriFormatException or RegexMatchTimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new([], true);
        }
    }

    private static ReadingArticle? ParseArticle(XElement element, ReadingFeed feed, Uri feedUri)
    {
        var title = CleanText(ChildText(element, "title"), 200);
        var link = element.Name.LocalName == "entry"
            ? element.Elements().FirstOrDefault(child => child.Name.LocalName == "link" && ((string?)child.Attribute("rel") is null or "alternate"))?.Attribute("href")?.Value
            : ChildText(element, "link");
        var url = SafeUrl(link, feedUri);
        if (title.Length == 0 || url is null) return null;
        var summary = CleanText(ChildText(element, "description", "summary", "content", "encoded"), 600);
        if (summary.Length == 0) summary = "暂无摘要，点击阅读全文。";
        var dateText = ChildText(element, "pubDate", "published", "updated", "date");
        var publishedAt = ParsePublicationDate(dateText);
        return new(title, summary, url, CleanText(feed.Name, 80), feed.Category, publishedAt);
    }

    private static DateTimeOffset? ParsePublicationDate(string text)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date;
        var trimmed = text.Trim();
        var separator = trimmed.LastIndexOf(' ');
        if (separator < 0) return null;
        // RFC 822 names denote these fixed offsets; never consult the machine's timezone.
        var offset = trimmed[(separator + 1)..].ToUpperInvariant() switch
        {
            "UT" or "UTC" or "GMT" => "+00:00",
            "EST" => "-05:00",
            "EDT" => "-04:00",
            "CST" => "-06:00",
            "CDT" => "-05:00",
            "MST" => "-07:00",
            "MDT" => "-06:00",
            "PST" => "-08:00",
            "PDT" => "-07:00",
            _ => null
        };
        return offset is not null && DateTimeOffset.TryParse(trimmed[..(separator + 1)] + offset,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) ? date : null;
    }

    private static string ChildText(XElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var child = element.Elements().FirstOrDefault(child => child.Name.LocalName == name);
            if (child is not null && !string.IsNullOrWhiteSpace(child.Value))
                return child.HasElements ? child.ToString(SaveOptions.DisableFormatting) : child.Value;
        }
        return "";
    }

    private static string CleanText(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        // Bound individual HTML fields too: a single large description must not monopolize parsing.
        var text = value.Length > 16000 ? value[..16000] : value;
        text = WebUtility.HtmlDecode(WebUtility.HtmlDecode(text));
        text = HiddenHtml.Replace(text, " ");
        text = HtmlTags.Replace(text, " ");
        text = Whitespace.Replace(text, " ").Trim();
        return text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";
    }

    private static string? SafeUrl(string? link, Uri? baseUri = null)
    {
        if (string.IsNullOrWhiteSpace(link) || link.Length > 4096) return null;
        Uri? uri;
        if (baseUri is not null)
        {
            if (!Uri.TryCreate(baseUri, link.Trim(), out uri)) return null;
        }
        else if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Host.Length == 0) return null;
        return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
    }

    private static ReadingArticle[] SelectArticles(ReadingArticle[] pool, DateOnly today, ReadingCache? previous)
    {
        var previousUrls = (previous?.Articles ?? []).Select(a => a.Url).ToHashSet(StringComparer.Ordinal);
        var selected = new List<ReadingArticle>(3);
        foreach (var category in new[] { ReadingCategory.General, ReadingCategory.Technology })
        {
            var needed = category == ReadingCategory.General ? 2 : 1;
            // A refresh can improve the pool without reshuffling today's already chosen articles.
            if (previous?.Day == today)
                selected.AddRange(previous.Articles.Where(a => a.Category == category)
                    .Select(a => pool.FirstOrDefault(candidate => candidate.Url == a.Url)).OfType<ReadingArticle>().Take(needed));
            var candidates = pool.Where(a => a.Category == category && !selected.Any(chosen => chosen.Url == a.Url))
                .OrderBy(a => previousUrls.Contains(a.Url))
                .ThenBy(a => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{today:yyyy-MM-dd}:{a.Url}"))), StringComparer.Ordinal)
                .ToList();
            while (selected.Count(a => a.Category == category) < needed && candidates.Count > 0)
            {
                var first = candidates[0];
                var next = candidates.FirstOrDefault(a => previousUrls.Contains(a.Url) == previousUrls.Contains(first.Url)
                    && !selected.Any(chosen => chosen.SourceName == a.SourceName)) ?? first;
                selected.Add(next);
                candidates.Remove(next);
            }
        }
        return selected.ToArray();
    }

    private async Task<ReadingCache?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var file = File.OpenRead(Path.Combine(cacheDirectory, "daily-reading.json"));
            using var bounded = await ReadBoundedAsync(file, cancellationToken);
            var cache = await JsonSerializer.DeserializeAsync<ReadingCache>(bounded, cancellationToken: cancellationToken);
            if (cache is null || cache.Version != 1 || cache.Articles is null || cache.Pool is null
                || cache.Articles.Length is < 1 or > 3 || cache.Pool.Length > MaxPoolSize) return null;
            var articles = cache.Articles.Select(ValidateCachedArticle).OfType<ReadingArticle>().DistinctBy(a => a.Url).ToArray();
            if (articles.Length != cache.Articles.Length || articles.Count(a => a.Category == ReadingCategory.General) > 2
                || articles.Count(a => a.Category == ReadingCategory.Technology) > 1) return null;
            var pool = articles.Concat(cache.Pool.Select(ValidateCachedArticle).OfType<ReadingArticle>()).DistinctBy(a => a.Url).Take(MaxPoolSize).ToArray();
            return memoryCache = cache with { Articles = articles, Pool = pool };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or RegexMatchTimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static ReadingArticle? ValidateCachedArticle(ReadingArticle? article)
    {
        if (article is null || !Enum.IsDefined(article.Category)) return null;
        var url = SafeUrl(article.Url);
        var title = CleanText(article.Title, 200);
        if (url is null || title.Length == 0) return null;
        return article with { Url = url, Title = title, Summary = CleanText(article.Summary, 600), SourceName = CleanText(article.SourceName, 80) };
    }

    private async Task<bool> WriteCacheAsync(ReadingCache cache, CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            temporaryPath = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, cache, cancellationToken: cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, Path.Combine(cacheDirectory, "daily-reading.json"), true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<MemoryStream> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = new MemoryStream();
        try
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (result.Length + read > MaxResponseBytes) throw new InvalidDataException("Reading data is too large.");
                result.Write(buffer, 0, read);
            }
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private sealed record ReadingCache(int Version, DateOnly Day, ReadingArticle[] Articles, ReadingArticle[] Pool);
    private sealed record FeedResult(ReadingArticle[] Articles, bool Failed);
}
