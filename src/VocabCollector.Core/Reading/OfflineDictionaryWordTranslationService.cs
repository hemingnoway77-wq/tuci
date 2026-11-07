namespace VocabCollector.Core.Reading;

public sealed class OfflineDictionaryWordTranslationService : IWordTranslationService, IDisposable
{
    private readonly IOfflineWordDictionary _dictionary;
    private readonly IWordTranslationService _fallback;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<WordTranslation>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<WordTranslation> _recency = new();
    private bool _disposed;

    public OfflineDictionaryWordTranslationService(IOfflineWordDictionary dictionary, IWordTranslationService fallback)
    {
        ArgumentNullException.ThrowIfNull(dictionary); ArgumentNullException.ThrowIfNull(fallback);
        _dictionary = dictionary; _fallback = fallback;
    }

    public async Task<WordTranslation> TranslateAsync(string word, CancellationToken ct)
    {
        lock (_sync) ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        var target = WordInput.Normalize(word);
        lock (_sync)
        {
            ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
            if (_cache.TryGetValue(target, out var cached))
            {
                _recency.Remove(cached); _recency.AddLast(cached);
                return cached.Value with { IsFromCache = true };
            }
        }
        try
        {
            var result = await _dictionary.LookupAsync(target, ct).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result is null || string.IsNullOrWhiteSpace(result.TranslatedText))
                result = await _fallback.TranslateAsync(target, ct).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.Word != target) result = result with { Word = target };
            // IReadOnlyList alone does not prevent its original owner mutating it.
            // Cache a frozen snapshot of all details rather than only the summary.
            if (result.Meanings is not null) result = result with { Meanings = Array.AsReadOnly(result.Meanings.ToArray()) };
            lock (_sync)
            {
                ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
                if (_cache.Remove(target, out var old)) _recency.Remove(old);
                _cache[target] = _recency.AddLast(result);
                if (_cache.Count > 128)
                {
                    _cache.Remove(_recency.First!.Value.Word); _recency.RemoveFirst();
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }
        catch (WordTranslationException) { ct.ThrowIfCancellationRequested(); throw; }
        catch (Exception error)
        {
            ct.ThrowIfCancellationRequested(); lock (_sync) ThrowIfDisposed();
            throw new WordTranslationException("离线单词详情暂时不可用，请重试。", error);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _cache.Clear(); _recency.Clear();
        }
        try { _dictionary.Dispose(); }
        finally { if (_fallback is IDisposable owned && !ReferenceEquals(_dictionary, owned)) owned.Dispose(); }
    }
}
