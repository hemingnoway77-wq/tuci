using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VocabCollector.Core.Library;

public static class PracticeQuestionBank
{
    public static IReadOnlyList<PracticeSense> WithContexts(IReadOnlyList<PracticeSense> senses, IReadOnlyList<PracticeContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(senses);
        ArgumentNullException.ThrowIfNull(contexts);
        if (senses.Count == 0 || contexts.Count != senses.Count ||
            senses.Any(sense => sense is null || string.IsNullOrWhiteSpace(sense.Id)) ||
            senses.Select(sense => sense.Id).Distinct(StringComparer.Ordinal).Count() != senses.Count)
            throw new ArgumentException("每个义项都需要一条完整、唯一绑定的中英语境。");
        var byId = new Dictionary<string, PracticeContext>(StringComparer.Ordinal);
        foreach (var context in contexts)
            if (context is null || string.IsNullOrWhiteSpace(context.SenseId) || !byId.TryAdd(context.SenseId, context))
                throw new ArgumentException("语境义项编号无效或重复。");
        return senses.Select(sense =>
        {
            if (!byId.TryGetValue(sense.Id, out var context))
                throw new ArgumentException("语境缺少对应义项。");
            var form = NormalizeAnswerForm(sense, context.AnswerForm);
            if (!ValidContext(form ?? sense.Word, context.English, context.Chinese))
                throw new ArgumentException("语境需要对应义项、包含收藏词形的完整英文短句和中文译句。");
            return sense with { Context = NormalizeContextText(context.English), ContextTranslation = NormalizeContextText(context.Chinese), AnswerForm = form };
        }).ToArray();
    }

    public static IReadOnlyList<PracticeSense> Build(LibraryNote note, string? dictionaryLemma = null)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (!note.IsWord) return [];
        var word = WordInput.Normalize(note.Title);
        var body = WithoutFencedCode(note.Body);
        var lines = body.Split('\n');
        var start = Array.FindIndex(lines, line => Regex.IsMatch(line, @"^\s*\*\*释义\*\*[：:]?\s*$"));
        if (start < 0) return [];
        var meanings = new List<string>();
        foreach (var line in lines.Skip(start + 1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.TrimStart().StartsWith('>')) break;
            var meaning = PlainText(line.TrimStart()[1..]).Trim();
            if (meaning.Length == 0 || Regex.IsMatch(meaning, @"^(?:待补充|暂无释义|未提供|暂无可靠释义)[。.]?$")) continue;
            if (!meanings.Contains(meaning, StringComparer.Ordinal)) meanings.Add(meaning);
        }
        if (meanings.Count == 0) return [];
        var lemma = FrontmatterLemma(note.Markdown) ?? NormalizeLemma(dictionaryLemma);
        if (lemma == word) lemma = null;
        var form = Regex.Match(body, @"(?m)^\s*\*\*词形\*\*[：:]\s*([^\n]*)");
        var formNote = PlainText(form.Groups[1].Value);
        var context = "";
        // A sentence has no dependable sense binding when more than one meaning
        // was saved. Example position is deliberately never used as a sense ID.
        if (meanings.Count == 1)
        {
            var original = Regex.Match(body, @"(?ms)^#{1,6}\s+遇见时的原句\s*\n(.*?)(?=^#{1,6}\s|\z)");
            if (original.Success)
            {
                var candidate = PlainText(string.Join("\n", original.Groups[1].Value.Split('\n').Select(line => line.TrimStart().TrimStart('>').TrimStart())));
                if (WordPattern(word).IsMatch(candidate)) context = candidate;
            }
        }
        return meanings.Select((meaning, index) => new PracticeSense(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(meaning.Normalize(NormalizationForm.FormC)))),
            word, lemma, meaning, context, formNote, index + 1, meanings.Count)).ToArray();
    }

    public static PracticeOutcome Evaluate(PracticeQuestion question, string answer, bool timedOut = false)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (timedOut) return PracticeOutcome.TimedOut;
        if (question.Kind != PracticeKind.Spelling) throw new InvalidOperationException("中文释义需要对照参考后自行确认。");
        string normalized;
        try { normalized = WordInput.Normalize(answer); }
        catch (ArgumentException) { return PracticeOutcome.Incorrect; }
        if (normalized == WordInput.Normalize(question.Word)) return PracticeOutcome.Correct;
        return question.Lemma is not null && normalized == NormalizeLemma(question.Lemma)
            ? PracticeOutcome.LemmaCorrect : PracticeOutcome.Incorrect;
    }

    internal static PracticeQuestion Create(PracticeSense sense, string noteId, Guid exposureId, PracticeKind kind)
    {
        if (!HasContext(sense)) throw new ArgumentException("缺少当前义项的完整中英语境，不能出题。");
        var form = NormalizeAnswerForm(sense, sense.AnswerForm);
        var target = form ?? WordInput.Normalize(sense.Word);
        var lemma = form is null ? sense.Lemma : WordInput.Normalize(sense.Word);
        var formNote = form is null ? sense.FormNote : $"本句使用复数 {target}；词条原形为 {lemma}。";
        var spelling = kind == PracticeKind.Spelling;
        var meaning = spelling ? HideAnswers(sense.Meaning, target, lemma) : sense.Meaning;
        var context = spelling ? HideAnswers(sense.Context, target, lemma) : sense.Context;
        var translation = spelling ? HideAnswers(sense.ContextTranslation, target, lemma) : sense.ContextTranslation;
        var prompt = spelling ? context : target + "\n" + context;
        // Word, Lemma and FormNote are reference fields for the revealed answer.
        return new(exposureId, noteId, sense.Id, kind, target, lemma, meaning, prompt, context, formNote, sense.Number, sense.Count, translation);
    }

    internal static bool HasContext(PracticeSense sense) => ValidContext(NormalizeAnswerForm(sense, sense.AnswerForm) ?? sense.Word, sense.Context, sense.ContextTranslation);

    internal static IReadOnlyList<string> AllowedAnswerForms(PracticeSense sense)
    {
        var word = WordInput.Normalize(sense.Word);
        var plural = ExplicitPlural(sense);
        return plural is null ? [word] : [word, plural];
    }

    private static string? NormalizeAnswerForm(PracticeSense sense, string? supplied)
    {
        if (supplied is null) return null;
        var form = WordInput.Normalize(supplied);
        if (form == WordInput.Normalize(sense.Word)) return null;
        if (form == ExplicitPlural(sense)) return form;
        throw new ArgumentException("该义项没有明确支持此答案词形。");
    }

    private static string? ExplicitPlural(PracticeSense sense)
    {
        // The saved meaning, not the model response, must explicitly identify
        // the noun's plural. Only forward regular rules are supported here.
        if (!Regex.IsMatch(sense.Meaning, @"^\s*(?:n\.|noun\b|名词)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(sense.Meaning, @"(?:不是|并非|非|不|not)\s*(?:用|使用|采用|a|the)?\s*(?:复数|plural)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return null;
        var word = WordInput.Normalize(sense.Word);
        if (!Regex.IsMatch(word, @"\A[a-z]+\z", RegexOptions.CultureInvariant)) return null;
        var plural = Regex.IsMatch(word, @"[^aeiou]y\z", RegexOptions.CultureInvariant) ? word[..^1] + "ies"
            : Regex.IsMatch(word, @"(?:s|x|z|ch|sh)\z", RegexOptions.CultureInvariant) ? word + "es" : word + "s";
        if (plural.Length > 64) return null;
        var explicitForms = Regex.Matches(sense.Meaning, @"(?:复数|plural)\s*(?:形式)?\s*(?:为|是)?\s*[:：]?\s*([a-z]+(?:[-'][a-z]+)*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        return explicitForms.Length == 1 && explicitForms[0] == plural ? plural : null;
    }

    private static bool ValidContext(string word, string? english, string? chinese)
    {
        if (!PlainContext(english) || !PlainContext(chinese)) return false;
        var sentence = NormalizeContextText(english!);
        var translation = NormalizeContextText(chinese!);
        var words = Regex.Matches(sentence, @"[A-Za-z]+(?:[-'][A-Za-z]+)*").Count;
        return words is >= 6 and <= 24 && !ContainsChinese(sentence) && ContainsChinese(translation) && WordPattern(word).IsMatch(sentence);
    }

    private static bool ContainsChinese(string text) => Regex.IsMatch(text, @"[\u3400-\u9fff\uf900-\ufaff]");

    private static bool PlainContext(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2000) return false;
        var decoded = WebUtility.HtmlDecode(value);
        foreach (var text in new[] { value, decoded })
        {
            if (text.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format || character is '\u2028' or '\u2029')) return false;
            if (Regex.IsMatch(text, @"[\[\]<>`{}*\\]|_{2,}|[a-z][a-z0-9+.-]*://|(?:mailto|javascript|data|file|obsidian):|www\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        }
        return true;
    }

    private static string NormalizeContextText(string value) => NormalizeApostrophes(WebUtility.HtmlDecode(value)).Trim();

    private static string HideAnswers(string text, string word, string? lemma)
    {
        text = NormalizeApostrophes(text);
        text = WordPattern(word).Replace(text, "____");
        return lemma is null ? text : WordPattern(lemma).Replace(text, "____");
    }

    private static Regex WordPattern(string word) => new("(?<![A-Za-z'-])" + Regex.Escape(word) + "(?![A-Za-z'-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? FrontmatterLemma(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('\uFEFF');
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return null;
        var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        if (end < 0) return null;
        var fields = Regex.Matches(text[..end], @"(?m)^lemma\s*:\s*([^\n]*)$");
        if (fields.Count != 1) return null;
        var value = fields[0].Groups[1].Value.Trim();
        try
        {
            if (value.StartsWith('"')) value = JsonSerializer.Deserialize<string>(value) ?? "";
            else if (value.StartsWith('\''))
            {
                if (value.Length < 2 || !value.EndsWith('\'')) return null;
                value = value[1..^1].Replace("''", "'");
            }
            return NormalizeLemma(value);
        }
        catch (JsonException) { return null; }
    }

    private static string? NormalizeLemma(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return WordInput.Normalize(value); }
        catch (ArgumentException) { return null; }
    }

    private static string PlainText(string value)
    {
        value = NormalizeApostrophes(WebUtility.HtmlDecode(value));
        value = Regex.Replace(value, @"\[\[([^\]\r\n]+)\]\]", match => match.Groups[1].Value.Split('|').Last());
        value = Regex.Replace(value, @"\[([^\]\r\n]*)\]\([^\r\n)]*\)", "$1");
        value = Regex.Replace(value, @"<[^>]*>", "");
        value = Regex.Replace(value, @"\\([\\`*_#|\[\]])", "$1");
        value = Regex.Replace(value, @"[*_`]", "");
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static string NormalizeApostrophes(string value) => value.Replace('’', '\'').Replace('‘', '\'');

    private static string WithoutFencedCode(string body)
    {
        var output = new StringBuilder(); char fence = '\0'; var width = 0;
        foreach (var line in body.Split('\n'))
        {
            var marker = Regex.Match(line, @"^\s{0,3}(`{3,}|~{3,})");
            if (marker.Success)
            {
                if (fence == '\0') { fence = marker.Groups[1].Value[0]; width = marker.Groups[1].Length; }
                else if (marker.Groups[1].Value[0] == fence && marker.Groups[1].Length >= width) fence = '\0';
                output.AppendLine(); continue;
            }
            output.AppendLine(fence == '\0' ? line : "");
        }
        return output.ToString().Replace("\r\n", "\n");
    }
}
