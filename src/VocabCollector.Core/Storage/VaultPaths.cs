namespace VocabCollector.Core.Storage;

public static class VaultPaths
{
    private static readonly char[] LinkReserved = ['#', '^', '[', ']', '|'];
    private static readonly string[] Folders = ["名词", "动词", "形容词", "副词", "代词", "介词", "连词", "感叹词", "限定词", "数词", "其他"];
    public static string PartFolder(PartOfSpeech part) => Enum.IsDefined(part) ? Folders[(int)part] : throw new ArgumentException("词性无效。");
    public static string LinkTarget(NoteLocation note) => note.ObsidianRelativePath ?? note.VaultRelativePath;
    public static string ObsidianRoot(VaultOptions options)
    {
        var wordRoot = WordRoot(options);
        var configuredRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        for (string? current = wordRoot; current is not null; current = Path.GetDirectoryName(current))
        {
            var marker = Path.Combine(current, ".obsidian");
            if (Directory.Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) == 0)
                return current;
            if (current.Equals(configuredRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
        return configuredRoot;
    }
    public static string WordRoot(VaultOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath) || !Path.IsPathFullyQualified(options.RootPath) || !Directory.Exists(options.RootPath))
            throw new ArgumentException("请选择已经存在的 Obsidian 仓库文件夹。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
        var sub = options.OutputSubfolder.Trim();
        if (sub.Length == 0 || Path.IsPathRooted(sub) || sub.Contains(':')) throw new ArgumentException("单词目录必须是仓库内的相对目录。");
        var parts = sub.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.IndexOfAny(LinkReserved) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(part.Split('.')[0], @"\A(?:con|prn|aux|nul|com[1-9]|lpt[1-9])\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new ArgumentException("单词目录含有不支持的名称或字符，请避开 # ^ [ ] | 和路径跳转。");
        }
        var result = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        EnsureInside(root, result);
        return result;
    }
    public static void EnsureInside(string root, string target)
    {
        var absolute = Path.GetFullPath(target);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!absolute.Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
            !absolute.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目标路径必须位于选定仓库内。");
        EnsureNoReparse(absolute);
    }
    private static void EnsureNoReparse(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("路径含有目录链接，无法安全写入；请选择普通文件夹。");
        }
    }
    public static bool IsLinkable(NoteLocation note)
    {
        try
        {
            var target = LinkTarget(note).Replace('\\', '/');
            if (!File.Exists(note.AbsolutePath) || string.IsNullOrWhiteSpace(target) || Path.IsPathRooted(target) ||
                target.IndexOfAny(LinkReserved) >= 0 || target.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || target.Contains(':') ||
                target.Split('/').Any(part => part is "" or "." or "..")) return false;
            EnsureNoReparse(note.AbsolutePath);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
