using System.Diagnostics;
using System.Text;
namespace VocabCollector.Core.Storage;

public sealed class DuplicateWordException(IReadOnlyList<NoteLocation> notes) : IOException("这个单词已经收录。")
{
    public IReadOnlyList<NoteLocation> Notes { get; } = notes;
}

public sealed class VaultStore : IVocabularyStore
{
    public Task<VaultIndex> ScanAsync(VaultOptions options, CancellationToken ct) => Task.Run(() =>
    {
        var wordRoot = VaultPaths.WordRoot(options);
        List<NoteLocation> notes = [];
        if (!Directory.Exists(wordRoot)) return new VaultIndex(notes);
        Stack<string> pending = new(); pending.Push(wordRoot);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            VaultPaths.EnsureInside(options.RootPath, directory);
            foreach (var path in Directory.EnumerateFiles(directory, "*.md"))
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                string word;
                try { word = WordInput.Normalize(WordInput.FromFileStem(Path.GetFileNameWithoutExtension(path))); }
                catch (ArgumentException) { continue; }
                notes.Add(new(word, path, Path.GetRelativePath(options.RootPath, path).Replace('\\', '/')));
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
        }
        return new VaultIndex(notes);
    }, ct);

    public async Task<NoteLocation> SaveNewAsync(string word, PartOfSpeech primaryPos, string markdown, VaultOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var wordRoot = VaultPaths.WordRoot(options);
        Directory.CreateDirectory(wordRoot);
        VaultPaths.EnsureInside(options.RootPath, wordRoot);
        var lockPath = Path.Combine(wordRoot, ".vocabcollector.write.lock");
        VaultPaths.EnsureInside(options.RootPath, lockPath);
        await using var writeLock = await AcquireLockAsync(lockPath, ct);
        var existing = (await ScanAsync(options, ct)).Find(word);
        if (existing.Count > 0) throw new DuplicateWordException(existing);
        var directory = options.ClassifyByPartOfSpeech ? Path.Combine(wordRoot, VaultPaths.PartFolder(primaryPos)) : wordRoot;
        VaultPaths.EnsureInside(options.RootPath, directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, WordInput.ToFileName(word));
        var temporary = Path.Combine(directory, ".vocab-" + Guid.NewGuid().ToString("N") + ".tmp");
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
            ct.ThrowIfCancellationRequested();
            VaultPaths.EnsureInside(options.RootPath, path);
            File.Move(temporary, path, false);
            // The atomic move is the commit point: later cancellation must not report an unsaved result.
            return new(word, path, Path.GetRelativePath(options.RootPath, path).Replace('\\', '/'));
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
