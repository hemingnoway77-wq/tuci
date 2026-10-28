using System.Text.RegularExpressions;

namespace VocabCollector.Core;

internal static class WordForms
{
    private static readonly Regex Words = new("[A-Za-z0-9]+(?:[-'’‘][A-Za-z0-9]+)*", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static IReadOnlySet<string> ForExamples(string word, WordAnalysis analysis)
    {
        var normalized = WordInput.Normalize(word);
        HashSet<string> forms = new(StringComparer.OrdinalIgnoreCase) { normalized };
        // Only a confirmed base verb may use its regular third-person form in an example.
        // The card's spelling and inflected inputs still retain their exact surface form.
        if (!analysis.IsValidWord || !normalized.Equals(analysis.Lemma.Trim(), StringComparison.OrdinalIgnoreCase)
            || !analysis.Meanings.Any(meaning => meaning.PartOfSpeech == PartOfSpeech.Verb)
            || normalized.Any(c => c is < 'a' or > 'z')) return forms;
        // Avoid irregular forms and endings whose spelling needs additional lexical knowledge.
        if (normalized is "be" or "have" || normalized.EndsWith('o')
            || normalized.EndsWith('z') && !normalized.EndsWith("zz", StringComparison.Ordinal)) return forms;

        var thirdPerson = normalized.Length > 1 && normalized.EndsWith('y') && !"aeiou".Contains(normalized[^2])
            ? normalized[..^1] + "ies"
            : normalized.EndsWith('s') || normalized.EndsWith('x') || normalized.EndsWith('z')
                || normalized.EndsWith("ch", StringComparison.Ordinal) || normalized.EndsWith("sh", StringComparison.Ordinal)
                ? normalized + "es"
                : normalized + "s";
        forms.Add(thirdPerson);
        return forms;
    }

    public static IEnumerable<Match> FindMatches(string text, IReadOnlySet<string> forms)
    {
        foreach (Match token in Words.Matches(text))
            if (forms.Contains(token.Value.Replace('’', '\'').Replace('‘', '\''))) yield return token;
    }
}
