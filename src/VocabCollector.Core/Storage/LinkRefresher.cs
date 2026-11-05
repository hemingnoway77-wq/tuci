using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Core.Markdown;

namespace VocabCollector.Core.Storage;

public sealed partial class LinkRefresher
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Registers only a newly generated note, under the caller's vault write lock.</summary>
    public async Task RegisterAsync(VaultOptions options, NoteLocation note, string markdown, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var regions = ManagedLinks.CaptureState(markdown);
        if (regions.Count == 0) return;
        var wordRoot = VaultPaths.WordRoot(options);
        ValidateNote(options.RootPath, wordRoot, note);
        var directory = Path.Combine(wordRoot, ".vocabcollector");
        var path = Path.Combine(directory, "note-state.json");
        var ownership = await ReadOwnershipAsync(options.RootPath, path, ct);
        ownership.Notes[note.VaultRelativePath] = new() { Word = note.Word, Regions = regions.ToArray() };
        await WriteStateAsync(options.RootPath, directory, path, ownership, ct);
    }

    /// <summary>The caller owns the vault write lock. This method never invokes an AI service.</summary>
    public async Task<LinkRefreshResult> RefreshAsync(VaultOptions options, VaultIndex index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var wordRoot = VaultPaths.WordRoot(options);
        var stateDirectory = Path.Combine(wordRoot, ".vocabcollector");
        var cachePath = Path.Combine(stateDirectory, "link-index.json");
        var ownershipPath = Path.Combine(stateDirectory, "note-state.json");
        OwnershipState ownership;
        try { ownership = await ReadOwnershipAsync(options.RootPath, ownershipPath, ct); }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // Ownership is durable, unlike the disposable cache. Never infer it from a plain personal note.
            return new(0, 0, index.Notes.Count);
        }
        var previous = await ReadCacheAsync(options.RootPath, cachePath, ct);
        var targets = TargetFingerprints(options.RootPath, wordRoot, index);
        var changedWords = new HashSet<string>(previous.Targets.Keys.Concat(targets.Keys).Where(word =>
            !previous.Targets.TryGetValue(word, out var oldTarget) || !targets.TryGetValue(word, out var newTarget) || oldTarget != newTarget), StringComparer.OrdinalIgnoreCase);
        CacheState next = new() { Targets = targets };
        int updated = 0, skipped = 0, failed = 0;

        foreach (var note in index.Notes.DistinctBy(n => n.AbsolutePath, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ValidateNote(options.RootPath, wordRoot, note);
                var stamp = Stamp(note.AbsolutePath);
                ownership.Notes.TryGetValue(note.VaultRelativePath, out var owned);
                var ownershipFingerprint = Fingerprint(owned);
                previous.Notes.TryGetValue(note.VaultRelativePath, out var known);
                if (known is not null && known.LastWriteUtcTicks == stamp.LastWriteUtcTicks && known.Length == stamp.Length &&
                    known.OwnershipFingerprint == ownershipFingerprint &&
                    !known.References.Any(changedWords.Contains))
                {
                    next.Notes[note.VaultRelativePath] = known;
                    if (known.HasEditedBlocks) skipped++;
                    continue;
                }

                var original = await File.ReadAllTextAsync(note.AbsolutePath, Utf8, ct);
                var legacy = ManagedLinks.ImportLegacy(original);
                var clean = legacy?.Markdown ?? original;
                if (legacy is not null)
                {
                    owned = new() { Word = note.Word, Regions = legacy.Regions.ToArray() };
                }
                if (owned is null || !owned.Word.Equals(note.Word, StringComparison.OrdinalIgnoreCase))
                {
                    next.Notes[note.VaultRelativePath] = Entry(stamp, false, [], ownershipFingerprint);
                    continue;
                }

                var hasEditedBlocks = ManagedLinks.HasEditedBlocks(clean, owned.Regions);
                if (hasEditedBlocks) skipped++;
                var markdown = ManagedLinks.Refresh(clean, note.Word, index, owned.Regions);
                if (markdown == original)
                {
                    next.Notes[note.VaultRelativePath] = Entry(stamp, hasEditedBlocks, ManagedLinks.CandidateWords(clean, owned.Regions), Fingerprint(owned));
                    continue;
                }

                // Persist embedded ORIGINAL hashes before removing comments, including hashes of edited regions.
                if (legacy is not null)
                    await SetOwnershipAsync(options.RootPath, stateDirectory, ownershipPath, ownership, note.VaultRelativePath, owned, ct);
                await ReplaceWithBackupAsync(options.RootPath, wordRoot, stateDirectory, note, original, markdown, ct);
                updated++;
                var advanced = ManagedLinks.AdvanceState(clean, markdown, owned.Regions);
                owned = new() { Word = note.Word, Regions = advanced.ToArray() };
                // A failed metadata update retains old hashes; subsequent refresh protects the changed note.
                await SetOwnershipAsync(options.RootPath, stateDirectory, ownershipPath, ownership, note.VaultRelativePath, owned, ct);
                next.Notes[note.VaultRelativePath] = Entry(Stamp(note.AbsolutePath), hasEditedBlocks, ManagedLinks.CandidateWords(markdown, owned.Regions), Fingerprint(owned));
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                // Leave this note out of the cache so that a later refresh retries it.
                failed++;
            }
        }

        // A missing or unwritable cache only makes the next refresh scan again; it never cancels completed note updates.
        try { await WriteStateAsync(options.RootPath, stateDirectory, cachePath, next, ct); }
        catch (Exception exception) when (IsFileFailure(exception)) { }
        return new(updated, skipped, failed);
    }

    private static Dictionary<string, string> TargetFingerprints(string root, string wordRoot, VaultIndex index)
    {
        List<(string Key, NoteLocation Note)> valid = [];
        foreach (var item in index.Notes.Select(note => (Key: note.Word, Note: note))
            .Concat(index.RootNotes.Select(note => (Key: "root:" + note.Word, Note: note))))
        {
            try
            {
                var note = item.Note;
                ValidateNote(root, wordRoot, note);
                // Even an un-linkable duplicate makes a formerly unique word ambiguous.
                if (File.Exists(note.AbsolutePath)) valid.Add(item);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return valid.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
            group => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', group.Select(item =>
                item.Note.VaultRelativePath + "\t" + VaultPaths.LinkTarget(item.Note)).Order(StringComparer.OrdinalIgnoreCase))))), StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateNote(string root, string wordRoot, NoteLocation note)
    {
        VaultPaths.EnsureInside(root, note.AbsolutePath);
        VaultPaths.EnsureInside(wordRoot, note.AbsolutePath);
        var expected = Path.GetRelativePath(root, note.AbsolutePath).Replace('\\', '/');
        if (!expected.Equals(note.VaultRelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(note.AbsolutePath).Equals(".md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("笔记路径与词库索引不一致。");
    }

    private static FileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("历史单词笔记已移动或删除。", path);
        return new(info.LastWriteTimeUtc.Ticks, info.Length);
    }

    private static CacheEntry Entry(FileStamp stamp, bool hasEditedBlocks, IReadOnlyList<string> references, string ownershipFingerprint) => new()
    {
        LastWriteUtcTicks = stamp.LastWriteUtcTicks,
        Length = stamp.Length,
        HasEditedBlocks = hasEditedBlocks,
        References = references.ToArray(),
        OwnershipFingerprint = ownershipFingerprint
    };

    private static async Task ReplaceWithBackupAsync(string root, string wordRoot, string stateDirectory, NoteLocation note,
        string original, string markdown, CancellationToken ct)
    {
        var backupDirectory = Path.Combine(stateDirectory, "backups");
        var backup = Path.Combine(backupDirectory, Path.GetFileName(note.AbsolutePath) + "." + Guid.NewGuid().ToString("N") + ".bak");
        var temporary = Path.Combine(Path.GetDirectoryName(note.AbsolutePath)!, ".vocablinks-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            VaultPaths.EnsureInside(root, backupDirectory);
            Directory.CreateDirectory(backupDirectory);
            VaultPaths.EnsureInside(root, backupDirectory);
            VaultPaths.EnsureInside(root, backup);
            VaultPaths.EnsureInside(root, temporary);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(Utf8.GetBytes(markdown), ct);
                await stream.FlushAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            ValidateNote(root, wordRoot, note);
            if (await File.ReadAllTextAsync(note.AbsolutePath, Utf8, ct) != original)
                throw new IOException("笔记刚刚被其他程序修改，本次跳过以保护修改内容。");
            VaultPaths.EnsureInside(root, backup);
            VaultPaths.EnsureInside(root, temporary);
            ValidateNote(root, wordRoot, note);
            File.Replace(temporary, note.AbsolutePath, backup, true);
        }
        finally { DeleteTemporary(root, temporary); }
    }

    private static async Task<CacheState> ReadCacheAsync(string root, string path, CancellationToken ct)
    {
        try
        {
            VaultPaths.EnsureInside(root, path);
            if (!File.Exists(path)) return new();
            var state = JsonSerializer.Deserialize<CacheState>(await File.ReadAllTextAsync(path, Utf8, ct));
            if (state is null || state.Version != 3 || state.Notes is null || state.Targets is null ||
                state.Notes.Values.Any(entry => entry is null || entry.References is null)) return new();
            // JSON dictionaries do not retain their original comparer after deserialization.
            state.Targets = new(state.Targets, StringComparer.OrdinalIgnoreCase);
            state.Notes = new(state.Notes, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException) { return new(); }
    }

    private static async Task WriteStateAsync<T>(string root, string directory, string path, T state, CancellationToken ct)
    {
        var temporary = Path.Combine(directory, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            VaultPaths.EnsureInside(root, directory);
            Directory.CreateDirectory(directory);
            VaultPaths.EnsureInside(root, path);
            VaultPaths.EnsureInside(root, temporary);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, state, cancellationToken: ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            VaultPaths.EnsureInside(root, path);
            VaultPaths.EnsureInside(root, temporary);
            File.Move(temporary, path, true);
        }
        finally { DeleteTemporary(root, temporary); }
    }

    private static void DeleteTemporary(string root, string path)
    {
        try { VaultPaths.EnsureInside(root, path); if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    private static bool IsFileFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static async Task SetOwnershipAsync(string root, string directory, string path, OwnershipState state, string notePath, OwnedNote note, CancellationToken ct)
    {
        state.Notes.TryGetValue(notePath, out var previous);
        state.Notes[notePath] = note;
        try { await WriteStateAsync(root, directory, path, state, ct); }
        catch
        {
            if (previous is null) state.Notes.Remove(notePath);
            else state.Notes[notePath] = previous;
            throw;
        }
    }

    private static string Fingerprint(OwnedNote? note) => note is null ? "" : Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(
        note.Word + "\n" + string.Join('\n', note.Regions.Select(region => region.Mode + ":" + region.BodyHash)))));

    private static async Task<OwnershipState> ReadOwnershipAsync(string root, string path, CancellationToken ct)
    {
        VaultPaths.EnsureInside(root, path);
        if (!File.Exists(path)) return new();
        try
        {
            var state = JsonSerializer.Deserialize<OwnershipState>(await File.ReadAllTextAsync(path, Utf8, ct));
            if (state is null || state.Version != 1 || state.Notes is null || state.Notes.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || string.IsNullOrWhiteSpace(pair.Value.Word) ||
                    pair.Value.Regions is null || pair.Value.Regions.Length > 4 ||
                    pair.Value.Regions.Select(region => region?.Mode).Distinct().Count() != pair.Value.Regions.Length ||
                    pair.Value.Regions.Any(region => region is null || region.Mode is not ("examples" or "relations" or "roots" or "forms") ||
                        region.BodyHash is null || region.BodyHash.Length != 64 || !region.BodyHash.All(Uri.IsHexDigit))))
                throw new IOException("自动补链记录格式无效，已保留笔记内容。");
            state.Notes = new(state.Notes, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (JsonException exception) { throw new IOException("自动补链记录无法读取，已保留笔记内容。", exception); }
    }

    private sealed record FileStamp(long LastWriteUtcTicks, long Length);
    private sealed class CacheState
    {
        public int Version { get; set; } = 3;
        public Dictionary<string, string> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CacheEntry> Notes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class CacheEntry
    {
        public long LastWriteUtcTicks { get; set; }
        public long Length { get; set; }
        public bool HasEditedBlocks { get; set; }
        public string[] References { get; set; } = [];
        public string OwnershipFingerprint { get; set; } = "";
    }
    private sealed class OwnershipState
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, OwnedNote> Notes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class OwnedNote
    {
        public string Word { get; set; } = "";
        public LinkRegionState[] Regions { get; set; } = [];
    }
}
