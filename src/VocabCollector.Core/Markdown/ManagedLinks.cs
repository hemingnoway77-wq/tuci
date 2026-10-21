using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VocabCollector.Core.Storage;
namespace VocabCollector.Core.Markdown;

public static class ManagedLinks
{
    private static readonly Regex Blocks = new(@"^<!-- vocabcollector:links:v1 mode=(?<mode>examples|relations) sha256=(?<hash>[0-9a-fA-F]{64}) -->\r?\n(?<body>.*?)\r?\n<!-- /vocabcollector:links -->", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
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

    /// <summary>The checksum protects user edits: only exactly matching generated blocks can be refreshed.</summary>
    public static string Wrap(string mode, string body)
    {
        if (mode is not ("examples" or "relations")) throw new ArgumentException("不支持的自动链接区域。", nameof(mode));
        var normalized = Normalize(body).TrimEnd('\n');
        return $"<!-- vocabcollector:links:v1 mode={mode} sha256={Hash(normalized)} -->\n{normalized}\n<!-- /vocabcollector:links -->";
    }

    public static string Refresh(string document, string currentWord, VaultIndex index) => Blocks.Replace(document, block =>
    {
        var body = Normalize(block.Groups["body"].Value);
        if (!Hash(body).Equals(block.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase)) return block.Value;
        var plain = StripOldLinks(body);
        var mode = block.Groups["mode"].Value;
        var updated = mode == "examples"
            ? ExampleLines.Replace(plain, line => line.Groups["prefix"].Value + LinkSentence(line.Groups["sentence"].Value, currentWord, index))
            : RelationLines.Replace(plain, line => line.Groups["prefix"].Value + LinkWord(line.Groups["word"].Value, currentWord, index) + line.Groups["suffix"].Value);
        return updated == body ? block.Value : Wrap(mode, updated);
    });

    internal static bool HasBlocks(string document) => document.Contains("<!-- vocabcollector:links:v1 ", StringComparison.Ordinal);

    internal static bool HasEditedBlocks(string document)
    {
        var blocks = Blocks.Matches(document).Cast<Match>().ToArray();
        var starts = Regex.Matches(document, @"^<!-- vocabcollector:links:v1 ", RegexOptions.Multiline | RegexOptions.CultureInvariant).Count;
        return starts != blocks.Length || blocks.Any(block => !Hash(Normalize(block.Groups["body"].Value)).Equals(block.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> CandidateWords(string document)
    {
        HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in Blocks.Matches(document))
        {
            var body = Normalize(block.Groups["body"].Value);
            if (!Hash(body).Equals(block.Groups["hash"].Value, StringComparison.OrdinalIgnoreCase)) continue;
            var plain = StripOldLinks(body);
            if (block.Groups["mode"].Value == "examples")
                foreach (Match line in ExampleLines.Matches(plain))
                    foreach (Match word in Words.Matches(line.Groups["sentence"].Value)) words.Add(NormalizeWord(word.Value));
            else
                foreach (Match line in RelationLines.Matches(plain)) words.Add(NormalizeWord(line.Groups["word"].Value));
        }
        return words.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

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
