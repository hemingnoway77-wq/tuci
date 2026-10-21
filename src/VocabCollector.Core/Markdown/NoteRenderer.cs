using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VocabCollector.Core.Storage;
namespace VocabCollector.Core.Markdown;

public sealed class NoteRenderer
{
    public string Render(string word, string context, WordAnalysis analysis, IReadOnlyList<ResolvedRelation> relations, DateTimeOffset created)
        => Render(word, context, analysis, new VaultIndex(relations.Select(r => r.Note).Distinct().ToArray()), created);

    public string Render(string word, string context, WordAnalysis analysis, VaultIndex index, DateTimeOffset created)
    {
        var text = new StringBuilder();
        text.AppendLine("---").AppendLine("type: english-word").AppendLine("word: " + JsonSerializer.Serialize(word));
        if (!string.IsNullOrEmpty(analysis.Lemma)) text.AppendLine("lemma: " + JsonSerializer.Serialize(analysis.Lemma));
        text.AppendLine("primary_pos: " + analysis.PrimaryPartOfSpeech.ToString().ToLowerInvariant()).AppendLine("parts_of_speech:");
        foreach (var pos in analysis.Meanings.Select(m => m.PartOfSpeech).Distinct()) text.AppendLine("  - " + pos.ToString().ToLowerInvariant());
        text.AppendLine("created: '" + created.ToString("O") + "'").AppendLine("tags:\n  - english\n  - vocabulary\n---\n");
        text.AppendLine("# " + Plain(word)).AppendLine("---\n");
        text.AppendLine("**发音**：UK " + Phonetic(analysis.UkPhonetic) + " · US " + Phonetic(analysis.UsPhonetic));
        text.AppendLine("\n**释义**：" + string.Join("；", analysis.Meanings.Select(m => Abbreviation(m.PartOfSpeech) + " " + Plain(m.Definition))));
        if (!string.IsNullOrWhiteSpace(analysis.FormNote)) text.AppendLine("\n**词形**：" + Plain(analysis.FormNote));
        var examples = new StringBuilder("#### 例句\n");
        for (var i = 0; i < analysis.Examples.Count; i++)
        {
            var english = Plain(analysis.Examples[i].English);
            english = Regex.Replace(english, @"(?<![A-Za-z'-])" + Regex.Escape(Plain(word)) + @"(?![A-Za-z'-])", m => "**" + m.Value + "**", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            examples.AppendLine($"{i + 1}. {english}");
            examples.AppendLine("   - " + Plain(analysis.Examples[i].Chinese));
        }
        text.AppendLine().AppendLine(ManagedLinks.Wrap("examples", examples.ToString().TrimEnd()));
        text.AppendLine("\n#### 常见搭配");
        foreach (var item in analysis.Collocations) text.AppendLine("- " + Plain(item.English) + "：" + Plain(item.Chinese));
        var semantic = new StringBuilder("#### 语义关系\n");
        AppendRelations(semantic, analysis.Related, RelationKind.Synonym, "近义词", "暂无可靠的常用近义词。", 3);
        AppendRelations(semantic, analysis.Related, RelationKind.Antonym, "反义词", "无常用直接反义词。", 2);
        foreach (var (kind, title) in new[] { (RelationKind.SameRoot, "同根词"), (RelationKind.Confusable, "易混词") })
            if (analysis.Related.Any(r => r.Kind == kind)) AppendRelations(semantic, analysis.Related, kind, title, "", 4);
        text.AppendLine().AppendLine(ManagedLinks.Wrap("relations", semantic.ToString().TrimEnd()));
        text.AppendLine("\n#### 语法信息\n##### 句法模式");
        if (analysis.Grammar.Count == 0) text.AppendLine("- 暂未提供可靠的句法说明。");
        foreach (var pattern in analysis.Grammar.Take(3))
        {
            text.AppendLine("- " + Plain(pattern.Pattern));
            text.AppendLine("  - " + Plain(pattern.Explanation));
        }
        if (!string.IsNullOrWhiteSpace(context))
        {
            text.AppendLine("\n#### 遇见时的原句");
            foreach (var line in context.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) text.AppendLine("> " + Plain(line));
        }
        return ManagedLinks.Refresh(text.AppendLine("\n#### 我的笔记\n").ToString().Replace("\r\n", "\n"), word, index);
    }
    private static void AppendRelations(StringBuilder text, IReadOnlyList<RelatedCandidate> related, RelationKind kind, string title, string empty, int limit)
    {
        text.AppendLine("##### " + title);
        var candidates = related.Where(r => r.Kind == kind).DistinctBy(r => r.Word, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
        if (candidates.Length == 0) text.AppendLine("- " + empty);
        foreach (var candidate in candidates) text.AppendLine("- " + Plain(candidate.Word) + "：" + (string.IsNullOrWhiteSpace(candidate.Explanation) ? "暂未提供用法说明。" : Plain(candidate.Explanation)));
    }
    private static string Phonetic(string value) => string.IsNullOrWhiteSpace(value) ? "未提供" : "/" + Plain(value.Trim().Trim('/')) + "/";
    private static string Abbreviation(PartOfSpeech pos) => pos switch
    {
        PartOfSpeech.Noun => "n.", PartOfSpeech.Verb => "v.", PartOfSpeech.Adjective => "adj.", PartOfSpeech.Adverb => "adv.",
        PartOfSpeech.Pronoun => "pron.", PartOfSpeech.Preposition => "prep.", PartOfSpeech.Conjunction => "conj.",
        PartOfSpeech.Interjection => "interj.", PartOfSpeech.Determiner => "det.", PartOfSpeech.Numeral => "num.", _ => "其他"
    };
    private static string Plain(string value)
    {
        var encoded = WebUtility.HtmlEncode(Regex.Replace(value, @"\s+", " ")).Replace("&#39;", "'").Replace("[", "&#91;").Replace("]", "&#93;");
        return Regex.Replace(encoded, @"[\\`*_#|]", @"\$0");
    }
}
