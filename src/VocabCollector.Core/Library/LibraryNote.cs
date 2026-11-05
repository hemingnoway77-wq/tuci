using System.Globalization;
using System.Text.RegularExpressions;

namespace VocabCollector.Core.Library;

public sealed record LibraryNote(string Id, string RelativePath, string AbsolutePath, string Markdown, DateTimeOffset AddedAt)
{
    private string NormalizedPath => RelativePath.Replace('\\', '/');
    public string Title
    {
        get
        {
            var stem = Path.GetFileNameWithoutExtension(NormalizedPath);
            return stem.ToLowerInvariant() is "_con" or "_aux" or "_prn" or "_nul" ? stem[1..] : stem;
        }
    }
    public string Folder => NormalizedPath.Contains('/') ? NormalizedPath[..NormalizedPath.LastIndexOf('/')] : "";
    public bool IsWord => !NormalizedPath.StartsWith("词根/", StringComparison.OrdinalIgnoreCase) &&
        Title.Length <= 64 && Regex.IsMatch(Title, "\\A[a-z]+(?:[-'][a-z]+)*\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Body
    {
        get
        {
            var text = Markdown.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');
            if (!text.StartsWith("---\n", StringComparison.Ordinal)) return text;
            var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
            return end >= 0 ? text[(end + 5)..] : text;
        }
    }

    public string Phonetics => Regex.Match(Body, @"(?m)^\*\*发音\*\*[：:]\s*([^\n]*)").Groups[1].Value.Trim();

    public string Meaning
    {
        get
        {
            var lines = Body.Split('\n');
            var start = Array.FindIndex(lines, line => line.Contains("**释义**", StringComparison.Ordinal));
            return start < 0 ? "" : string.Join("\n", lines.Skip(start + 1).SkipWhile(string.IsNullOrWhiteSpace)
                .TakeWhile(line => line.TrimStart().StartsWith('>')).Select(line => line.TrimStart()[1..].Trim()));
        }
    }

    public string Excerpt
    {
        get
        {
            var context = Regex.Match(Body, @"(?ms)^#{1,6}\s+遇见时的原句\s*\n(.*?)(?=^#{1,6}\s|\z)");
            var text = context.Success ? context.Groups[1].Value : Body;
            text = Regex.Replace(text, @"(?m)^\s*[#>]+\s*", "").Trim();
            return text.Length <= 240 ? text : text[..240] + "…";
        }
    }

    internal static DateTimeOffset? CreationDate(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return null;
        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return null;
        var match = Regex.Match(normalized[..end], @"(?m)^created:\s*([^\n]+)");
        var value = match.Groups[1].Value.Trim().Trim('\'', '"');
        return match.Success && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var result) ? result : null;
    }
}

public sealed record LibrarySnapshot(IReadOnlyList<LibraryNote> Notes, IReadOnlyList<string> Warnings);
