using System.IO;
using VocabCollector.Core;
using VocabCollector.Core.Storage;
namespace VocabCollector.Desktop;

public static class PreviewLinkResolver
{
    public static NoteLocation? Resolve(string target, VaultOptions options, NoteLocation? source)
    {
        // Links in a note are data. Only an existing Markdown file inside the configured vocabulary may be opened.
        var value = target.Split('#', 2)[0].Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(':') || value.Contains('%') ||
            value.Split('/').Any(part => part is "" or "." or "..")) return null;
        if (!Path.HasExtension(value)) value += ".md";
        if (!Path.GetExtension(value).Equals(".md", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetFullPath(options.RootPath);
        var wordRoot = VaultPaths.WordRoot(options);
        var relative = value.Replace('/', Path.DirectorySeparatorChar);
        List<string> candidates = [Path.Combine(VaultPaths.ObsidianRoot(options), relative), Path.Combine(root, relative)];
        if (source is not null && !value.Contains('/')) candidates.Add(Path.Combine(Path.GetDirectoryName(source.AbsolutePath)!, value));
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                VaultPaths.EnsureInside(root, path); VaultPaths.EnsureInside(wordRoot, path);
                if (File.Exists(path)) return new(Path.GetFileNameWithoutExtension(path), path, Path.GetRelativePath(root, path).Replace('\\', '/'));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return null;
    }
}
