using System.Diagnostics;
using System.Text;
namespace VocabCollector.Core.Storage;

public sealed class DuplicateWordException(IReadOnlyList<NoteLocation> notes) : IOException("这个单词已经收录。")
{
    public IReadOnlyList<NoteLocation> Notes { get; } = notes;
}

public sealed class VaultStore : IVocabularyStore, ILinkMaintenance
{
    private readonly LinkRefresher _links = new();

    public async Task<LinkRefreshResult> RefreshLinksAsync(VaultOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var wordRoot = VaultPaths.WordRoot(options);
        if (!Directory.Exists(wordRoot)) return new(0, 0, 0);
        var lockPath = Path.Combine(wordRoot, ".vocabcollector.write.lock");
        VaultPaths.EnsureInside(options.RootPath, lockPath);
        await using var writeLock = await AcquireLockAsync(lockPath, ct);
        return await _links.RefreshAsync(options, await ScanAsync(options, ct), ct);
    }

    public Task<VaultIndex> ScanAsync(VaultOptions options, CancellationToken ct) => Task.Run(() =>
    {
        var wordRoot = VaultPaths.WordRoot(options);
        List<NoteLocation> notes = [];
        List<NoteLocation> rootNotes = [];
        if (!Directory.Exists(wordRoot)) return new VaultIndex(notes);
        Stack<(string Path, bool IsRoot)> pending = new(); pending.Push((wordRoot, false));
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            var directory = current.Path;
            VaultPaths.EnsureInside(options.RootPath, directory);
            foreach (var path in Directory.EnumerateFiles(directory, "*.md"))
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                string word;
                try
                {
                    var stem = Path.GetFileNameWithoutExtension(path);
                    word = current.IsRoot ? RootInput.Normalize(stem) : WordInput.Normalize(WordInput.FromFileStem(stem));
                }
                catch (ArgumentException) { continue; }
                (current.IsRoot ? rootNotes : notes).Add(new(word, path, Path.GetRelativePath(options.RootPath, path).Replace('\\', '/')));
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(child);
                if (name.Equals(".obsidian", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".vocabcollector", StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    pending.Push((child, current.IsRoot || (directory == wordRoot && name == "词根")));
            }
        }
        return new VaultIndex(notes, rootNotes);
    }, ct);

    public async Task<SavedNote> SaveNewAsync(string word, PartOfSpeech primaryPos, Func<VaultIndex, string> render, VaultOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var wordRoot = VaultPaths.WordRoot(options);
        Directory.CreateDirectory(wordRoot);
        VaultPaths.EnsureInside(options.RootPath, wordRoot);
        var lockPath = Path.Combine(wordRoot, ".vocabcollector.write.lock");
        VaultPaths.EnsureInside(options.RootPath, lockPath);
        await using var writeLock = await AcquireLockAsync(lockPath, ct);
        var latestIndex = await ScanAsync(options, ct);
        var existing = latestIndex.Find(word);
        if (existing.Count > 0) throw new DuplicateWordException(existing);
        var directory = options.ClassifyByPartOfSpeech ? Path.Combine(wordRoot, VaultPaths.PartFolder(primaryPos)) : wordRoot;
        VaultPaths.EnsureInside(options.RootPath, directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, WordInput.ToFileName(word));
        var temporary = Path.Combine(directory, ".vocab-" + Guid.NewGuid().ToString("N") + ".tmp");
        var markdown = render(latestIndex);
        try
        {
            ct.ThrowIfCancellationRequested();
            VaultPaths.EnsureInside(options.RootPath, path);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                var bytes = new UTF8Encoding(false).GetBytes(markdown.Replace("\r\n", "\n").Replace('\r', '\n'));
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            // Re-resolve related files immediately before committing, including changes during lock wait/write.
            var finalMarkdown = render(await ScanAsync(options, ct));
            if (!string.Equals(markdown, finalMarkdown, StringComparison.Ordinal))
            {
                markdown = finalMarkdown;
                await File.WriteAllTextAsync(temporary, markdown, new UTF8Encoding(false), ct);
            }
            ct.ThrowIfCancellationRequested();
            VaultPaths.EnsureInside(options.RootPath, path);
            File.Move(temporary, path, false);
            // The atomic move is the commit point: later cancellation must not report an unsaved result.
            NoteLocation note = new(word, path, Path.GetRelativePath(options.RootPath, path).Replace('\\', '/'));
            try
            {
                await _links.RegisterAsync(options, note, markdown, CancellationToken.None);
                return new(note, markdown);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new(note, markdown) { Warning = "笔记已保存，但本笔记的自动补链记录未能保存。请检查目录权限。" };
            }
        }
        finally
        {
            try { VaultPaths.EnsureInside(options.RootPath, temporary); if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
    }
    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(3)) { await Task.Delay(50, ct); }
        }
    }
}
