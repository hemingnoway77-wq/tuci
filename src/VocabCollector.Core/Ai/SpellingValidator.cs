using System.Text.RegularExpressions;
namespace VocabCollector.Core.Ai;

public static class SpellingValidator
{
    private static readonly Regex Words = new("[a-z0-9]+(?:[-'][a-z0-9]+)*", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string ResolveWord(string input, WordAnalysis analysis)
    {
        try
        {
            var normalized = WordInput.Normalize(input);
            var spelling = analysis.Spelling;
            if (spelling is null)
            {
                if (analysis.IsValidWord) throw Invalid();
                return ""; // Old rejection responses are safe: they can never produce a note.
            }
            if (WordInput.Normalize(spelling.InputWord) != normalized || spelling.Suggestions is null || spelling.Suggestions.Count > 3) throw Invalid();
            foreach (var suggestion in spelling.Suggestions) WordInput.Normalize(suggestion);
            if (!analysis.IsValidWord)
            {
                if (spelling.Status is not (SpellingStatus.Uncertain or SpellingStatus.Invalid) || !string.IsNullOrWhiteSpace(spelling.Word)) throw Invalid();
                return "";
            }
            var word = WordInput.Normalize(spelling.Word);
            if (spelling.Suggestions.Count != 0) throw Invalid();
            switch (spelling.Status)
            {
                case SpellingStatus.Valid when word == normalized:
                    break;
                case SpellingStatus.Corrected when word != normalized:
                    var limit = normalized.Length <= 4 ? 1 : normalized.Length <= 9 ? 2 : 3;
                    // A missing final letter (busines -> business) can resemble inflection.
                    // Reject the reverse direction only when the model identifies the input as the lemma.
                    var changesKnownBase = IsRegularInflection(word, normalized) && !string.IsNullOrWhiteSpace(analysis.Lemma)
                        && WordInput.Normalize(analysis.Lemma) == normalized;
                    if (IsRegularInflection(normalized, word) || changesKnownBase || Distance(normalized, word) > limit) throw Invalid();
                    break;
                default:
                    throw Invalid();
            }
            if (analysis.Examples.Count != 2 || analysis.Examples.Any(example =>
                !Words.Matches(example.English.Replace('’', '\'').Replace('‘', '\'').ToLowerInvariant()).Any(token => token.Value == word))) throw Invalid();
            return word;
        }
        catch (ArgumentException) { throw Invalid(); }
    }

    // Inflection information belongs in lemma/form_note, never in a spelling correction.
    private static bool IsRegularInflection(string input, string lemma)
    {
        if (input == lemma + "s" || input == lemma + "es" || input == lemma + "ed" || input == lemma + "d" || input == lemma + "ing"
            || input == lemma + "er" || input == lemma + "est") return true;
        if (lemma.EndsWith('y') && (input == lemma[..^1] + "ies" || input == lemma[..^1] + "ied"
            || input == lemma[..^1] + "ier" || input == lemma[..^1] + "iest")) return true;
        if (lemma.EndsWith('e') && (input == lemma[..^1] + "ing" || input == lemma + "r" || input == lemma + "st")) return true;
        return lemma.Length > 1 && lemma[^1] is not ('a' or 'e' or 'i' or 'o' or 'u')
            && (input == lemma + lemma[^1] + "ed" || input == lemma + lemma[^1] + "ing"
                || input == lemma + lemma[^1] + "er" || input == lemma + lemma[^1] + "est");
    }

    private static int Distance(string source, string target)
    {
        var costs = new int[source.Length + 1, target.Length + 1];
        for (var i = 0; i <= source.Length; i++) costs[i, 0] = i;
        for (var j = 0; j <= target.Length; j++) costs[0, j] = j;
        for (var i = 1; i <= source.Length; i++)
            for (var j = 1; j <= target.Length; j++)
            {
                costs[i, j] = Math.Min(Math.Min(costs[i - 1, j] + 1, costs[i, j - 1] + 1), costs[i - 1, j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && source[i - 1] == target[j - 2] && source[i - 2] == target[j - 1])
                    costs[i, j] = Math.Min(costs[i, j], costs[i - 2, j - 2] + 1);
            }
        return costs[source.Length, target.Length];
    }

    private static AnalysisException Invalid() => new("AI 返回的拼写判断与解析不一致，请重试或更换模型。");
}
