using System.Text.Json;
namespace VocabCollector.Core.Ai;

public sealed class AnalysisException(string userMessage) : Exception(userMessage);

public static class AnalysisJson
{
    private static readonly Dictionary<string, PartOfSpeech> Parts = Enum.GetValues<PartOfSpeech>().ToDictionary(p => p.ToString().ToLowerInvariant());
    private static readonly Dictionary<string, RelationKind> Relations = new()
    {
        ["same_root"] = RelationKind.SameRoot, ["synonym"] = RelationKind.Synonym, ["antonym"] = RelationKind.Antonym, ["confusable"] = RelationKind.Confusable
    };
    public static WordAnalysis Parse(string json)
    {
        try
        {
            var value = json.Trim();
            if (value.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase) || value.StartsWith("```json\r\n", StringComparison.OrdinalIgnoreCase))
            {
                if (!value.EndsWith("```")) throw new JsonException();
                value = value[(value.IndexOf('\n') + 1)..^3].Trim();
            }
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var valid = root.GetProperty("is_valid_word").GetBoolean();
            var error = Text(root, "error", valid);
            if (!valid) return new(false, error, "", "", PartOfSpeech.Other, [], [], [], []);
            var phonetics = root.GetProperty("phonetics");
            var primary = Parts[Text(root, "primary_pos")];
            var meaningsArray = Array(root, "meanings", 1, 3);
            var meanings = meaningsArray.Select(x => new Meaning(Parts[Text(x, "part_of_speech")], Text(x, "definition"))).ToArray();
            if (!meanings.Any(m => m.PartOfSpeech == primary)) throw new JsonException();
            var examples = Bilingual(root, "examples", 2, 2);
            if (examples.Any(x => x.English.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 20)) throw new JsonException();
            var collocations = Bilingual(root, "collocations", 2, 4);
            var lemma = root.TryGetProperty("lemma", out _) ? WordInput.Normalize(Text(root, "lemma", maxLength: 64)) : "";
            var formNote = OptionalText(root, "form_note", 200);
            var grammar = root.TryGetProperty("grammar", out _)
                ? Array(root, "grammar", 0, 3).Select(x => new GrammarPattern(Text(x, "pattern", maxLength: 120), Text(x, "explanation", maxLength: 200))).ToArray()
                : [];
            List<RelatedCandidate> related = [];
            var synonymCount = 0; var antonymCount = 0;
            foreach (var item in Array(root, "related", 0, 12))
            {
                var relation = Relations[Text(item, "relation")];
                if (relation == RelationKind.Synonym && ++synonymCount > 3 || relation == RelationKind.Antonym && ++antonymCount > 2) throw new JsonException();
                var word = Text(item, "word");
                var explanation = OptionalText(item, "explanation", 200);
                try { related.Add(new(WordInput.Normalize(word), relation, explanation)); } catch (ArgumentException) { }
            }
            return new(true, error, Text(phonetics, "uk", true), Text(phonetics, "us", true), primary, meanings, examples, collocations, related)
            { Lemma = lemma, FormNote = formNote, Grammar = grammar };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new AnalysisException("AI 返回的解析内容不完整或格式不合规，请重试或更换模型。"); }
    }
    private static string Text(JsonElement obj, string name, bool allowEmpty = false, int maxLength = 2000)
    {
        var value = obj.GetProperty(name).GetString()?.Trim() ?? throw new JsonException();
        if (value.Length > maxLength || !allowEmpty && value.Length == 0 || value.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c))) throw new JsonException();
        return value;
    }
    private static string OptionalText(JsonElement obj, string name, int maxLength) =>
        obj.TryGetProperty(name, out _) ? Text(obj, name, true, maxLength) : "";
    private static JsonElement[] Array(JsonElement obj, string name, int min, int max)
    {
        var result = obj.GetProperty(name).EnumerateArray().ToArray();
        if (result.Length < min || result.Length > max) throw new JsonException();
        return result;
    }
    private static BilingualText[] Bilingual(JsonElement root, string name, int min, int max) =>
        Array(root, name, min, max).Select(x => new BilingualText(Text(x, "english"), Text(x, "chinese"))).ToArray();
}
