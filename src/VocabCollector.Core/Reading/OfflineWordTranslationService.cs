namespace VocabCollector.Core.Reading;

/// <summary>Validates selected words and caches successful translations from an owned local engine.</summary>
public sealed class OfflineWordTranslationService : IWordTranslationService, IDisposable
{
    private const int CacheLimit = 128;
    private const int MaxTranslationLength = 2000;
    private readonly IOfflineTranslationEngine _engine;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedListNode<WordTranslation>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<WordTranslation> _recency = new();
    private bool _disposed;

    public OfflineWordTranslationService(IOfflineTranslationEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public async Task<WordTranslation> TranslateAsync(string word, CancellationToken ct)
    {
        lock (_sync) ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        var target = WordInput.Normalize(word);
        lock (_sync)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            if (_cache.TryGetValue(target, out var cached))
            {
                _recency.Remove(cached); _recency.AddLast(cached);
                return cached.Value with { IsFromCache = true };
            }
        }

        try
        {
            // The engine controls inference concurrency. A caller may stop waiting
            // even when a native worker cannot interrupt its current calculation.
            var output = await _engine.TranslateAsync(target, ct).WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var result = new WordTranslation(target, CleanTranslation(output));
            lock (_sync)
            {
                ThrowIfDisposed();
                ct.ThrowIfCancellationRequested();
                if (_cache.Remove(target, out var previous)) _recency.Remove(previous);
                _cache[target] = _recency.AddLast(result);
                if (_cache.Count > CacheLimit)
                {
                    var oldest = _recency.First!;
                    _cache.Remove(oldest.Value.Word); _recency.RemoveFirst();
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }
        catch (WordTranslationException) { ct.ThrowIfCancellationRequested(); throw; }
        catch (Exception error)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync) ThrowIfDisposed();
            throw new WordTranslationException("离线翻译暂时不可用，请稍后重试。", error);
        }
    }

    private static string CleanTranslation(string? output)
    {
        if (output is null || output.Length > MaxTranslationLength) throw InvalidResult();
        var clean = string.Concat(output.Select(character => character < ' ' || character == '\u007f' ? ' ' : character)).Trim();
        return clean.Length > 0 ? clean : throw InvalidResult();
    }

    private static WordTranslationException InvalidResult() => new("离线翻译未返回有效结果，请重试。");
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear(); _recency.Clear();
        }
        _engine.Dispose();
    }
}
