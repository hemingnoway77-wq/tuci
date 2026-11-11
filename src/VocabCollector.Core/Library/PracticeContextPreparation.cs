using System.Collections.Concurrent;

namespace VocabCollector.Core.Library;

public sealed class PracticeContextPreparation
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate;
    private readonly PracticeContextCache _cache;
    private readonly IPracticeContextGenerator _generator;

    public PracticeContextPreparation(string cachePath, IPracticeContextGenerator generator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ArgumentNullException.ThrowIfNull(generator);
        var path = Path.GetFullPath(cachePath);
        _gate = Gates.GetOrAdd(path, static _ => new(1, 1));
        _cache = new(path); _generator = generator;
    }

    public async Task<IReadOnlyList<PracticeContext>> PrepareAsync(string word, IReadOnlyList<PracticeSense> senses,
        ApiOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (target, snapshot) = PracticeContextInput.Snapshot(word, senses);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read after waiting: a concurrent automatic/manual request may
            // already have supplied the entire bank. A cache hit needs no API.
            var cached = await _cache.ReadAsync(target, snapshot, ct).ConfigureAwait(false);
            if (cached.Count > 0) return cached;
            var generated = await _generator.GenerateAsync(target, Array.AsReadOnly(snapshot), options, ct)
                .WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var validated = PracticeContextInput.Validate(snapshot, generated);
            await _cache.SaveAsync(target, snapshot, validated, ct).ConfigureAwait(false);
            return validated;
        }
        finally { _gate.Release(); }
    }
}
