using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VocabCollector.Core.Storage;

namespace VocabCollector.Core.Sync;

internal static class SyncFiles
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex Device = new(@"^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$|CLOCK\$)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsNotePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length <= 1024 && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
        path.Split('/').All(part => part.Length > 0 && Utf8.GetByteCount(part) <= 255 && !part.StartsWith('.') && !part.EndsWith('.') &&
            !part.EndsWith(' ') && !part.Any(c => char.IsControl(c) || "<>:\"|?*\\".Contains(c)) && !Device.IsMatch(part.Split('.')[0].TrimEnd()));

    internal static string Resolve(string root, string relative)
    {
        if (!IsNotePath(relative)) throw new InvalidDataException($"笔记路径不兼容：{relative}");
        var target = Path.GetFullPath(Path.Combine(root, relative));
        VaultPaths.EnsureInside(root, target);
        // GetAttributes also sees dangling links for which File.Exists returns false.
        var belowRoot = true;
        for (var cursor = target; cursor != null; cursor = Path.GetDirectoryName(cursor))
        {
            try
            {
                if (Paths.Equals(cursor, root)) belowRoot = false;
                var attributes = File.GetAttributes(cursor);
                if (belowRoot && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    throw new IOException("已同步的路径现在指向隐藏或系统文件，同步已暂停。");
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("同步路径包含文件或目录链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return target;
    }

    internal static Dictionary<string, string> Snapshot(string root, CancellationToken ct)
    {
        var notes = new Dictionary<string, string>(Paths);
        var dirs = new Stack<string>();
        dirs.Push(root);
        while (dirs.TryPop(out var dir))
        {
            VaultPaths.EnsureInside(root, dir);
            foreach (var item in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(item).StartsWith('.')) continue;
                var attributes = File.GetAttributes(item);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (attributes.HasFlag(FileAttributes.Directory)) { dirs.Push(item); continue; }
                if (!item.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(root, item).Replace('\\','/');
                var fullPath = Resolve(root, relative);
                if (new FileInfo(fullPath).Length > 1024 * 1024 + 3) throw new InvalidDataException($"笔记超过 1 MiB，请缩短后重试：{relative}");
                var text = Read(fullPath);
                if (Utf8.GetByteCount(text) > 1024 * 1024) throw new InvalidDataException($"笔记超过 1 MiB，请缩短后重试：{relative}");
                if (!notes.TryAdd(relative, text)) throw new IOException("词库中有仅大小写不同的同名笔记。");
            }
        }
        return notes;
    }

    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(value)));
    internal static string Read(string path) => Utf8.GetString(File.ReadAllBytes(path)).TrimStartOnceBom();

    private static string TrimStartOnceBom(this string text) => text.StartsWith('\uFEFF') ? text[1..] : text;
    internal static string? Current(string path)
    {
        try { return Read(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    // ReplaceFile/rename preserve the exact bytes displaced, even if Obsidian saves between the check and commit.
    internal static void Apply(string root, string relative, string? content, string? expectedHash)
    {
        var path = Resolve(root, relative);
        if (Directory.Exists(path))
        {
            if (content == null) return;
            // A cloud rename can reuse a now-empty parent directory as the new file.
            // Removing an empty folder is safe; a non-empty directory fails without losing data.
            Directory.Delete(path, recursive: false);
        }
        var current = Current(path);
        if (current == content) return;
        var backups = Path.Combine(root, ".vocabcollector", "backups");
        VaultPaths.EnsureInside(root, backups);
        Directory.CreateDirectory(backups);
        var backup = Path.Combine(backups, Hash(relative)[..16] + "." + Guid.NewGuid().ToString("N") + ".bak");
        VaultPaths.EnsureInside(root, backup);
        if (content == null)
        {
            File.Move(path, backup);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = Path.Combine(Path.GetDirectoryName(path)!, ".vocab-sync-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, content, Utf8);
                Resolve(root, relative);
                if (current == null) File.Move(temp, path);
                else File.Replace(temp, path, backup);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        if (current == null) return;
        VaultPaths.EnsureInside(root, backup);
        var displaced = Read(backup);
        if (Hash(displaced) == expectedHash || displaced == content) return;
        // A concurrently edited note stays visible. Its next normal scan uploads this independent copy.
        var conflict = Path.Combine(Path.GetDirectoryName(path)!, "本机冲突-" + Guid.NewGuid().ToString("N") + ".md");
        VaultPaths.EnsureInside(root, conflict);
        File.Copy(backup, conflict);
    }
}
