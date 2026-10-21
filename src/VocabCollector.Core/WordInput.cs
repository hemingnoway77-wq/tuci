using System.Text.RegularExpressions;
namespace VocabCollector.Core;
public static class WordInput
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "con", "aux", "prn", "nul" };
    public static string Normalize(string raw)
    {
        var word = (raw ?? "").Trim().Replace('’', '\'').Replace('‘', '\'').ToLowerInvariant();
        if (word.Length is < 1 or > 64 || !Regex.IsMatch(word, "\\A[a-z]+(?:[-'][a-z]+)*\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("请输入一个不超过 64 个字符的英文单词，可包含内部连字符或撇号。");
        return word;
    }
    public static string ToFileName(string normalized)
    {
        var word = Normalize(normalized);
        return (Reserved.Contains(word) ? "_" : "") + word + ".md";
    }
    public static string FromFileStem(string stem)
    {
        var lower = stem.ToLowerInvariant();
        return lower.StartsWith('_') && Reserved.Contains(lower[1..]) ? lower[1..] : lower;
    }
}
