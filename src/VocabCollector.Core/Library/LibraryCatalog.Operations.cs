using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VocabCollector.Core.Storage;

namespace VocabCollector.Core.Library;

public sealed class LibraryConflictException(string message) : IOException(message);
public sealed record LibraryTrashItem(string Id, string RelativePath, DateTimeOffset DeletedAt);

public sealed partial class LibraryCatalog
{
    public async Task<LibraryNote> CreateNoteAsync(string relativePath, string markdown, CancellationToken cancellationToken = default)
    {
        var target = OperationPath(relativePath, isNote: true);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var vaultWriteLock = await AcquireVaultWriteLockAsync(cancellationToken);
            RefuseExisting(target);
            var (_, entries) = await OperationIndexAsync(cancellationToken);
            CreateSafeParents(target);
            var added = LibraryNote.CreationDate(markdown) ?? DateTimeOffset.UtcNow;
            var note = new LibraryNote(Guid.NewGuid().ToString("N"), Relative(target), target, markdown, added);
            var updated = entries.Append(Entry(note)).ToList();
            var temporary = await WriteTemporaryAsync(target, markdown, cancellationToken);
            try
            {
                await ChangeIndexAsync(entries, updated, () =>
                {
                    VaultPaths.EnsureInside(_wordRoot, target);
                    File.Move(temporary, target, overwrite: false);
                    return Task.CompletedTask;
                }, cancellationToken);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return note;
        }
        finally { _gate.Release(); }
    }

    public async Task CreateFolderAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var target = OperationPath(relativePath, isNote: false);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var vaultWriteLock = await AcquireVaultWriteLockAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RefuseExisting(target);
            Directory.CreateDirectory(target);
            VaultPaths.EnsureInside(_wordRoot, target);
        }
        finally { _gate.Release(); }
    }

    public async Task<LibraryNote> SaveNoteAsync(LibraryNote expected, string markdown, CancellationToken cancellationToken = default)
    {
        ValidateExpectedPath(expected);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var vaultWriteLock = await AcquireVaultWriteLockAsync(cancellationToken);
            var (snapshot, entries) = await OperationIndexAsync(cancellationToken);
            var current = CurrentExpected(expected, snapshot);
            await using var held = await HoldExpectedAsync(current, cancellationToken);
            if (markdown == current.Markdown) return current;
            await BackupAsync(current, cancellationToken);
            var saved = current with { Markdown = markdown, AddedAt = LibraryNote.CreationDate(markdown) ?? current.AddedAt };
            var updated = entries.Select(entry => entry.Id == saved.Id ? Entry(saved) : entry).ToList();
            var temporary = await WriteTemporaryAsync(current.AbsolutePath, markdown, cancellationToken);
            try
            {
                await ChangeIndexAsync(entries, updated, async () =>
                {
                    await VerifyExpectedOnDiskAsync(current, cancellationToken);
                    File.Replace(temporary, current.AbsolutePath, null);
                }, cancellationToken);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return saved;
        }
        finally { _gate.Release(); }
    }

    public async Task<LibraryNote> MoveNoteAsync(LibraryNote expected, string relativePath, CancellationToken cancellationToken = default)
    {
        ValidateExpectedPath(expected);
        var target = OperationPath(relativePath, isNote: true);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var vaultWriteLock = await AcquireVaultWriteLockAsync(cancellationToken);
            var (snapshot, entries) = await OperationIndexAsync(cancellationToken);
            var current = CurrentExpected(expected, snapshot);
            if (target.Equals(current.AbsolutePath, StringComparison.Ordinal)) return current;
            var caseOnly = target.Equals(current.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly) RefuseExisting(target);
            CreateSafeParents(target);
            await using var held = await HoldExpectedAsync(current, cancellationToken);
            var moved = current with { RelativePath = Relative(target), AbsolutePath = target };
            var updated = entries.Select(entry => entry.Id == current.Id ? Entry(moved) : entry).ToList();
            await ChangeIndexAsync(entries, updated, async () =>
            {
                await VerifyExpectedOnDiskAsync(current, cancellationToken);
                if (!caseOnly) File.Move(current.AbsolutePath, target, overwrite: false);
                else
                {
                    var intermediate = Path.Combine(Path.GetDirectoryName(target)!, ".rename-" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.Move(current.AbsolutePath, intermediate, overwrite: false);
                    try { File.Move(intermediate, target, overwrite: false); }
                    catch
                    {
                        if (!File.Exists(current.AbsolutePath)) File.Move(intermediate, current.AbsolutePath, overwrite: false);
                        throw;
                    }
                }
            }, cancellationToken);
            return moved;
        }
        finally { _gate.Release(); }
    }

    public async Task<LibraryTrashItem> TrashNoteAsync(LibraryNote expected, CancellationToken cancellationToken = default)
    {
        ValidateExpectedPath(expected);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var vaultWriteLock = await AcquireVaultWriteLockAsync(cancellationToken);
            var (snapshot, entries) = await OperationIndexAsync(cancellationToken);
            var current = CurrentExpected(expected, snapshot);
            await using var held = await HoldExpectedAsync(current, cancellationToken);
            var item = new LibraryTrashItem(Guid.NewGuid().ToString("N"), current.RelativePath, DateTimeOffset.UtcNow);
            var location = TrashDirectory(item.Id);
            LocalLibraryState.EnsureNoLinks(location);
            Directory.CreateDirectory(location);
            var record = new TrashRecord(item.Id, current.Id, current.RelativePath, current.AddedAt, item.DeletedAt,
                Hash(current.Markdown), entries.FirstOrDefault(entry => entry.Id == current.Id)?.CloudId);
            await LocalLibraryState.WriteAsync(Path.Combine(location, "record.json"), record, cancellationToken);
            await ChangeIndexAsync(entries, entries.Where(entry => entry.Id != current.Id).ToList(), async () =>
            {
                await VerifyExpectedOnDiskAsync(current, cancellationToken);
                LocalLibraryState.EnsureNoLinks(location);
                File.Move(current.AbsolutePath, Path.Combine(location, "note.md"), overwrite: false);
            }, cancellationToken);
            return item;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<LibraryTrashItem>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = Path.Combine(LocalStateDirectory, "trash");
            LocalLibraryState.EnsureNoLinks(root);
            if (!Directory.Exists(root)) return [];
            var items = new List<LibraryTrashItem>();
            foreach (var directory in Directory.GetDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                LocalLibraryState.EnsureNoLinks(directory);
                if (!File.Exists(Path.Combine(directory, "note.md"))) continue;
                var record = await ReadTrashAsync(id, cancellationToken);
                items.Add(new(record.Id, record.RelativePath, record.DeletedAt));
            }
            return items.OrderByDescending(item => item.DeletedAt).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<LibraryNote> RestoreNoteAsync(LibraryTrashItem item, CancellationToken cancellationToken = default)
    {
        var location = TrashDirectory(item.Id);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var vaultWriteLock = await AcquireVaultWriteLockAsync(cancellationToken);
            var record = await ReadTrashAsync(item.Id, cancellationToken);
            if (record.RelativePath != item.RelativePath || record.DeletedAt != item.DeletedAt)
                throw new LibraryConflictException("回收记录已变化，请刷新回收列表。");
            var target = OperationPath(record.RelativePath, isNote: true, allowLinkReserved: true);
            RefuseExisting(target);
            // Another note can reuse this path while the older note is in trash.
            // If that replacement was only just trashed, sync must first delete
            // its head; restoring now would make a path-based sync claim its ID.
            var restoredIdentity = record.CloudId ?? record.NoteId;
            if (_cloudByPath.TryGetValue(record.RelativePath, out var liveCloudId) &&
                !string.Equals(restoredIdentity, liveCloudId, StringComparison.Ordinal))
                throw new IOException("该路径有另一篇笔记尚待同步删除。请先同步词库，再恢复这篇笔记；回收内容已保留。");
            var (_, entries) = await OperationIndexAsync(cancellationToken);
            if (entries.Any(entry => entry.Id == record.NoteId))
                throw new LibraryConflictException("该笔记身份已在词库中，未重复恢复。");
            var blob = Path.Combine(location, "note.md");
            LocalLibraryState.EnsureNoLinks(blob);
            var markdown = await File.ReadAllTextAsync(blob, cancellationToken);
            if (Hash(markdown) != record.ContentHash) throw new IOException("回收内容已变化，未恢复或覆盖任何笔记。");
            var restored = new LibraryNote(record.NoteId, record.RelativePath, target, markdown, record.AddedAt);
            CreateSafeParents(target);
            var restoredEntry = new CatalogEntry(restored.Id, restored.RelativePath, Hash(restored.Markdown), restored.AddedAt, record.CloudId);
            await ChangeIndexAsync(entries, entries.Append(restoredEntry).ToList(), () =>
            {
                VaultPaths.EnsureInside(_wordRoot, target);
                LocalLibraryState.EnsureNoLinks(blob);
                File.Move(blob, target, overwrite: false);
                return Task.CompletedTask;
            }, cancellationToken);
            // An interrupted cleanup is harmless: listings require note.md as well as the descriptor.
            try { File.Delete(Path.Combine(location, "record.json")); Directory.Delete(location); }
            catch (Exception error) when (LocalLibraryState.IsStorageError(error)) { }
            return restored;
        }
        finally { _gate.Release(); }
    }

    private string OperationPath(string relativePath, bool isNote, bool allowLinkReserved = false)
    {
        var normalized = relativePath.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (normalized.Length > 1024 || Path.IsPathRooted(normalized) || (isNote && !normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) ||
            parts.Any(part => string.IsNullOrWhiteSpace(part) || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(' ') || part.Length > 255 ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || (!allowLinkReserved && part.IndexOfAny(['#', '^', '[', ']', '|']) >= 0) || part.Any(char.IsControl) ||
                Regex.IsMatch(part.Split('.')[0], @"\A(?:con|prn|aux|nul|conin\$|conout\$|clock\$|com[0-9¹²³]|lpt[0-9¹²³])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new ArgumentException("请输入词库内的普通相对路径，不能使用隐藏目录、保留名称或路径跳转。", nameof(relativePath));
        var path = Path.GetFullPath(Path.Combine(_wordRoot, Path.Combine(parts)));
        VaultPaths.EnsureInside(_wordRoot, path);
        return path;
    }

    private async Task<FileStream> AcquireVaultWriteLockAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VaultPaths.EnsureInside(_options.RootPath, _wordRoot);
        Directory.CreateDirectory(_wordRoot);
        var path = Path.Combine(_wordRoot, ".vocabcollector.write.lock");
        VaultPaths.EnsureInside(_wordRoot, path);
        var held = await VaultStore.AcquireLockAsync(path, cancellationToken);
        try
        {
            VaultPaths.EnsureInside(_wordRoot, path);
            return held;
        }
        catch { await held.DisposeAsync(); throw; }
    }

    private void ValidateExpectedPath(LibraryNote note)
    {
        if (!IsSafeNote(note)) throw new ArgumentException("笔记必须位于当前词库内，不能包含目录或文件链接。", nameof(note));
        _ = OperationPath(note.RelativePath, isNote: true, allowLinkReserved: true);
    }

    private static void RefuseExisting(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("目标已存在，未覆盖：" + Path.GetFileName(path));
    }

    private void CreateSafeParents(string path)
    {
        VaultPaths.EnsureInside(_wordRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        VaultPaths.EnsureInside(_wordRoot, path);
    }

    private string Relative(string absolute) => Path.GetRelativePath(_wordRoot, absolute).Replace('\\', '/');
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private CatalogEntry Entry(LibraryNote note) => new(note.Id, note.RelativePath, Hash(note.Markdown), note.AddedAt,
        CloudNoteIds.GetValueOrDefault(note.Id) ?? _cloudByPath.GetValueOrDefault(note.RelativePath));

    private async Task<(LibrarySnapshot Snapshot, List<CatalogEntry> Entries)> OperationIndexAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ScanCoreAsync(cancellationToken);
        if (_pendingEntries is not null) throw new IOException("本机词库索引尚未保存，文件操作未执行；请恢复索引目录读写权限后重试。");
        var path = Path.Combine(LocalStateDirectory, "catalog.json");
        LocalLibraryState.EnsureNoLinks(path);
        var index = JsonSerializer.Deserialize<CatalogIndex>(await File.ReadAllTextAsync(path, cancellationToken));
        return (snapshot, index?.Entries ?? throw new IOException("词库索引不可用，文件操作未执行。"));
    }

    private static LibraryNote CurrentExpected(LibraryNote expected, LibrarySnapshot snapshot)
    {
        var current = snapshot.Notes.FirstOrDefault(note => note.RelativePath.Equals(expected.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (current is null || current.Id != expected.Id || current.Markdown != expected.Markdown)
            throw new LibraryConflictException("笔记已被其他编辑器修改、移动或删除；编辑内容已保留，请刷新或另存副本。");
        return current;
    }

    private async Task<FileStream> HoldExpectedAsync(LibraryNote expected, CancellationToken cancellationToken)
    {
        VaultPaths.EnsureInside(_wordRoot, expected.AbsolutePath);
        var stream = new FileStream(expected.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            if (await reader.ReadToEndAsync(cancellationToken) != expected.Markdown)
                throw new LibraryConflictException("笔记在保存前已被修改，编辑内容已保留。");
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private async Task VerifyExpectedOnDiskAsync(LibraryNote expected, CancellationToken cancellationToken)
    {
        VaultPaths.EnsureInside(_wordRoot, expected.AbsolutePath);
        if (!File.Exists(expected.AbsolutePath) || await File.ReadAllTextAsync(expected.AbsolutePath, cancellationToken) != expected.Markdown)
            throw new LibraryConflictException("笔记在提交前已被外部修改，未覆盖外部内容。");
    }

    private async Task BackupAsync(LibraryNote note, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(LocalStateDirectory, "backups", Guid.NewGuid().ToString("N"));
        LocalLibraryState.EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        LocalLibraryState.EnsureNoLinks(directory);
        File.Copy(note.AbsolutePath, Path.Combine(directory, "original.md"), overwrite: false);
        await LocalLibraryState.WriteAsync(Path.Combine(directory, "record.json"), new BackupRecord(note.Id, note.RelativePath, DateTimeOffset.UtcNow), cancellationToken);
    }

    private static async Task<string> WriteTemporaryAsync(string destination, string markdown, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".edit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(markdown), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            return temporary;
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }

    private async Task ChangeIndexAsync(List<CatalogEntry> previous, List<CatalogEntry> updated, Func<Task> change, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(LocalStateDirectory, "catalog.json");
        await LocalLibraryState.WriteAsync(statePath, new CatalogIndex(1, updated), cancellationToken);
        try { cancellationToken.ThrowIfCancellationRequested(); await change(); }
        catch
        {
            try { await LocalLibraryState.WriteAsync(statePath, new CatalogIndex(1, previous), CancellationToken.None); }
            catch (Exception error) when (LocalLibraryState.IsStorageError(error)) { _pendingEntries = previous; }
            throw;
        }
    }

    private string TrashDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("回收记录编号无效。", nameof(id));
        var path = Path.Combine(LocalStateDirectory, "trash", id);
        LocalLibraryState.EnsureNoLinks(path);
        return path;
    }

    private async Task<TrashRecord> ReadTrashAsync(string id, CancellationToken cancellationToken)
    {
        var location = TrashDirectory(id);
        var path = Path.Combine(location, "record.json");
        LocalLibraryState.EnsureNoLinks(path);
        TrashRecord? record;
        try { record = JsonSerializer.Deserialize<TrashRecord>(await File.ReadAllTextAsync(path, cancellationToken)); }
        catch (JsonException error) { throw new IOException("回收记录损坏，请保留回收目录并检查备份。", error); }
        if (record is null || record.Id != id || string.IsNullOrWhiteSpace(record.NoteId) || string.IsNullOrWhiteSpace(record.RelativePath) || string.IsNullOrWhiteSpace(record.ContentHash))
            throw new IOException("回收记录不完整，未移动任何文件。");
        _ = OperationPath(record.RelativePath, isNote: true, allowLinkReserved: true);
        return record;
    }

    private sealed record BackupRecord(string NoteId, string RelativePath, DateTimeOffset BackedUpAt);
    private sealed record TrashRecord(string Id, string NoteId, string RelativePath, DateTimeOffset AddedAt, DateTimeOffset DeletedAt, string ContentHash, string? CloudId = null);
}
