using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Core.Markdown;

namespace VocabCollector.Core.Storage;

public sealed class LinkRefresher
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The caller owns the vault write lock. This method never invokes an AI service.</summary>
    public async Task<LinkRefreshResult> RefreshAsync(VaultOptions options, VaultIndex index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var wordRoot = VaultPaths.WordRoot(options);
        var stateDirectory = Path.Combine(wordRoot, ".vocabcollector");
        var cachePath = Path.Combine(stateDirectory, "link-index.json");
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
                previous.Notes.TryGetValue(note.VaultRelativePath, out var known);
                if (known is not null && known.LastWriteUtcTicks == stamp.LastWriteUtcTicks && known.Length == stamp.Length &&
                    !known.References.Any(changedWords.Contains))
                {
                    next.Notes[note.VaultRelativePath] = known;
                    if (known.HasEditedBlocks) skipped++;
                    continue;
                }

                var original = await File.ReadAllTextAsync(note.AbsolutePath, Utf8, ct);
                var hasManagedBlocks = ManagedLinks.HasBlocks(original);
                if (!hasManagedBlocks)
                {
                    next.Notes[note.VaultRelativePath] = Entry(stamp, false, false, []);
                    continue;
                }

                var hasEditedBlocks = ManagedLinks.HasEditedBlocks(original);
                if (hasEditedBlocks) skipped++;
                var markdown = ManagedLinks.Refresh(original, note.Word, index);
                if (markdown == original)
                {
                    next.Notes[note.VaultRelativePath] = Entry(stamp, true, hasEditedBlocks, ManagedLinks.CandidateWords(original));
                    continue;
                }

                await ReplaceWithBackupAsync(options.RootPath, wordRoot, stateDirectory, note, original, markdown, ct);
                updated++;
                next.Notes[note.VaultRelativePath] = Entry(Stamp(note.AbsolutePath), true, hasEditedBlocks, ManagedLinks.CandidateWords(markdown));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Leave this note out of the cache so that a later refresh retries it.
                failed++;
            }
        }

        // A missing or unwritable cache only makes the next refresh scan again; it never cancels completed note updates.
        try { await WriteCacheAsync(options.RootPath, stateDirectory, cachePath, next, ct); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return new(updated, skipped, failed);
    }

    private static Dictionary<string, string> TargetFingerprints(string root, string wordRoot, VaultIndex index)
    {
        List<NoteLocation> valid = [];
        foreach (var note in index.Notes)
        {
            try
            {
                ValidateNote(root, wordRoot, note);
                // Even an un-linkable duplicate makes a formerly unique word ambiguous.
                if (File.Exists(note.AbsolutePath)) valid.Add(note);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return valid.GroupBy(n => n.Word, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
            group => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', group.Select(n => n.VaultRelativePath).Order(StringComparer.OrdinalIgnoreCase))))), StringComparer.OrdinalIgnoreCase);
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

    private static CacheEntry Entry(FileStamp stamp, bool hasManagedBlocks, bool hasEditedBlocks, IReadOnlyList<string> references) => new()
    {
        LastWriteUtcTicks = stamp.LastWriteUtcTicks,
        Length = stamp.Length,
        HasManagedBlocks = hasManagedBlocks,
        HasEditedBlocks = hasEditedBlocks,
        References = references.ToArray()
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
            if (state is null || state.Version != 1 || state.Notes is null || state.Targets is null ||
                state.Notes.Values.Any(entry => entry is null || entry.References is null)) return new();
            // JSON dictionaries do not retain their original comparer after deserialization.
            state.Targets = new(state.Targets, StringComparer.OrdinalIgnoreCase);
            state.Notes = new(state.Notes, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException) { return new(); }
    }

    private static async Task WriteCacheAsync(string root, string directory, string path, CacheState state, CancellationToken ct)
    {
        var temporary = Path.Combine(directory, ".link-index-" + Guid.NewGuid().ToString("N") + ".tmp");
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

    private sealed record FileStamp(long LastWriteUtcTicks, long Length);
    private sealed class CacheState
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, string> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CacheEntry> Notes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class CacheEntry
    {
        public long LastWriteUtcTicks { get; set; }
        public long Length { get; set; }
        public bool HasManagedBlocks { get; set; }
        public bool HasEditedBlocks { get; set; }
        public string[] References { get; set; } = [];
    }
}
