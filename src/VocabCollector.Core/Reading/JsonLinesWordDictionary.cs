using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VocabCollector.Core.Reading;

public sealed class JsonLinesWordDictionary : IOfflineWordDictionary
{
    private const string Tags = "(?:n|v|vt|vi|a|adj|ad|adv|prep|pron|art|conj|interj|int|aux|num|phr)";
    private static readonly Regex PartOfSpeech = new("^\\s*(?<pos>" + Tags + "\\.(?:\\s*[&/,]\\s*" + Tags + "\\.)*)\\s*(?<definition>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    private readonly string _directory;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _loads = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<char, LinkedListNode<Shard>> _cache = new();
    private readonly LinkedList<Shard> _recency = new();
    private Dictionary<string, Asset>? _files;
    private bool _disposed;
    private sealed record Asset(string Path, long Bytes, byte[] Hash);
    private sealed record Shard(char Letter, IReadOnlyDictionary<string, WordTranslation> Words);

    public JsonLinesWordDictionary(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public async Task<WordTranslation?> LookupAsync(string word, CancellationToken ct)
    {
        lock (_sync) ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        var target = WordInput.Normalize(word);
        var letter = target[0];
        lock (_sync)
        {
            ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
            if (Cached(letter) is { } words) return words.GetValueOrDefault(target);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var entered = false;
        try
        {
            await _loads.WaitAsync(cancellation.Token).ConfigureAwait(false); entered = true;
            lock (_sync)
            {
                ThrowIfDisposed(); cancellation.Token.ThrowIfCancellationRequested();
                if (Cached(letter) is { } words) return words.GetValueOrDefault(target);
            }
            // Parsing a whole letter and hashing the package must never consume
            // the WPF caller's dispatcher, including synchronous file-cache hits.
            var loaded = await Task.Run(async () =>
            {
                Dictionary<string, Asset>? files;
                lock (_sync) files = _files;
                if (files is null)
                {
                    files = await VerifyManifestAsync(cancellation.Token).ConfigureAwait(false);
                    lock (_sync) { ThrowIfDisposed(); cancellation.Token.ThrowIfCancellationRequested(); _files = files; }
                }
                return await ReadShardAsync(letter, files[letter + ".jsonl"], cancellation.Token).ConfigureAwait(false);
            }, cancellation.Token).ConfigureAwait(false);
            lock (_sync)
            {
                ThrowIfDisposed(); cancellation.Token.ThrowIfCancellationRequested();
                _cache[letter] = _recency.AddLast(new Shard(letter, loaded));
                if (_cache.Count > 3) { _cache.Remove(_recency.First!.Value.Letter); _recency.RemoveFirst(); }
            }
            return loaded.GetValueOrDefault(target);
        }
        catch (OperationCanceledException)
        {
            lock (_sync) ThrowIfDisposed();
            ct.ThrowIfCancellationRequested(); throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            lock (_sync)
            {
                ThrowIfDisposed(); _files = null; _cache.Clear(); _recency.Clear();
            }
            ct.ThrowIfCancellationRequested();
            throw new WordTranslationException("离线词典包缺失或损坏，请使用完整应用包重新解压。", error);
        }
        finally { if (entered) _loads.Release(); }
    }

    private IReadOnlyDictionary<string, WordTranslation>? Cached(char letter)
    {
        if (!_cache.TryGetValue(letter, out var cached)) return null;
        _recency.Remove(cached); _recency.AddLast(cached); return cached.Value.Words;
    }

    private async Task<Dictionary<string, Asset>> VerifyManifestAsync(CancellationToken ct)
    {
        var manifestPath = Resolve("dictionary.manifest.json");
        await using var stream = Open(manifestPath);
        if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException();
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formatVersion", out var version) ||
            !version.TryGetInt32(out var format) || format != 1 || !root.TryGetProperty("files", out var files) ||
            files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 1 or > 128) throw new InvalidDataException();
        var result = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                !file.TryGetProperty("bytes", out var size) || !size.TryGetInt64(out var bytes) || bytes < 0 || bytes > 128 * 1024 * 1024 ||
                !file.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String) throw new InvalidDataException();
            var name = path.GetString()!; var hexadecimal = hash.GetString()!;
            if (hexadecimal.Length != 64 || !hexadecimal.All(Uri.IsHexDigit)) throw new InvalidDataException();
            var asset = new Asset(Resolve(name), bytes, Convert.FromHexString(hexadecimal));
            if (!result.TryAdd(name.Replace('\\', '/'), asset)) throw new InvalidDataException();
            await using var content = Open(asset.Path);
            await VerifyFileAsync(content, asset, ct).ConfigureAwait(false);
        }
        foreach (var letter in "abcdefghijklmnopqrstuvwxyz") if (!result.ContainsKey(letter + ".jsonl")) throw new InvalidDataException();
        return result;
    }

    private async Task<IReadOnlyDictionary<string, WordTranslation>> ReadShardAsync(char letter, Asset asset, CancellationToken ct)
    {
        // Keep the same read-only handle open from verification through parsing,
        // so a package update cannot replace its bytes between those operations.
        await using var stream = Open(Resolve(letter + ".jsonl"));
        await VerifyFileAsync(stream, asset, ct).ConfigureAwait(false); stream.Position = 0;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 8192, leaveOpen: true);
        var result = new Dictionary<string, WordTranslation>(StringComparer.Ordinal);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            if (line.Length > 1024 * 1024) throw new InvalidDataException();
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
            var row = document.RootElement;
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 4 || row.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                throw new InvalidDataException();
            var word = row[0].GetString()!;
            if (WordInput.Normalize(word) != word || word[0] != letter || result.ContainsKey(word)) throw new InvalidDataException();
            var translation = row[2].GetString()!;
            if (string.IsNullOrWhiteSpace(translation)) continue;
            var phonetic = row[1].GetString()!.Trim();
            var meanings = translation.Split('\n').Where(value => !string.IsNullOrWhiteSpace(value)).Select(value =>
            {
                var match = PartOfSpeech.Match(value.Trim());
                return match.Success ? new WordMeaning(match.Groups["pos"].Value.Trim(), match.Groups["definition"].Value.Trim())
                    : new WordMeaning("", value.Trim());
            }).ToArray();
            result.Add(word, new WordTranslation(word, translation)
            { Phonetic = phonetic.Length == 0 ? null : phonetic, Meanings = Array.AsReadOnly(meanings), Lemma = Lemma(word, row[3].GetString()!) });
        }
        return result;
    }

    private static string? Lemma(string word, string exchange)
    {
        var entries = exchange.Split('/').Where(value => value.StartsWith("0:", StringComparison.Ordinal)).Select(value => value[2..]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (entries.Length != 1) return null;
        try { var lemma = WordInput.Normalize(entries[0]); return lemma == word ? null : lemma; }
        catch (ArgumentException) { return null; }
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static async Task VerifyFileAsync(FileStream stream, Asset asset, CancellationToken ct)
    {
        if (stream.Length != asset.Bytes || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false), asset.Hash))
            throw new InvalidDataException();
    }
    private string Resolve(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl) || parts.Any(part => part.Length == 0 || part is "." or ".."))
            throw new InvalidDataException();
        var absolute = Path.GetFullPath(Path.Combine(_directory, Path.Combine(parts)));
        if (!absolute.StartsWith(Path.TrimEndingDirectorySeparator(_directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        var current = _directory;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        foreach (var part in parts)
        { current = Path.Combine(current, part); if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(); }
        return absolute;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _files = null; _cache.Clear(); _recency.Clear();
        }
        _lifetime.Cancel();
        // In-flight loads and waiters still own these objects until they unwind.
    }
}
