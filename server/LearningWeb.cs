using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;

namespace Tuci.Sync;

internal static class LearningWeb
{
    private record Feed(string Source, string Category, string Url, string[] Hosts);
    private record Story(string Id, string Title, string Summary, string Source, string Category, Uri Url, DateTimeOffset? Published);
    private record Edition(Story[] Stories, DateTimeOffset FetchedAt);
    private static readonly Feed[] Feeds =
    [
        new("ScienceDaily · Society", "general", "https://www.sciencedaily.com/rss/top/society.xml", ["www.sciencedaily.com"]),
        new("ScienceDaily · Environment", "general", "https://www.sciencedaily.com/rss/earth_climate/environmental_science.xml", ["www.sciencedaily.com"]),
        new("Cloudflare Blog", "technology", "https://blog.cloudflare.com/rss/", ["blog.cloudflare.com"]),
        new("BBC · Technology", "technology", "https://feeds.bbci.co.uk/news/technology/rss.xml", ["www.bbc.co.uk", "www.bbc.com"])
    ];
    private static readonly HttpClient Client = new(new HttpClientHandler {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private static readonly SemaphoreSlim FeedLock = new(1);
    private static readonly SemaphoreSlim ArticleSlots = new(4);
    private static readonly MemoryCache Bodies = new(new MemoryCacheOptions { SizeLimit = 80 });
    private static Edition edition = new([], DateTimeOffset.MinValue);
    private static DateTimeOffset nextFetch;
    private static readonly TimeSpan RegexDeadline = TimeSpan.FromMilliseconds(400);

    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/reading", GetEdition).RequireRateLimiting("reading");
        app.MapGet("/v1/reading/{id}", GetBody).RequireRateLimiting("reading");
    }

    private static async Task<Edition> GetEdition()
    {
        await FeedLock.WaitAsync();
        try
        {
            if (DateTimeOffset.UtcNow >= nextFetch)
            {
                var results = await Task.WhenAll(Feeds.Select(ReadFeed));
                var fresh = results.SelectMany(items => items).DistinctBy(item => item.Id).ToArray();
                var now = DateTimeOffset.UtcNow;
                if (fresh.Length > 0)
                    edition = new(fresh.Concat(edition.Stories).DistinctBy(item => item.Id).Take(240).ToArray(), now);
                nextFetch = now.AddMinutes(fresh.Length == 0 ? 5 : 45);
            }
            return edition;
        }
        finally { FeedLock.Release(); }
    }

    private static async Task<IResult> GetBody(string id)
    {
        // Only IDs issued by the configured feeds are addressable; callers never supply a URL.
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) return Results.NotFound();
        if (Bodies.TryGetValue(id, out string[]? cached) && cached != null)
            return Results.Ok(new { paragraphs = cached });
        var story = (await GetEdition()).Stories.FirstOrDefault(s => s.Id == id);
        if (story == null) return Results.NotFound();
        if (!await ArticleSlots.WaitAsync(0)) return Results.StatusCode(429);
        try
        {
            var feed = Feeds.First(f => f.Source == story.Source);
            var html = await Fetch(story.Url, feed.Hosts);
            var paragraphs = ExtractParagraphs(html, story.Url.Host);
            if (paragraphs.Length == 0) return Results.StatusCode(502);
            Bodies.Set(id, paragraphs, new MemoryCacheEntryOptions().SetSize(1).SetAbsoluteExpiration(TimeSpan.FromHours(12)));
            return Results.Ok(new { paragraphs });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or RegexMatchTimeoutException)
        { return Results.StatusCode(502); }
        finally { ArticleSlots.Release(); }
    }

    private static async Task<Story[]> ReadFeed(Feed feed)
    {
        try
        {
            var feedUri = new Uri(feed.Url);
            var xml = await Fetch(feedUri, [feedUri.Host]);
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024
            });
            var doc = XDocument.Load(reader);
            var result = new List<Story>();
            foreach (var item in doc.Descendants().Where(n => n.Name.LocalName is "item" or "entry").Take(60))
            {
                XElement? Field(string key) => item.Elements().FirstOrDefault(e => e.Name.LocalName == key);
                var link = Field("link");
                if (!Uri.TryCreate(feedUri, link?.Attribute("href")?.Value ?? link?.Value, out var uri) || !Allowed(uri, feed.Hosts)) continue;
                var title = Clean(Field("title")?.Value ?? "", 240);
                if (title.Length == 0) continue;
                var summary = Clean((Field("description") ?? Field("summary"))?.Value ?? "", 600);
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant()[..32];
                DateTimeOffset? date = DateTimeOffset.TryParse((Field("pubDate") ?? Field("updated"))?.Value, out var dt) ? dt : null;
                result.Add(new(id, title, summary, feed.Source, feed.Category, uri, date));
            }
            return result.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or XmlException or InvalidDataException or RegexMatchTimeoutException)
        { return []; }
    }

    private static bool Allowed(Uri uri, string[] hosts) =>
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo == "" &&
        hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    private static async Task<string> Fetch(Uri uri, string[] hosts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        for (var i = 0; i < 3 && Allowed(uri, hosts); i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("Tuci/1.0 (+daily-reading)");
            using var res = await Client.SendAsync(req, timeout.Token);
            if ((int)res.StatusCode is >= 300 and < 400 && res.Headers.Location is { } to)
            {
                uri = new Uri(uri, to);
                continue;
            }
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(timeout.Token);
        }
        throw new InvalidDataException("Source unavailable");
    }

    private static string Clean(string text, int limit)
    {
        var plain = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]{1,4000}>", " ", RegexOptions.None, RegexDeadline));
        plain = Regex.Replace(plain, @"\s+", " ", RegexOptions.None, RegexDeadline).Trim();
        return plain.Length > limit ? plain[..limit] + "…" : plain;
    }

    private static readonly Regex DivTag = new(@"</?div\b[^>]{0,4000}>", RegexOptions.IgnoreCase, RegexDeadline);
    private static int DivEnd(string html, int from)
    {
        var depth = 1;
        for (var tag = DivTag.Match(html, from); tag.Success; tag = tag.NextMatch())
        {
            depth += tag.Value[1] == '/' ? -1 : 1;
            if (depth == 0) return tag.Index;
        }
        return -1;
    }

    private static string[] ExtractParagraphs(string html, string host)
    {
        html = Regex.Replace(html, @"<(script|style|nav|footer|aside)\b[^>]*>[\s\S]*?</\1\s*>", "", RegexOptions.IgnoreCase, RegexDeadline);
        // Each publisher uses an identifiable body container. Exclude navigation,
        // newsletter forms, video controls and related-story cards from the reader.
        var marker = host.Contains("sciencedaily", StringComparison.Ordinal)
            ? """<div\b[^>]*\bid=["']story_text["'][^>]*>"""
            : host.Contains("cloudflare", StringComparison.Ordinal)
                ? """<div\b[^>]*class=["'][^"']*\barticle-content\b[^"']*["'][^>]*>"""
                : """<div\b[^>]*class=["'][^"']*RichTextContainer[^"']*["'][^>]*>""";
        var fragments = new List<string>();
        foreach (Match start in Regex.Matches(html, marker, RegexOptions.IgnoreCase, RegexDeadline))
        {
            var from = start.Index + start.Length;
            var end = DivEnd(html, from);
            if (end > from) fragments.Add(html[from..end]);
            if (fragments.Count >= 140) break;
        }
        var body = string.Join("\n", fragments);
        var lines = new List<string>();
        var length = 0;
        foreach (Match m in Regex.Matches(body, @"<p(?:\s[^>]{0,3000})?>([\s\S]*?)</p\s*>", RegexOptions.IgnoreCase, RegexDeadline))
        {
            var line = Clean(m.Groups[1].Value, 6000);
            if (line.Length < 12) continue;
            lines.Add(line);
            length += line.Length;
            if (length > 90000 || lines.Count >= 140) break;
        }
        return lines.ToArray();
    }
}
