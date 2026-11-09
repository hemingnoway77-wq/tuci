using System.IO.Compression;

namespace VocabCollector.Core.Updates;

public static class UpdateArchive
{
    /// <summary>Extracts an already authenticated package into a new or empty directory.</summary>
    public static async Task ExtractAsync(string packagePath, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(destinationDirectory);
        RejectLinksInPath(destination);
        var existed = Directory.Exists(destination);
        if (File.Exists(destination) || existed && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidDataException("更新包只能解压到新目录或空目录。");

        await using var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (package.Length > UpdatePolicy.MaximumPackageBytes) throw new InvalidDataException("更新压缩包过大。");
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var entries = ValidateEntries(archive, cancellationToken);
        var createdFiles = new List<string>();
        var createdDirectories = new List<string>();
        try
        {
            EnsureDirectory(destination, createdDirectories);
            var buffer = new byte[81920];
            long extracted = 0;
            foreach (var (entry, relativePath, isDirectory) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar));
                RejectLinksInPath(path);
                if (isDirectory)
                {
                    EnsureDirectory(path, createdDirectories);
                    continue;
                }
                EnsureDirectory(Path.GetDirectoryName(path)!, createdDirectories);
                RejectLinksInPath(path);
                await using var source = entry.Open();
                await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                createdFiles.Add(path);
                long entryBytes = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    entryBytes += read;
                    extracted += read;
                    if (entryBytes > entry.Length || extracted > UpdatePolicy.MaximumExtractedBytes)
                        throw new InvalidDataException("更新包解压大小超出限制。");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (entryBytes != entry.Length) throw new InvalidDataException("更新压缩包文件长度无效。");
            }
        }
        catch
        {
            // Only remove paths this invocation created; never recursively traverse a possibly replaced directory.
            foreach (var path in createdFiles.AsEnumerable().Reverse())
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var path in createdDirectories.AsEnumerable().Reverse())
                try { Directory.Delete(path, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static List<(ZipArchiveEntry Entry, string Path, bool IsDirectory)> ValidateEntries(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > UpdatePolicy.MaximumArchiveEntries) throw new InvalidDataException("更新压缩包文件数量过多。");
        var result = new List<(ZipArchiveEntry, string, bool)>();
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var canonicalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || unixType is not (0 or 0x8000 or 0x4000))
                throw new InvalidDataException("更新包不允许符号链接、重解析点或特殊文件。");
            var name = entry.FullName;
            var isDirectory = name.EndsWith('/');
            if (name.Length == 0 || name.Length > 240 || name.Contains('\\') || name.StartsWith('/')
                || unixType == 0x4000 && !isDirectory || unixType == 0x8000 && isDirectory)
                throw new InvalidDataException("更新包包含无效路径。");
            var path = isDirectory ? name[..^1] : name;
            var segments = path.Split('/');
            if (segments.Any(InvalidSegment)) throw new InvalidDataException("更新包包含越界或 Windows 不安全路径。");
            RegisterSpelling(path, canonicalPaths);
            if (!explicitPaths.Add(path)) throw new InvalidDataException("更新包包含重复或大小写冲突的文件。");
            for (var i = 1; i < segments.Length; i++)
            {
                var parent = string.Join('/', segments, 0, i);
                RegisterSpelling(parent, canonicalPaths);
                if (paths.TryGetValue(parent, out var parentIsDirectory) && !parentIsDirectory)
                    throw new InvalidDataException("更新包文件与目录路径冲突。");
                paths[parent] = true;
            }
            if (paths.TryGetValue(path, out var existingDirectory) && (!existingDirectory || !isDirectory))
                throw new InvalidDataException("更新包文件与目录路径冲突。");
            paths[path] = isDirectory;
            if (entry.Length < 0 || isDirectory && entry.Length != 0 || entry.Length > UpdatePolicy.MaximumPackageBytes
                || entry.Length > UpdatePolicy.MaximumExtractedBytes - totalLength)
                throw new InvalidDataException("更新包解压大小超出限制。");
            totalLength += entry.Length;
            result.Add((entry, path, isDirectory));
        }
        if (!paths.TryGetValue(UpdateConfiguration.AppExecutableName, out var appDirectory) || appDirectory
            || !paths.TryGetValue(UpdateConfiguration.UpdaterExecutableName, out var updaterDirectory) || updaterDirectory)
            throw new InvalidDataException("更新包根目录必须包含兔词.exe和兔词更新器.exe。");
        return result;
    }

    private static void RegisterSpelling(string path, Dictionary<string, string> canonicalPaths)
    {
        if (canonicalPaths.TryGetValue(path, out var canonical) && !string.Equals(canonical, path, StringComparison.Ordinal))
            throw new InvalidDataException("更新包目录或文件的大小写冲突。");
        canonicalPaths[path] = path;
    }

    private static bool InvalidSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.Any(c => c < 32 || c is ':' or '<' or '>' or '"' or '|' or '?' or '*')) return true;
        var stem = segment.Split('.')[0].TrimEnd(' ', '.');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }

    private static void RejectLinksInPath(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("更新解压路径不能包含符号链接或重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void EnsureDirectory(string path, List<string> created)
    {
        RejectLinksInPath(path);
        if (Directory.Exists(path)) return;
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) EnsureDirectory(parent, created);
        Directory.CreateDirectory(path);
        created.Add(path);
        RejectLinksInPath(path);
    }
}
