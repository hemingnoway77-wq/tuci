namespace VocabCollector.Core.Storage;
public sealed class VaultIndex(IReadOnlyList<NoteLocation> notes)
{
    private readonly IReadOnlyList<NoteLocation> _notes = notes;
    public IReadOnlyList<NoteLocation> Find(string word) => _notes.Where(n => n.Word.Equals(word, StringComparison.OrdinalIgnoreCase)).ToArray();
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
