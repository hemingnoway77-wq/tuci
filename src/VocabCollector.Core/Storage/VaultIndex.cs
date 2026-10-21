namespace VocabCollector.Core.Storage;
public sealed class VaultIndex(IReadOnlyList<NoteLocation> notes)
{
    public IReadOnlyList<NoteLocation> Notes { get; } = Array.AsReadOnly(notes.ToArray());
    private readonly Dictionary<string, IReadOnlyList<NoteLocation>> _byWord = notes
        .GroupBy(n => n.Word, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => (IReadOnlyList<NoteLocation>)Array.AsReadOnly(g.ToArray()), StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<NoteLocation> Find(string word) => _byWord.TryGetValue(word, out var matches) ? matches : [];
    public IReadOnlyList<ResolvedRelation> ResolveRelated(string currentWord, IReadOnlyList<RelatedCandidate> candidates)
    {
        List<ResolvedRelation> result = [];
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.Take(12))
        {
            string word;
            try { word = WordInput.Normalize(candidate.Word); } catch (ArgumentException) { continue; }
            if (word.Equals(currentWord, StringComparison.OrdinalIgnoreCase) || !Enum.IsDefined(candidate.Kind)) continue;
            var matches = Find(word);
            if (matches.Count != 1 || !VaultPaths.IsLinkable(matches[0]) || !used.Add(matches[0].AbsolutePath)) continue;
            result.Add(new(candidate.Kind, matches[0]));
            if (result.Count == 8) break;
        }
        return result;
    }
}
