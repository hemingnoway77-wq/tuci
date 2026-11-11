using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace VocabCollector.Core.Library;

public sealed class PracticeContextCache
{
    private const int MaxFileBytes = 32 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    public PracticeContextCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); _path = Path.GetFullPath(path);
        _gate = Gates.GetOrAdd(_path, static _ => new(1, 1));
    }

    public async Task<IReadOnlyList<PracticeContext>> ReadAsync(string word, IReadOnlyList<PracticeSense> senses, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (target, snapshot) = PracticeContextInput.Snapshot(word, senses);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return state.Words.TryGetValue(target, out var cached) && cached.SenseHash == Fingerprint(snapshot)
                ? Array.AsReadOnly(cached.Contexts.ToArray()) : [];
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string word, IReadOnlyList<PracticeSense> senses, IReadOnlyList<PracticeContext> contexts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (target, snapshot) = PracticeContextInput.Snapshot(word, senses);
        var validated = PracticeContextInput.Validate(snapshot, contexts);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            var words = new Dictionary<string, Entry>(state.Words, StringComparer.Ordinal)
            { [target] = new(Fingerprint(snapshot), snapshot, validated.ToArray()) };
            var candidate = new CacheState(1, words);
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, LocalLibraryState.JsonOptions).Length > MaxFileBytes)
                throw new IOException("语境缓存已满，原缓存已保留。");
            await LocalLibraryState.WriteAsync(_path, candidate, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (LocalLibraryState.IsStorageError(error))
        { throw new IOException("无法保存本机语境缓存，原文件已保留。", error); }
        finally { _gate.Release(); }
    }

    public async Task ImportMissingAsync(string word, IReadOnlyList<PracticeSense> senses, IReadOnlyList<PracticeContext> contexts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (target, snapshot) = PracticeContextInput.Snapshot(word, senses);
        ArgumentNullException.ThrowIfNull(contexts); var incoming = contexts.ToArray();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Validate the entire incoming bank even when a local bank will take precedence.
            var validated = PracticeContextInput.Validate(snapshot, incoming);
            var state = await LoadAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var fingerprint = Fingerprint(snapshot);
            if (state.Words.TryGetValue(target, out var current) && current.SenseHash == fingerprint) return;
            var words = new Dictionary<string, Entry>(state.Words, StringComparer.Ordinal)
            { [target] = new(fingerprint, snapshot, validated.ToArray()) };
            var candidate = new CacheState(1, words);
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, LocalLibraryState.JsonOptions).Length > MaxFileBytes)
                throw new IOException("语境缓存已满，原缓存已保留。");
            await LocalLibraryState.WriteAsync(_path, candidate, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (LocalLibraryState.IsStorageError(error))
        { throw new IOException("无法导入本机语境缓存，原文件已保留。", error); }
        finally { _gate.Release(); }
    }

    private async Task<CacheState> LoadAsync(CancellationToken ct)
    {
        try
        {
            LocalLibraryState.EnsureNoLinks(_path);
            FileStream stream;
            try { stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(1, new(StringComparer.Ordinal)); }
            await using (stream)
            {
                if (stream.Length > MaxFileBytes) throw new InvalidDataException();
                var state = await JsonSerializer.DeserializeAsync<CacheState>(stream, new JsonSerializerOptions { MaxDepth = 16 }, ct).ConfigureAwait(false);
                if (state is null || state.FormatVersion != 1 || state.Words is null) throw new InvalidDataException();
                foreach (var (word, entry) in state.Words)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry is null || entry.Senses is null || entry.Contexts is null || WordInput.Normalize(word) != word ||
                        entry.SenseHash is null || entry.SenseHash.Length != 64 || !entry.SenseHash.All(Uri.IsHexDigit)) throw new InvalidDataException();
                    var (_, senses) = PracticeContextInput.Snapshot(word, entry.Senses);
                    if (Fingerprint(senses) != entry.SenseHash) throw new InvalidDataException();
                    _ = PracticeContextInput.Validate(senses, entry.Contexts);
                }
                return state;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        { throw new IOException("本机语境缓存格式无效，原文件已保留。", error); }
        catch (Exception error) when (LocalLibraryState.IsStorageError(error))
        { throw new IOException("无法读取本机语境缓存，原文件已保留。", error); }
    }

    private static string Fingerprint(IReadOnlyList<PracticeSense> senses) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(senses.OrderBy(sense => sense.Id, StringComparer.Ordinal).ToArray())));
    private sealed record Entry(string SenseHash, PracticeSense[] Senses, PracticeContext[] Contexts);
    private sealed record CacheState(int FormatVersion, Dictionary<string, Entry> Words);
}
