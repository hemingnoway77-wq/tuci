namespace VocabCollector.Updater;

/// <summary>Rejects reparse points at every existing path component, including file leaves.</summary>
public static class InstallPathSafety
{
    public static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("更新路径必须是绝对路径。");
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
            throw new InvalidDataException("更新路径必须位于本地磁盘，且不能包含备用数据流。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool IsInside(string path, string directory) =>
        FullPath(path).StartsWith(FullPath(directory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void RequireInside(string path, string directory)
    {
        if (!IsInside(path, directory)) throw new InvalidDataException("更新路径越出了事务目录。");
    }

    public static void RequireNoLinks(string path)
    {
        var current = FullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("更新路径不能包含符号链接或目录联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    public static void RequireNonRootDirectory(string path)
    {
        path = FullPath(path);
        if (string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("不能向磁盘根目录安装更新。");
        RequireNoLinks(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("应用安装目录不存在。");
    }
}
