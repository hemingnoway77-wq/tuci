using System.Text.Json;
namespace VocabCollector.Core.Ai;

public sealed class AnalysisException(string userMessage, string diagnosticCode = "analysis_error", string fieldPath = "response", string rule = "", int? actualCount = null) : Exception(userMessage)
{
    public string DiagnosticCode { get; } = diagnosticCode;
    public string FieldPath { get; } = fieldPath;
    public string Rule { get; } = rule;
    public int? ActualCount { get; } = actualCount;
}

public static class AnalysisJson
{
    private const string FormatPrefix = "AI 返回的解析内容不完整或格式不合规";
    private const string PartRule = "enum=noun|verb|adjective|adverb|pronoun|preposition|conjunction|interjection|determiner|numeral|other";
    private static readonly Dictionary<string, PartOfSpeech> Parts = Enum.GetValues<PartOfSpeech>().ToDictionary(p => p.ToString().ToLowerInvariant());
    private static readonly Dictionary<string, RelationKind> Relations = new()
    {
        ["same_root"] = RelationKind.SameRoot, ["synonym"] = RelationKind.Synonym, ["antonym"] = RelationKind.Antonym, ["confusable"] = RelationKind.Confusable
    };
    public static WordAnalysis Parse(string json, string? expectedInput = null)
    {
        try
        {
            var value = UnwrapJsonFence(json);
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            Object(root, "response", "解析结果");
            var validJson = Required(root, "is_valid_word", "", "单词有效性");
            if (validJson.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Failure("invalid_type", "is_valid_word", "type=boolean", "单词有效性应为布尔值");
            var valid = validJson.GetBoolean();
            var error = Text(root, "error", "错误说明", allowEmpty: valid);
            var spelling = root.TryGetProperty("spelling", out var spellingJson) ? Spelling(spellingJson) : null;
            if (!valid)
            {
                var rejected = new WordAnalysis(false, error, "", "", PartOfSpeech.Other, [], [], [], []) { Spelling = spelling };
                if (expectedInput is not null) SpellingValidator.ResolveWord(expectedInput, rejected);
                return rejected;
            }
            var phonetics = Required(root, "phonetics", "", "音标");
            Object(phonetics, "phonetics", "音标");
            var primary = Part(root, "primary_pos", "", "主要词性");
            var meanings = Array(root, "meanings", "释义", 1, 3).Select((x, index) =>
            {
                var path = ItemPath("meanings", index);
                Object(x, path, "释义项");
                return new Meaning(Part(x, "part_of_speech", path, "释义词性"), Text(x, "definition", "释义文本", path));
            }).ToArray();
            if (!meanings.Any(m => m.PartOfSpeech == primary))
                throw Failure("inconsistent_pos", "primary_pos", "must_match=meanings[].part_of_speech", "主要词性需出现在释义中");
            var examples = Bilingual(root, "examples", "例句", 2, 2);
            for (var index = 0; index < examples.Length; index++)
            {
                var count = examples[index].English.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                if (count > 20)
                    throw Failure("invalid_count", ItemPath("examples", index) + ".english", "max_words=20", $"英文例句需不超过20词（本次{count}词）", count);
            }
            var collocations = Bilingual(root, "collocations", "搭配", 2, 4);
            var lemma = root.TryGetProperty("lemma", out _) ? Word(Text(root, "lemma", "原形", maxLength: 64), "lemma", "原形") : "";
            var formNote = OptionalText(root, "form_note", "词形说明", 200);
            var roots = root.TryGetProperty("roots", out _)
                ? Array(root, "roots", "词根", 0, 3).Select((x, index) =>
                {
                    var path = ItemPath("roots", index);
                    Object(x, path, "词根项");
                    return new RootPart(
                        Root(Text(x, "root", "词根", path, maxLength: 32), path + ".root"),
                        Text(x, "meaning", "词根含义", path, maxLength: 80),
                        Array(x, "variants", "词根变体", 0, 3, path).Select((v, variantIndex) =>
                        {
                            var variantPath = ItemPath(path + ".variants", variantIndex);
                            return Root(String(v, variantPath, "词根变体"), variantPath);
                        }).ToArray());
                }).ToArray()
                : [];
            var grammar = root.TryGetProperty("grammar", out _)
                ? Array(root, "grammar", "语法模式", 0, 3).Select((x, index) =>
                {
                    var path = ItemPath("grammar", index);
                    Object(x, path, "语法模式项");
                    return new GrammarPattern(Text(x, "pattern", "语法模式", path, maxLength: 120), Text(x, "explanation", "语法说明", path, maxLength: 200));
                }).ToArray()
                : [];
            List<RelatedCandidate> related = [];
            var synonymCount = 0; var antonymCount = 0;
            var relatedArray = Array(root, "related", "关联词", 0, 12);
            for (var index = 0; index < relatedArray.Length; index++)
            {
                var item = relatedArray[index];
                var path = ItemPath("related", index);
                Object(item, path, "关联词项");
                if (!Relations.TryGetValue(Text(item, "relation", "关联类型", path), out var relation))
                    throw Failure("invalid_enum", path + ".relation", "enum=same_root|synonym|antonym|confusable", "关联类型需为约定的小写类型");
                if (relation == RelationKind.Synonym) synonymCount++;
                if (relation == RelationKind.Antonym) antonymCount++;
                var word = Text(item, "word", "关联词", path);
                var explanation = OptionalText(item, "explanation", "关联说明", 200, path);
                try { related.Add(new(WordInput.Normalize(word), relation, explanation)); } catch (ArgumentException) { }
            }
            if (synonymCount > 3)
                throw Failure("invalid_count", "related", "synonyms=0..3", $"同义词需不超过3项（本次{synonymCount}项）", synonymCount);
            if (antonymCount > 2)
                throw Failure("invalid_count", "related", "antonyms=0..2", $"反义词需不超过2项（本次{antonymCount}项）", antonymCount);
            var analysis = new WordAnalysis(true, error, Text(phonetics, "uk", "英式音标", "phonetics", true), Text(phonetics, "us", "美式音标", "phonetics", true), primary, meanings, examples, collocations, related)
            { Lemma = lemma, FormNote = formNote, Grammar = grammar, Roots = roots, Spelling = spelling };
            if (expectedInput is not null) SpellingValidator.ResolveWord(expectedInput, analysis);
            return analysis;
        }
        catch (JsonException)
        { throw Failure("invalid_json", "response", "valid_json;max_depth=16", "解析结果需为完整且层级不超过16的JSON"); }
    }
    internal static string UnwrapJsonFence(string json)
    {
        var value = json.Trim();
        if (!value.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("```json\r\n", StringComparison.OrdinalIgnoreCase)) return value;
        if (!value.EndsWith("```", StringComparison.Ordinal)) throw new JsonException();
        return value[(value.IndexOf('\n') + 1)..^3].Trim();
    }

    private static SpellingAssessment Spelling(JsonElement value)
    {
        Object(value, "spelling", "拼写判断");
        var input = Word(Text(value, "input_word", "输入词拼写", "spelling", maxLength: 64), "spelling.input_word", "输入词拼写");
        var target = Text(value, "word", "目标词拼写", "spelling", true, 64);
        var status = Text(value, "status", "拼写状态", "spelling") switch
        {
            "valid" => SpellingStatus.Valid,
            "corrected" => SpellingStatus.Corrected,
            "uncertain" => SpellingStatus.Uncertain,
            "invalid" => SpellingStatus.Invalid,
            _ => throw Failure("invalid_enum", "spelling.status", "enum=valid|corrected|uncertain|invalid", "拼写状态需为约定的小写状态")
        };
        var suggestions = Array(value, "suggestions", "拼写建议", 0, 3, "spelling").Select((item, index) =>
        {
            var path = ItemPath("spelling.suggestions", index);
            return Word(String(item, path, "拼写建议"), path, "拼写建议");
        }).ToArray();
        return new(input, target.Length == 0 ? "" : Word(target, "spelling.word", "目标词拼写"), status, suggestions);
    }

    private static PartOfSpeech Part(JsonElement obj, string name, string parent, string label)
    {
        if (!Parts.TryGetValue(Text(obj, name, label, parent), out var part))
            throw Failure("invalid_enum", FieldPath(parent, name), PartRule, label + "需为约定的小写词性");
        return part;
    }

    private static string Text(JsonElement obj, string name, string label, string parent = "", bool allowEmpty = false, int maxLength = 2000)
    {
        var path = FieldPath(parent, name);
        var value = String(Required(obj, name, parent, label), path, label).Trim();
        if (value.Length > maxLength)
            throw Failure("invalid_text", path, $"max_chars={maxLength}", $"{label}需不超过{maxLength}字符（本次{value.Length}字符）", value.Length);
        if (!allowEmpty && value.Length == 0)
            throw Failure("invalid_text", path, "non_empty", label + "不能为空");
        if (value.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c)))
            throw Failure("invalid_text", path, "no_nonwhitespace_control_characters", label + "包含不允许的控制字符");
        return value;
    }

    private static string String(JsonElement value, string path, string label)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Failure("invalid_type", path, "type=string", label + "应为文本");
        return value.GetString()!;
    }

    private static string OptionalText(JsonElement obj, string name, string label, int maxLength, string parent = "") =>
        obj.TryGetProperty(name, out _) ? Text(obj, name, label, parent, true, maxLength) : "";

    private static JsonElement[] Array(JsonElement obj, string name, string label, int min, int max, string parent = "")
    {
        var path = FieldPath(parent, name);
        var value = Required(obj, name, parent, label);
        if (value.ValueKind != JsonValueKind.Array)
            throw Failure("invalid_type", path, "type=array", label + "应为数组");
        var result = value.EnumerateArray().ToArray();
        if (result.Length < min || result.Length > max)
        {
            var range = min == max ? min.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{min}–{max}";
            throw Failure("invalid_count", path, $"items={min}..{max}", $"{label}需为{range}项（本次{result.Length}项）", result.Length);
        }
        return result;
    }

    private static BilingualText[] Bilingual(JsonElement root, string name, string label, int min, int max) =>
        Array(root, name, label, min, max).Select((x, index) =>
        {
            var path = ItemPath(name, index);
            Object(x, path, label + "项");
            return new BilingualText(Text(x, "english", "英文" + label, path), Text(x, "chinese", "中文" + label, path));
        }).ToArray();

    private static JsonElement Required(JsonElement obj, string name, string parent, string label)
    {
        if (!obj.TryGetProperty(name, out var value))
            throw Failure("required_field", FieldPath(parent, name), "required", "缺少" + label);
        return value;
    }

    private static void Object(JsonElement value, string path, string label)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Failure("invalid_type", path, "type=object", label + "应为对象");
    }

    private static string Word(string value, string path, string label)
    {
        try { return WordInput.Normalize(value); }
        catch (ArgumentException)
        { throw Failure("invalid_word", path, "single_english_word;length=1..64", label + "需为1–64字符的单个英文单词"); }
    }

    private static string Root(string value, string path)
    {
        try { return RootInput.Normalize(value); }
        catch (ArgumentException)
        { throw Failure("invalid_root", path, "letters=1..32;edge_hyphens=allowed", "词根及变体需为1–32个英文字母，可在两端使用连字符"); }
    }

    // Paths contain only schema constants and integer array indexes, never model-provided keys or values.
    private static string FieldPath(string parent, string name) => parent.Length == 0 ? name : parent + "." + name;
    private static string ItemPath(string parent, int index) => parent + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
    private static AnalysisException Failure(string code, string path, string rule, string detail, int? count = null) =>
        new(FormatPrefix + "：" + detail + "。请重试或更换模型。", code, path, rule, count);
}
