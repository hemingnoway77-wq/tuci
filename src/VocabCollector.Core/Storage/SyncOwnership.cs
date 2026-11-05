using VocabCollector.Core.Markdown;
using VocabCollector.Core.Sync;

namespace VocabCollector.Core.Storage;

public sealed partial class LinkRefresher
{
    // Caller owns the same write lock used by desktop capture and link maintenance.
    internal static async Task<Dictionary<string, Dictionary<string, string>>> ExportRegions(VaultOptions options, CancellationToken ct)
    {
        var root = VaultPaths.WordRoot(options);
        var data = await ReadOwnershipAsync(options.RootPath, Path.Combine(root, ".vocabcollector", "note-state.json"), ct);
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, note) in data.Notes)
        {
            var relative = Path.GetRelativePath(root, Path.Combine(options.RootPath, path)).Replace('\\','/');
            if (SyncFiles.IsNotePath(relative) && WordInput.FromFileStem(Path.GetFileNameWithoutExtension(relative)).Equals(note.Word, StringComparison.OrdinalIgnoreCase))
                result[relative] = note.Regions.ToDictionary(r => r.Mode, r => r.BodyHash);
        }
        return result;
    }

    internal static async Task ImportRegions(VaultOptions options, Dictionary<string, Dictionary<string, string>?> updates, CancellationToken ct)
    {
        if (updates.Count == 0) return;
        var root = VaultPaths.WordRoot(options);
        var dir = Path.Combine(root, ".vocabcollector");
        var file = Path.Combine(dir, "note-state.json");
        var ownership = await ReadOwnershipAsync(options.RootPath, file, ct);
        bool dirty = false;
        foreach (var (relative, regions) in updates)
        {
            var location = SyncFiles.Resolve(root, relative);
            var key = Path.GetRelativePath(options.RootPath, location).Replace('\\','/');
            if (regions is null || regions.Count == 0)
            {
                dirty |= ownership.Notes.Remove(key);
                continue;
            }
            if (regions.Count > 4 || regions.Any(r => r.Key is not ("examples" or "relations" or "forms" or "roots") ||
                r.Value == null || r.Value.Length != 64 || !r.Value.All(Uri.IsHexDigit)))
                throw new InvalidDataException("笔记分区校验信息无效。");
            string word;
            try { word = WordInput.Normalize(WordInput.FromFileStem(Path.GetFileNameWithoutExtension(location))); }
            catch (ArgumentException) { dirty |= ownership.Notes.Remove(key); continue; }
            ownership.Notes[key] = new OwnedNote { Word = word, Regions = regions.Select(r => new LinkRegionState(r.Key, r.Value.ToLowerInvariant())).ToArray() };
            dirty = true;
        }
        if (dirty) await WriteStateAsync(options.RootPath, dir, file, ownership, ct);
    }
}
