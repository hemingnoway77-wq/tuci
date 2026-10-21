using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VocabCollector.Core.Storage;
namespace VocabCollector.Core.Markdown;

public sealed record LinkRegionState(string Mode, string BodyHash);
public sealed record LegacyLinkImport(string Markdown, IReadOnlyList<LinkRegionState> Regions);

public static class ManagedLinks
{
    public static IReadOnlyList<LinkRegionState> CaptureState(string cleanDocument) => Sections(cleanDocument)
        .Select(section => new LinkRegionState(section.Mode, BodyHash(section.Text))).ToArray();

    public static string RefreshNew(string cleanDocument, string currentWord, VaultIndex index) =>
        Refresh(cleanDocument, currentWord, index, CaptureState(cleanDocument));

    /// <summary>Only sections matching separately stored checksums are eligible for automatic linking.</summary>
    public static string Refresh(string document, string currentWord, VaultIndex index, IReadOnlyList<LinkRegionState> state)
    {
        var result = document;
        foreach (var section in Sections(document).OrderByDescending(section => section.Start))
        {
            if (!Matches(section, state)) continue;
            var updated = RefreshBody(section.Text, section.Mode, currentWord, index);
            if (updated != section.Text) result = result[..section.Start] + updated + result[(section.Start + section.Length)..];
        }
        return result;
    }

    /// <summary>Unverified regions retain their old checksums so a refresh cannot adopt a personal edit.</summary>
    public static IReadOnlyList<LinkRegionState> AdvanceState(string original, string updated, IReadOnlyList<LinkRegionState> previous)
    {
        var before = Sections(original);
        var after = Sections(updated);
        return previous.Select(region =>
        {
            var prior = before.SingleOrDefault(section => section.Mode == region.Mode);
            var next = after.SingleOrDefault(section => section.Mode == region.Mode);
            return prior is not null && next is not null && Matches(prior, previous)
                ? region with { BodyHash = BodyHash(next.Text) } : region;
        }).ToArray();
    }

    /// <summary>Removes only paired v1 marker lines outside code fences, retaining their original edit protection.</summary>
    public static LegacyLinkImport? ImportLegacy(string document)
    {
        var lines = Lines(document);
        List<(Line Start, Line End, LinkRegionState Region)> pairs = [];
        (Line Line, Match Match)? pending = null;
        foreach (var line in lines.Where(line => line.Visible))
        {
            var start = LegacyStart.Match(line.Text);
            if (start.Success)
            {
                if (pending is not null) return null;
                pending = (line, start);
            }
            else if (line.Text == "<!-- /vocabcollector:links -->")
            {
                if (pending is null) return null;
                pairs.Add((pending.Value.Line, line, new(pending.Value.Match.Groups["mode"].Value,
                    pending.Value.Match.Groups["hash"].Value.ToLowerInvariant())));
                pending = null;
            }
            else if (line.Text.StartsWith("<!-- vocabcollector:links:v1 ", StringComparison.Ordinal)) return null;
        }
        if (pending is not null || pairs.Count == 0 || pairs.Select(pair => pair.Region.Mode).Distinct(StringComparer.Ordinal).Count() != pairs.Count) return null;

        var clean = document;
        foreach (var pair in pairs.OrderByDescending(pair => pair.Start.Start))
        {
            // Consume each marker's own line ending; all body and surrounding bytes stay unchanged.
            clean = clean.Remove(pair.End.Start, pair.End.Length);
            clean = clean.Remove(pair.Start.Start, pair.Start.Length);
        }
        return new(clean, pairs.Select(pair => pair.Region).ToArray());
    }

    internal static IReadOnlyList<string> CandidateWords(string document, IReadOnlyList<LinkRegionState> state)
    {
        HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
        foreach (var section in Sections(document).Where(section => Matches(section, state)))
        {
            foreach (var line in Lines(section.Text).Where(line => line.Visible))
            {
                var plain = StripOldLinks(line.Text);
                if (section.Mode == "examples")
                    foreach (Match example in ExampleLines.Matches(plain))
                        foreach (Match word in Words.Matches(example.Groups["sentence"].Value)) words.Add(NormalizeWord(word.Value));
                else
                    foreach (Match relation in RelationLines.Matches(plain)) words.Add(NormalizeWord(relation.Groups["word"].Value));
            }
        }
        return words.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool HasEditedBlocks(string document, IReadOnlyList<LinkRegionState> state)
    {
        var sections = Sections(document);
        return state.Any(region => !sections.Any(section => section.Mode == region.Mode && Matches(section, state)));
    }

    private static readonly Regex LegacyStart = new(@"^<!-- vocabcollector:links:v1 mode=(?<mode>examples|relations) sha256=(?<hash>[0-9a-fA-F]{64}) -->$", RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(@"^(?<level>#{1,6})[ \t]+(?<title>[^\r\n]*?)[ \t]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<tail>.*)$", RegexOptions.CultureInvariant);

    private sealed record Section(string Mode, int Start, int Length, string Text);
    private sealed record Line(int Start, int Length, string Text, bool Visible);

    private static IReadOnlyList<Section> Sections(string document)
    {
        var headings = Lines(document).Where(line => line.Visible).Select(line => (Line: line, Match: Heading.Match(line.Text)))
            .Where(item => item.Match.Success).ToArray();
        List<Section> sections = [];
        foreach (var mode in new[] { "examples", "relations" })
        {
            var title = mode == "examples" ? "例句" : "语义关系";
            var candidates = headings.Where(item => item.Match.Groups["level"].Length == 4 && item.Match.Groups["title"].Value == title).ToArray();
            if (candidates.Length != 1) continue;
            var heading = candidates[0].Line;
            var next = headings.FirstOrDefault(item => item.Line.Start > heading.Start && item.Match.Groups["level"].Length <= 4).Line;
            var end = next?.Start ?? document.Length;
            sections.Add(new(mode, heading.Start, end - heading.Start, document[heading.Start..end]));
        }
        return sections;
    }

    private static IReadOnlyList<Line> Lines(string document)
    {
        List<Line> lines = [];
        char fenceCharacter = '\0';
        var fenceLength = 0;
        var inComment = false;
        for (var start = 0; start < document.Length;)
        {
            var end = start;
            while (end < document.Length && document[end] is not ('\r' or '\n')) end++;
            var text = document[start..end];
            var next = end;
            if (next < document.Length && document[next] == '\r') next++;
            if (next < document.Length && document[next] == '\n') next++;
            var fence = Fence.Match(text);
            var visible = fenceCharacter == '\0' && !inComment && !fence.Success;
            if (fenceCharacter != '\0')
            {
                if (fence.Success && fence.Groups["fence"].Value[0] == fenceCharacter &&
                    fence.Groups["fence"].Length >= fenceLength && string.IsNullOrWhiteSpace(fence.Groups["tail"].Value)) fenceCharacter = '\0';
            }
            else if (!inComment && fence.Success)
            {
                fenceCharacter = fence.Groups["fence"].Value[0];
                fenceLength = fence.Groups["fence"].Length;
            }
            else
            {
                for (var position = 0; position < text.Length;)
                {
                    var marker = text.IndexOf(inComment ? "-->" : "<!--", position, StringComparison.Ordinal);
                    if (marker < 0) break;
                    position = marker + (inComment ? 3 : 4);
                    inComment = !inComment;
                }
            }
            lines.Add(new(start, next - start, text, visible));
            start = next;
        }
        return lines;
    }

    private static bool Matches(Section section, IReadOnlyList<LinkRegionState> state)
    {
        var candidates = state.Where(region => region.Mode == section.Mode).ToArray();
        return candidates.Length == 1 && BodyHash(section.Text).Equals(candidates[0].BodyHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string BodyHash(string body) => Hash(Normalize(body).TrimEnd('\n'));

    private static string RefreshBody(string body, string mode, string currentWord, VaultIndex index)
    {
        var updated = body;
        foreach (var line in Lines(body).Where(line => line.Visible).Reverse())
        {
            var plain = StripOldLinks(line.Text);
            var replacement = mode == "examples"
                ? ExampleLines.Replace(plain, match => match.Groups["prefix"].Value + LinkSentence(match.Groups["sentence"].Value, currentWord, index))
                : RelationLines.Replace(plain, match => match.Groups["prefix"].Value + LinkWord(match.Groups["word"].Value, currentWord, index) + match.Groups["suffix"].Value);
            if (replacement != line.Text) updated = updated[..line.Start] + replacement + updated[(line.Start + line.Text.Length)..];
        }
        return updated;
    }
    private static readonly Regex Words = new(@"(?<![A-Za-z0-9_])[A-Za-z]+(?:[-'’][A-Za-z]+)*(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
    private static readonly Regex ExampleLines = new(@"^(?<prefix>\s*\d+\.\s+)(?<sentence>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex RelationLines = new(@"^(?<prefix>[ \t]*-[ \t]+)(?<word>[A-Za-z]+(?:[-'’][A-Za-z]+)*)(?<suffix>[ \t]*[：:][^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex OldLinks = new(@"(?<![\\\[])\[\[(?<target>[^\[\]\r\n|]+)\|(?<word>[A-Za-z]+(?:[-'’][A-Za-z]+)*)\]\]", RegexOptions.CultureInvariant);
    private static readonly Regex ProtectedSyntax = new(@"\\\[[^\r\n]*?\\\]|\[\[[^\r\n]*?\]\]|\[[^\r\n]*?\]\([^\r\n]*?\)|`+[^`\r\n]*`+|&#91;[^\r\n]*?&#93;|https?://\S+|<[^>\r\n]*>|&(?:#\d+|#x[0-9a-fA-F]+|[A-Za-z]+);", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StopWords = new(("a an the and or but so because if when while as of in on at to for from by with without into over under about " +
        "is am are was were be been being do does did have has had can could may might must will would shall should " +
        "i me my mine we us our ours you your yours he him his she her hers it its they them their theirs this that these those " +
        "not no yes very too also just then than there here who what which where why how all any each every some such own same " +
        "up down out off again only even still more most much many few less other another both either neither").Split(' '), StringComparer.OrdinalIgnoreCase);

    private static string LinkSentence(string sentence, string currentWord, VaultIndex index)
    {
        var protectedSpans = ProtectedSyntax.Matches(sentence).Cast<Match>().ToArray();
        HashSet<string> usedTargets = new(StringComparer.OrdinalIgnoreCase);
        return Words.Replace(sentence, token =>
        {
            if (usedTargets.Count == 3 || StopWords.Contains(token.Value) ||
                protectedSpans.Any(span => token.Index < span.Index + span.Length && token.Index + token.Length > span.Index)) return token.Value;
            var target = Resolve(token.Value, currentWord, index);
            if (target is null || !usedTargets.Add(target.AbsolutePath)) return token.Value;
            return WikiLink(target, token.Value);
        });
    }

    private static string LinkWord(string word, string currentWord, VaultIndex index)
    {
        var target = Resolve(word, currentWord, index);
        return target is null ? word : WikiLink(target, word);
    }

    private static NoteLocation? Resolve(string word, string currentWord, VaultIndex index)
    {
        var normalized = NormalizeWord(word);
        if (normalized.Equals(NormalizeWord(currentWord), StringComparison.OrdinalIgnoreCase)) return null;
        var matches = index.Find(normalized);
        if (matches.Count != 1 || !VaultPaths.IsLinkable(matches[0])) return null;
        var note = matches[0];
        var relative = note.VaultRelativePath.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.IndexOfAny(['\r', '\n']) >= 0 ||
            relative.Split('/').Any(part => part is "" or "." or "..")) return null;
        return note;
    }

    private static string WikiLink(NoteLocation note, string display)
    {
        var target = note.VaultRelativePath.Replace('\\', '/');
        if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) target = target[..^3];
        return $"[[{target}|{display}]]";
    }

    private static string StripOldLinks(string body)
    {
        var literalSpans = ProtectedSyntax.Matches(body).Cast<Match>().Where(span => !span.Value.StartsWith("[[", StringComparison.Ordinal)).ToArray();
        return OldLinks.Replace(body, match => match.Groups["target"].Value.Contains('/') &&
            !literalSpans.Any(span => match.Index < span.Index + span.Length && match.Index + match.Length > span.Index)
            ? match.Groups["word"].Value : match.Value);
    }
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    private static string NormalizeWord(string text) => text.Replace('’', '\'').ToLowerInvariant();
    private static string Hash(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
}
