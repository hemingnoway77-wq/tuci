using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Core.Storage;

namespace VocabCollector.Core.Library;

public sealed partial class LibraryCatalog
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly VaultOptions _options;
    private readonly string _wordRoot;
    private readonly SemaphoreSlim _gate;
    private List<CatalogEntry>? _pendingEntries;
    private List<CatalogEntry>? _lastKnownEntries;
    public string LocalStateDirectory { get; }

    public LibraryCatalog(VaultOptions options, string? metadataDirectory = null)
    {
        _options = options;
        _wordRoot = VaultPaths.WordRoot(options);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_wordRoot.Normalize().ToUpperInvariant())));
        LocalStateDirectory = Path.GetFullPath(metadataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector", "library-catalog", key));
        var vault = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        if (LocalStateDirectory.Equals(vault, StringComparison.OrdinalIgnoreCase) ||
            LocalStateDirectory.StartsWith(vault + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("学习元数据必须保存在词库之外。", nameof(metadataDirectory));
        _gate = Gates.GetOrAdd(LocalStateDirectory, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<LibrarySnapshot> ScanAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ScanCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<LibrarySnapshot> ScanCoreAsync(CancellationToken cancellationToken)
    {
        VaultPaths.EnsureInside(_options.RootPath, _wordRoot);
        var warnings = new List<string>();
        var statePath = Path.Combine(LocalStateDirectory, "catalog.json");
        var previous = new List<CatalogEntry>();
        var canSave = true;
        var readPersistedIndex = false;
        try
        {
            LocalLibraryState.EnsureNoLinks(statePath);
            Directory.CreateDirectory(LocalStateDirectory);
            if (File.Exists(statePath))
            {
                var index = JsonSerializer.Deserialize<CatalogIndex>(await File.ReadAllTextAsync(statePath, cancellationToken));
                if (index is null || index.Version != 1 || index.Entries is null ||
                    index.Entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.RelativePath) || string.IsNullOrWhiteSpace(entry.ContentHash)) ||
                    index.Entries.Select(entry => entry.Id).Distinct().Count() != index.Entries.Count ||
                    index.Entries.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != index.Entries.Count)
                    throw new JsonException("词库索引格式无效。");
                previous = index.Entries;
                _lastKnownEntries = previous;
                readPersistedIndex = true;
            }
        }
        catch (Exception error) when (LocalLibraryState.IsStorageError(error) || error is JsonException)
        {
            warnings.Add("无法读取本机词库索引；学习身份可能暂时不可用。" + error.Message);
            canSave = false;
        }
        // Keep identities already shown by this catalog even when a previous save failed.
        // The disk file is still read above so corruption is reported rather than overwritten.
        if (_pendingEntries is not null)
        {
            if (readPersistedIndex)
            {
                // A temporarily unreadable index may have produced provisional identities.
                // Prefer the recovered persistent identity, retaining only genuinely new paths.
                var persistedPaths = previous.Select(entry => entry.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                previous = previous.Concat(_pendingEntries.Where(entry => !persistedPaths.Contains(entry.RelativePath))).ToList();
            }
            else previous = _pendingEntries;
        }
        else if (!canSave && _lastKnownEntries is not null) previous = _lastKnownEntries;

        var files = new List<(string RelativePath, string AbsolutePath, string Markdown, string Hash, DateTimeOffset Created)>();
        var unavailablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateMarkdown(warnings, unavailablePaths, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                VaultPaths.EnsureInside(_wordRoot, path);
                var markdown = await File.ReadAllTextAsync(path, cancellationToken);
                files.Add((Path.GetRelativePath(_wordRoot, path).Replace('\\', '/'), path, markdown,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown))), File.GetCreationTimeUtc(path)));
            }
            catch (Exception error) when (LocalLibraryState.IsStorageError(error) || error is ArgumentException)
            {
                unavailablePaths.Add(path);
                warnings.Add("无法读取笔记：" + path + "。" + error.Message);
            }
        }
        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        var byPath = previous.ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var unavailableEntries = previous.Where(entry =>
        {
            var absolute = Path.GetFullPath(Path.Combine(_wordRoot, entry.RelativePath));
            return unavailablePaths.Any(path => absolute.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                absolute.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        var currentHashes = files.GroupBy(file => file.Hash).ToDictionary(group => group.Key, group => group.Count());
        var oldHashes = previous.GroupBy(entry => entry.ContentHash).ToDictionary(group => group.Key, group => group.ToArray());
        var identities = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
        var usedIds = unavailableEntries.Select(entry => entry.Id).ToHashSet();
        // Match unchanged files first, then unique unchanged moves, and finally edits in place.
        // A moved file may occupy a deleted note's path or leave its old name available for reuse.
        foreach (var file in files)
            if (byPath.TryGetValue(file.RelativePath, out var samePath) && samePath.ContentHash == file.Hash && usedIds.Add(samePath.Id))
                identities[file.RelativePath] = samePath;
        foreach (var file in files)
            if (unavailablePaths.Count == 0 && !identities.ContainsKey(file.RelativePath) && currentHashes[file.Hash] == 1 &&
                oldHashes.TryGetValue(file.Hash, out var sameContent) && sameContent.Length == 1 && usedIds.Add(sameContent[0].Id))
                identities[file.RelativePath] = sameContent[0];
        foreach (var file in files)
            if (!identities.ContainsKey(file.RelativePath) && byPath.TryGetValue(file.RelativePath, out var edited) && usedIds.Add(edited.Id))
                identities[file.RelativePath] = edited;
        var notes = new List<LibraryNote>();
        var entries = new List<CatalogEntry>(unavailableEntries);
        foreach (var file in files)
        {
            identities.TryGetValue(file.RelativePath, out var old);
            // A deterministic fallback also keeps a read-only library usable when local storage fails.
            var id = old?.Id ?? (canSave ? Guid.NewGuid().ToString("N") :
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_wordRoot.ToUpperInvariant() + "\n" + file.RelativePath.Normalize().ToUpperInvariant()))));
            var added = LibraryNote.CreationDate(file.Markdown) ?? old?.AddedAt ?? file.Created;
            notes.Add(new(id, file.RelativePath, file.AbsolutePath, file.Markdown, added));
            entries.Add(new(id, file.RelativePath, file.Hash, added));
        }
        if (canSave)
        {
            try
            {
                await LocalLibraryState.WriteAsync(statePath, new CatalogIndex(1, entries), cancellationToken);
                _pendingEntries = null;
                _lastKnownEntries = entries;
            }
            catch (Exception error) when (LocalLibraryState.IsStorageError(error))
            {
                canSave = false;
                warnings.Add("无法保存本机词库索引；请检查本机目录权限。" + error.Message);
            }
        }
        if (!canSave)
        {
            _pendingEntries = entries;
            warnings.Add("本次新增笔记的学习身份暂存在当前会话，重启后可能变化；请先恢复本机索引目录的读写权限。");
        }
        return new(notes, warnings);
    }

    private IEnumerable<string> EnumerateMarkdown(List<string> warnings, HashSet<string> unavailablePaths, CancellationToken cancellationToken)
    {
        try { _ = File.GetAttributes(_wordRoot); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (Exception error) when (LocalLibraryState.IsStorageError(error))
        {
            unavailablePaths.Add(_wordRoot);
            warnings.Add("无法检查词库目录：" + _wordRoot + "。" + error.Message);
            return [];
        }
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(_wordRoot);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] children;
            try
            {
                VaultPaths.EnsureInside(_wordRoot, directory);
                children = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception error) when (LocalLibraryState.IsStorageError(error) || error is ArgumentException)
            {
                unavailablePaths.Add(directory);
                warnings.Add("无法扫描目录：" + directory + "。" + error.Message);
                continue;
            }
            foreach (var path in children)
            {
                if (Path.GetFileName(path).StartsWith('.')) continue;
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        unavailablePaths.Add(path);
                        warnings.Add("已跳过目录或文件链接：" + path);
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else if (Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) result.Add(path);
                }
                catch (Exception error) when (LocalLibraryState.IsStorageError(error))
                {
                    unavailablePaths.Add(path);
                    warnings.Add("无法检查路径：" + path + "。" + error.Message);
                }
            }
        }
        return result;
    }

    public IReadOnlyList<LibraryNote> Resolve(string target, LibraryNote? source, IReadOnlyList<LibraryNote> notes)
    {
        if (string.IsNullOrWhiteSpace(target)) return [];
        var raw = target.Trim();
        if (raw.StartsWith("[[", StringComparison.Ordinal) && raw.EndsWith("]]", StringComparison.Ordinal)) raw = raw[2..^2];
        raw = raw.Split('|')[0].Split('#')[0].Trim();
        try { raw = Uri.UnescapeDataString(raw).Normalize().Replace('\\', '/'); }
        catch (Exception error) when (error is UriFormatException or ArgumentException) { return []; }
        if (raw.Length == 0 || raw.StartsWith('/') || raw.Contains(':') || raw.IndexOfAny(['\0', '|', '#', '?', '*']) >= 0) return [];
        var name = raw.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".md";
        if (!name.Contains('/'))
            return notes.Where(note => Path.GetFileName(note.RelativePath.Replace('\\', '/')).Normalize().Equals(name, StringComparison.OrdinalIgnoreCase))
                .Where(IsSafeNote).ToArray();

        IReadOnlyList<LibraryNote> Match(string relative)
        {
            try
            {
                var absolute = Path.GetFullPath(Path.Combine(_wordRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                VaultPaths.EnsureInside(_wordRoot, absolute);
                var normalized = Path.GetRelativePath(_wordRoot, absolute).Replace('\\', '/').Normalize();
                return notes.Where(note => note.RelativePath.Replace('\\', '/').Normalize().Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    .Where(IsSafeNote).ToArray();
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return []; }
        }

        var exact = Match(name);
        if (exact.Count > 0) return exact;
        if (source is not null && IsSafeNote(source))
        {
            var relative = Match(source.Folder.Length == 0 ? name : source.Folder + "/" + name);
            if (relative.Count > 0) return relative;
        }
        var prefixes = new List<string> { _options.OutputSubfolder.Replace('\\', '/').Trim('/').Normalize() };
        try
        {
            var obsidianPrefix = Path.GetRelativePath(VaultPaths.ObsidianRoot(_options), _wordRoot).Replace('\\', '/').Normalize();
            if (obsidianPrefix != ".") prefixes.Insert(0, obsidianPrefix);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { }
        foreach (var prefix in prefixes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!name.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var prefixed = Match(name[(prefix.Length + 1)..]);
            if (prefixed.Count > 0) return prefixed;
        }
        return [];
    }

    private bool IsSafeNote(LibraryNote note)
    {
        try
        {
            var relative = note.RelativePath.Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/').Any(part => part is "" or "." or "..")) return false;
            var expected = Path.GetFullPath(Path.Combine(_wordRoot, relative));
            VaultPaths.EnsureInside(_wordRoot, expected);
            return Path.GetFullPath(note.AbsolutePath).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public async Task<IReadOnlyList<LibraryNote>> ImportAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var vaultWriteLock = await AcquireVaultWriteLockAsync(cancellationToken);
            VaultPaths.EnsureInside(_options.RootPath, _wordRoot);
            var imports = new List<(string Source, string Target)>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(file);
                LocalLibraryState.EnsureNoLinks(source);
                if (!File.Exists(source) || !Path.GetExtension(source).Equals(".md", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("请选择已有的 Markdown 文件。", nameof(files));
                var target = Path.Combine(_wordRoot, Path.GetFileName(source));
                VaultPaths.EnsureInside(_wordRoot, target);
                if (!targets.Add(target) || File.Exists(target) || Directory.Exists(target))
                    throw new IOException("该笔记已存在，未覆盖：" + Path.GetFileName(target));
                imports.Add((source, target));
            }
            Directory.CreateDirectory(_wordRoot);
            VaultPaths.EnsureInside(_options.RootPath, _wordRoot);
            foreach (var import in imports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temporary = Path.Combine(_wordRoot, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    LocalLibraryState.EnsureNoLinks(import.Source);
                    File.Copy(import.Source, temporary, overwrite: false);
                    VaultPaths.EnsureInside(_wordRoot, import.Target);
                    File.Move(temporary, import.Target, overwrite: false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            var snapshot = await ScanCoreAsync(cancellationToken);
            return snapshot.Notes.Where(note => targets.Contains(note.AbsolutePath)).ToArray();
        }
        finally { _gate.Release(); }
    }

    private sealed record CatalogEntry(string Id, string RelativePath, string ContentHash, DateTimeOffset AddedAt);
    private sealed record CatalogIndex(int Version, List<CatalogEntry> Entries);
}
