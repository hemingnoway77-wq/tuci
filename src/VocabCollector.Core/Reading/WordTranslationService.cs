using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VocabCollector.Core.Reading;

public sealed class MyMemoryWordTranslationService : IWordTranslationService, IDisposable
{
    private const int MaxResponseBytes = 256 * 1024;
    private const int CacheLimit = 128;
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    private readonly bool _ownsClient;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<WordTranslation>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<WordTranslation> _recency = new();
    private int _disposed;

    public MyMemoryWordTranslationService() : this(CreateClient(), true, null) { }
    public MyMemoryWordTranslationService(HttpClient client, TimeSpan? timeout = null) : this(client, false, timeout) { }

    private MyMemoryWordTranslationService(HttpClient client, bool ownsClient, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout), "翻译超时必须是有效的正时长。");
        _client = client;
        _ownsClient = ownsClient;
    }

    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxResponseBytes };

    public async Task<WordTranslation> TranslateAsync(string word, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        var target = WordInput.Normalize(word);
        lock (_cacheLock)
        {
            ct.ThrowIfCancellationRequested();
            if (_cache.TryGetValue(target, out var cached))
            {
                _recency.Remove(cached); _recency.AddLast(cached);
                return cached.Value with { IsFromCache = true };
            }
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            // Official GET contract: https://mymemory.translated.net/doc/spec.php
            // Only the selected word and language pair leave the process; AI settings are unrelated.
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(target) + "&langpair=en%7Czh-CN";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden) throw QuotaError();
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new WordTranslationException("免费翻译服务返回了不允许的跳转，请稍后重试。");
            if (!response.IsSuccessStatusCode) throw new WordTranslationException("免费翻译服务暂时不可用，请稍后重试。");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new WordTranslationException("免费翻译返回的数据过大，请稍后重试。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (body.Length + count > MaxResponseBytes)
                    throw new WordTranslationException("免费翻译返回的数据过大，请稍后重试。");
                body.Write(buffer, 0, count);
            }
            deadline.Token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)), new JsonDocumentOptions { MaxDepth = 32 });
            var text = ParseTranslation(document.RootElement);
            deadline.Token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var result = new WordTranslation(target, text);
            lock (_cacheLock)
            {
                deadline.Token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (_cache.Remove(target, out var old)) _recency.Remove(old);
                _cache[target] = _recency.AddLast(result);
                if (_cache.Count > CacheLimit)
                {
                    var first = _recency.First!;
                    _cache.Remove(first.Value.Word); _recency.RemoveFirst();
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }
        catch (OperationCanceledException error) { throw new WordTranslationException("免费翻译请求超时，请稍后重试。", error); }
        catch (WordTranslationException) { throw; }
        catch (JsonException error) { throw new WordTranslationException("免费翻译返回的数据无效，请稍后重试。", error); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            throw new WordTranslationException("免费翻译服务暂时不可用，请检查网络后重试。", error);
        }
    }

    private static string ParseTranslation(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var quotaFinished = false;
        if (root.TryGetProperty("quotaFinished", out var quota))
        {
            if (quota.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidResponse();
            quotaFinished = quota.GetBoolean();
        }
        if (!root.TryGetProperty("responseStatus", out var status) || status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code))
            throw InvalidResponse();
        if (quotaFinished || code is 403 or 429) throw QuotaError();
        if (code != 200 || !root.TryGetProperty("responseData", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("translatedText", out var text) || text.ValueKind != JsonValueKind.String)
            throw InvalidResponse();
        var raw = text.GetString()!;
        if (raw.TrimStart().StartsWith("MYMEMORY WARNING:", StringComparison.OrdinalIgnoreCase)) throw QuotaError();
        if (raw.Length > 2000) throw InvalidResponse();
        var clean = string.Concat(raw.Select(character => character < ' ' || character == '\u007f' ? ' ' : character)).Trim();
        return clean.Length > 0 ? clean : throw InvalidResponse();
    }

    private static WordTranslationException InvalidResponse() => new("免费翻译返回的数据无效，请稍后重试。");
    private static WordTranslationException QuotaError() => new("免费翻译服务额度已用完或请求受限，请稍后重试。");
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsClient) _client.Dispose();
        lock (_cacheLock) { _cache.Clear(); _recency.Clear(); }
    }
}
