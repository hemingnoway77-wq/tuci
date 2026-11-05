using System.Net;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace VocabCollector.Core.Library;

public sealed record LibraryHeading(int Level, string Text, int Index);

public sealed class LibraryGraph(LibraryCatalog catalog, IReadOnlyList<LibraryNote> notes)
{
    private readonly ConcurrentDictionary<(string Id, string RelativePath, string Markdown), IReadOnlyList<LibraryNote>> _outgoing = new();

    public IReadOnlyList<LibraryNote> Outgoing(LibraryNote note) => _outgoing.GetOrAdd((note.Id, note.RelativePath, note.Markdown), _ => ReadOutgoing(note));

    private IReadOnlyList<LibraryNote> ReadOutgoing(LibraryNote note)
    {
        var source = string.Join("\n", VisibleLines(note.Markdown));
        source = Regex.Replace(source, @"(?s)(`+).*?\1", "");
        var targets = Regex.Matches(source, @"(?<![!\\])\[\[([^\]\r\n]+)\]\]").Select(match => match.Groups[1].Value).ToList();
        foreach (Match match in Regex.Matches(source, "(?<![!\\\\])\\[[^\\]\\r\\n]+\\]\\(\\s*(?:<(?<path>[^>\\r\\n]+)>|(?<path>(?:\\\\.|[^\\\\\\s()]|\\([^()]*\\))+))(?:\\s+[\"'][^\"'\\r\\n]*[\"'])?\\s*\\)"))
            targets.Add(Regex.Replace(match.Groups["path"].Value, @"\\([() ])", "$1"));
        var resolved = new List<LibraryNote>();
        foreach (var target in targets)
        {
            var choices = catalog.Resolve(target, note, notes);
            // A namesake is only a possible destination until the reader chooses one.
            if (choices.Count == 1) resolved.Add(choices[0]);
        }
        return resolved.DistinctBy(target => target.Id).ToArray();
    }

    public IReadOnlyList<LibraryNote> Backlinks(LibraryNote note) => notes
        .Where(source => Outgoing(source).Any(target => target.Id == note.Id)).DistinctBy(source => source.Id).ToArray();

    public static IReadOnlyList<LibraryHeading> Headings(string markdown)
    {
        var result = new List<LibraryHeading>();
        var lines = VisibleLines(markdown);
        for (var i = 0; i < lines.Length; i++)
        {
            var atx = Regex.Match(lines[i], @"^ {0,3}(#{1,6})(?:[ \t]+|$)(.*)$");
            if (atx.Success)
            {
                var text = Regex.Replace(atx.Groups[2].Value, @"[ \t]+#+[ \t]*$", "");
                result.Add(new(atx.Groups[1].Length, PlainHeading(text), result.Count));
            }
            else if (i > 0 && Regex.IsMatch(lines[i], @"^ {0,3}(=+|-+)[ \t]*$") &&
                !string.IsNullOrWhiteSpace(lines[i - 1]) && !Regex.IsMatch(lines[i - 1], @"^ {0,3}#{1,6}(?:\s|$)"))
                result.Add(new(lines[i].TrimStart().StartsWith('=') ? 1 : 2, PlainHeading(lines[i - 1]), result.Count));
        }
        return result;
    }

    private static string[] VisibleLines(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');
        text = Regex.Replace(text, @"(?s)<!--.*?-->", "");
        var lines = text.Split('\n');
        if (lines.Length > 1 && lines[0] == "---")
        {
            var end = Array.FindIndex(lines, 1, line => line is "---" or "...");
            if (end >= 0) Array.Fill(lines, "", 0, end + 1);
        }
        char? fence = null;
        var fenceLength = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (fence is { } marker)
            {
                if (Regex.IsMatch(lines[i], "^ {0,3}" + Regex.Escape(new string(marker, fenceLength)) + Regex.Escape(marker.ToString()) + "*[ \\t]*$")) fence = null;
                lines[i] = "";
                continue;
            }
            var opening = Regex.Match(lines[i], @"^ {0,3}(`{3,}|~{3,})");
            if (opening.Success)
            { fence = opening.Groups[1].Value[0]; fenceLength = opening.Groups[1].Length; lines[i] = ""; }
            else if (lines[i].StartsWith("    ", StringComparison.Ordinal) || lines[i].StartsWith('\t')) lines[i] = "";
        }
        return lines;
    }

    private static string PlainHeading(string source)
    {
        var text = Regex.Replace(source, @"\[\[([^\]|]+)\|([^\]]+)\]\]", "$2");
        text = Regex.Replace(text, @"\[\[([^\]]+)\]\]", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = Regex.Replace(text, @"[`*_~]", "");
        return WebUtility.HtmlDecode(text).Trim();
    }
}
