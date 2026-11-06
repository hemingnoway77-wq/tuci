using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VocabCollector.Core.Reading;

public sealed class ArticleContentService : IArticleContentService, IDisposable
{
    private const int MaxResponseBytes = 2 * 1024 * 1024, MaxCacheBytes = 1024 * 1024, MaxCacheEntries = 80, MaxRedirects = 3;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private readonly HttpClient _client;
    private readonly string _cacheDirectory;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    public ArticleContentService(string cacheDirectory, TimeProvider? clock = null)
        : this(new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        }) { Timeout = Timeout.InfiniteTimeSpan }, cacheDirectory, clock)
    {
        _ownsClient = true;
    }

    /// <summary>The injected client's handler must disable automatic redirects and cookies.</summary>
    public ArticleContentService(HttpClient client, string cacheDirectory, TimeProvider? clock = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public static bool IsAllowed(string? url) => !string.IsNullOrWhiteSpace(url) && url == url.Trim() &&
        !url.Any(character => char.IsControl(character) || character == '\\') &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowed(uri);

    public static bool IsAllowed(Uri uri) => uri.IsAbsoluteUri && uri.IsWellFormedOriginalString() &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        !uri.GetLeftPart(UriPartial.Authority).Contains('@') && Family(uri) != "";

    private static string Family(Uri uri) => uri.IdnHost.ToLowerInvariant() switch
    {
        "www.sciencedaily.com" => "science",
        "blog.cloudflare.com" => "cloudflare",
        "www.bbc.com" or "www.bbc.co.uk" => "bbc",
        _ => ""
    };

    public async Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(article);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAllowed(article.Url)) throw new ArticleContentException("此文章地址不支持纯净阅读。仅支持 ScienceDaily、Cloudflare Blog 和 BBC 的安全 HTTPS 来源。");
        var original = new UriBuilder(article.Url) { Fragment = "" }.Uri;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original.AbsoluteUri)));
        var cachePath = Path.Combine(_cacheDirectory, "article-" + key + ".json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(_timeout);
        CacheEntry? cached = null;
        var entered = false;
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            cached = await ReadCacheAsync(cachePath, original, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (!forceRefresh && cached != null && _clock.GetUtcNow() - cached.FetchedAt <= CacheLifetime)
                return Content(cached, fromCache: true, offline: false, "全文已从本地缓存加载。");

            var (html, finalUri) = await FetchAsync(original, deadline.Token).ConfigureAwait(false);
            var paragraphs = await ArticleExtractor.ExtractAsync(html, finalUri, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var fresh = new CacheEntry(1, original.AbsoluteUri, _clock.GetUtcNow(), paragraphs.ToArray());
            var saved = await WriteCacheAsync(cachePath, fresh, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return Content(fresh, fromCache: false, offline: false, saved ? "文章全文已准备好。" : "文章全文已获取，本次未能保存本地缓存。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
        catch (OperationCanceledException error)
        {
            return OfflineOrThrow(new ArticleContentException("获取文章全文超时，请检查网络后重试。", error));
        }
        catch (ArticleContentException error) { return OfflineOrThrow(error); }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return OfflineOrThrow(new ArticleContentException("来源暂时无法提供文章全文，请检查网络后重试。", error));
        }
        finally { if (entered) _gate.Release(); }

        ArticleContent Content(CacheEntry entry, bool fromCache, bool offline, string message) =>
            new(article, Array.AsReadOnly(entry.Paragraphs), entry.FetchedAt, fromCache, offline, message);
        ArticleContent OfflineOrThrow(ArticleContentException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cached != null) return Content(cached, fromCache: true, offline: true, "来源暂不可用，正在显示已缓存的文章全文。");
            throw error;
        }
    }

    private async Task<(string Html, Uri FinalUri)> FetchAsync(Uri original, CancellationToken token)
    {
        var current = original;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            token.ThrowIfCancellationRequested();
            if (!IsAllowed(current) || Family(current) != Family(original)) throw new ArticleContentException("文章跳转地址不安全或已离开原阅读来源，已停止获取全文。");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Tuci/1.0 (+daily-reading)");
            request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var responseUri = response.RequestMessage?.RequestUri ?? current;
            if (!IsAllowed(responseUri) || Family(responseUri) != Family(original) || responseUri != current)
                throw new ArticleContentException("文章请求发生了未经验证的自动跳转，已停止获取全文。");
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirect == MaxRedirects || response.Headers.Location is not { } location)
                    throw new ArticleContentException("文章来源跳转次数过多或缺少有效地址，请稍后重试。");
                if (!Uri.TryCreate(current, location, out var next) || !IsAllowed(next) || Family(next) != Family(original))
                    throw new ArticleContentException("文章跳转地址不安全或已离开原阅读来源，已停止获取全文。");
                current = new UriBuilder(next) { Fragment = "" }.Uri;
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new ArticleContentException($"文章来源返回 HTTP {(int)response.StatusCode}，暂时无法获取全文，请稍后重试。");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) && !string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                throw new ArticleContentException("文章来源未返回 HTML 正文，无法作为文章全文打开。");
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new ArticleContentException("文章页面超过 2 MiB，已停止下载。");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var body = await ReadBoundedAsync(stream, MaxResponseBytes, token).ConfigureAwait(false);
            var encoding = Encoding.UTF8;
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try { encoding = Encoding.GetEncoding(charset); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException)
                { throw new ArticleContentException("文章来源使用了暂不支持的文本编码，无法可靠读取全文。", error); }
            }
            using var reader = new StreamReader(body, encoding, detectEncodingFromByteOrderMarks: true);
            return (await reader.ReadToEndAsync(token).ConfigureAwait(false), current);
        }
        throw new ArticleContentException("文章来源跳转次数过多，请稍后重试。");
    }

    private async Task<CacheEntry?> ReadCacheAsync(string path, Uri original, CancellationToken token)
    {
        try
        {
            EnsureNoLinks(path);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            using var body = await ReadBoundedAsync(file, MaxCacheBytes, token).ConfigureAwait(false);
            var entry = await JsonSerializer.DeserializeAsync<CacheEntry>(body, cancellationToken: token).ConfigureAwait(false);
            return entry is { Version: 1, Paragraphs: not null } && entry.Url == original.AbsoluteUri &&
                entry.FetchedAt >= DateTimeOffset.UnixEpoch && entry.FetchedAt <= _clock.GetUtcNow().AddMinutes(5) &&
                ArticleExtractor.IsValidBody(entry.Paragraphs) ? entry : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private async Task<bool> WriteCacheAsync(string path, CacheEntry entry, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            EnsureNoLinks(path);
            Directory.CreateDirectory(_cacheDirectory);
            EnsureNoLinks(path);
            temporary = Path.Combine(_cacheDirectory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(file, entry, cancellationToken: token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            EnsureNoLinks(path);
            File.Move(temporary, path, overwrite: true);
            var caches = Directory.EnumerateFiles(_cacheDirectory, "article-*.json")
                .Where(file => Path.GetFileName(file) is { Length: 77 } name && name[8..72].All(Uri.IsHexDigit) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
            foreach (var expired in caches.Skip(MaxCacheEntries)) { token.ThrowIfCancellationRequested(); File.Delete(expired); }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("浏览器正文缓存路径包含文件或目录链接。");
    }

    private static async Task<MemoryStream> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        var result = new MemoryStream();
        try
        {
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + read > limit) throw new ArticleContentException("文章或缓存内容过大，已停止读取。");
                result.Write(buffer, 0, read);
            }
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_ownsClient) _client.Dispose();
    }

    private sealed record CacheEntry(int Version, string Url, DateTimeOffset FetchedAt, string[] Paragraphs);
}
