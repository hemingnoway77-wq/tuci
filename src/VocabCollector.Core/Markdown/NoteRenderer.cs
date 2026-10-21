using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VocabCollector.Core.Storage;
namespace VocabCollector.Core.Markdown;

public sealed class NoteRenderer
{
    public string Render(string word, string context, WordAnalysis analysis, IReadOnlyList<ResolvedRelation> relations, DateTimeOffset created)
    {
        var text = new StringBuilder();
        text.AppendLine("---").AppendLine("type: english-word").AppendLine("word: " + JsonSerializer.Serialize(word));
        text.AppendLine("primary_pos: " + analysis.PrimaryPartOfSpeech.ToString().ToLowerInvariant()).AppendLine("parts_of_speech:");
        foreach (var pos in analysis.Meanings.Select(m => m.PartOfSpeech).Distinct()) text.AppendLine("  - " + pos.ToString().ToLowerInvariant());
        text.AppendLine("created: " + JsonSerializer.Serialize(created.ToString("O"))).AppendLine("tags:\n  - english\n  - vocabulary\n---\n");
        text.AppendLine("# " + Plain(word)).AppendLine("\n## 音标");
        text.AppendLine("- 英：" + Phonetic(analysis.UkPhonetic)).AppendLine("- 美：" + Phonetic(analysis.UsPhonetic));
        text.AppendLine("\n## 词性与释义");
        foreach (var meaning in analysis.Meanings) text.AppendLine("- **" + VaultPaths.PartFolder(meaning.PartOfSpeech) + "**：" + Plain(meaning.Definition));
        text.AppendLine("\n## 例句");
        for (var i = 0; i < analysis.Examples.Count; i++)
        {
            text.AppendLine($"{i + 1}. {Plain(analysis.Examples[i].English)}");
            text.AppendLine("   " + Plain(analysis.Examples[i].Chinese));
        }
        text.AppendLine("\n## 常见搭配");
        foreach (var item in analysis.Collocations) text.AppendLine("- " + Plain(item.English) + "：" + Plain(item.Chinese));
        text.AppendLine("\n## 遇见时的原句");
        if (string.IsNullOrWhiteSpace(context)) text.AppendLine("未提供。");
        else foreach (var line in context.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) text.AppendLine("> " + Plain(line));
        text.AppendLine("\n## 已收录的关联词");
        var valid = relations.Where(r => VaultPaths.IsLinkable(r.Note)).ToArray();
        if (valid.Length == 0) text.AppendLine("暂无已收录的关联词。");
        foreach (var relation in valid)
        {
            var label = relation.Kind switch { RelationKind.SameRoot => "同根词", RelationKind.Synonym => "近义词", RelationKind.Antonym => "反义词", _ => "易混词" };
            var path = relation.Note.VaultRelativePath.Replace('\\', '/');
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) path = path[..^3];
            text.AppendLine("- " + label + "：[[" + path + "|" + Plain(relation.Note.Word) + "]]");
        }
        return text.AppendLine("\n## 我的笔记\n").ToString().Replace("\r\n", "\n");
    }
    private static string Phonetic(string value) => string.IsNullOrWhiteSpace(value) ? "未提供" : Plain(value);
    private static string Plain(string value)
    {
        var encoded = WebUtility.HtmlEncode(Regex.Replace(value, @"\s+", " ")).Replace("[", "&#91;").Replace("]", "&#93;");
        return Regex.Replace(encoded, @"[\\`*_#|]", @"\$0");
    }
}
