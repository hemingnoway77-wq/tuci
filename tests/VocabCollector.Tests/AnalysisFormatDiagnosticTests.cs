using System.Text.Json.Nodes;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public class AnalysisFormatDiagnosticTests
{
    [Fact]
    public void Credit_FourMeaningsReportsTheCountWithoutChangingTheLimit()
    {
        var json = Response("credit");
        json["primary_pos"] = "noun";
        json["meanings"] = JsonNode.Parse("""
            [{"part_of_speech":"noun","definition":"信用"},
             {"part_of_speech":"noun","definition":"学分"},
             {"part_of_speech":"noun","definition":"功劳"},
             {"part_of_speech":"verb","definition":"归功于"}]
            """);

        var error = Reject(json, "Credit");

        Diagnostic(error, "invalid_count", "meanings", "items=1..3", 4);
        Assert.Contains("释义需为1–3项", error.Message);
        Assert.Contains("本次4项", error.Message);
    }

    [Fact]
    public void Credit_ThreeMeaningsAcrossPartsOfSpeechStillPass()
    {
        var json = Response("credit");
        json["primary_pos"] = "noun";
        json["meanings"] = JsonNode.Parse("""
            [{"part_of_speech":"noun","definition":"信用"},
             {"part_of_speech":"verb","definition":"归功于"},
             {"part_of_speech":"noun","definition":"学分"}]
            """);

        var analysis = AnalysisJson.Parse(json.ToJsonString(), "Credit");

        Assert.Equal(3, analysis.Meanings.Count);
        Assert.Equal(PartOfSpeech.Noun, analysis.PrimaryPartOfSpeech);
        Assert.Equal(PartOfSpeech.Verb, analysis.Meanings[1].PartOfSpeech);
    }

    [Theory]
    [InlineData("[]", "phonetics", "type=object", "音标应为对象")]
    [InlineData("{\"uk\":[],\"us\":\"\"}", "phonetics.uk", "type=string", "英式音标应为文本")]
    public void Produce_PhoneticsShapeReportsTheExactField(string phonetics, string path, string rule, string detail)
    {
        var json = Response("produce");
        json["phonetics"] = JsonNode.Parse(phonetics);

        var error = Reject(json, "produce");

        Diagnostic(error, "invalid_type", path, rule);
        Assert.Contains(detail, error.Message);
    }

    [Theory]
    [InlineData("[{\"root\":\"duc/duct\",\"meaning\":\"引导\",\"variants\":[]}]", "invalid_root", "roots[0].root", "letters=1..32;edge_hyphens=allowed")]
    [InlineData("[{\"root\":\"duc\",\"meaning\":\"引导\"}]", "required_field", "roots[0].variants", "required")]
    [InlineData("[{\"root\":\"duc\",\"meaning\":\"引导\",\"variants\":[\"duct\",\"duc/duct\"]}]", "invalid_root", "roots[0].variants[1]", "letters=1..32;edge_hyphens=allowed")]
    [InlineData("[{\"root\":\"duc\",\"meaning\":\"引导\",\"variants\":[42]}]", "invalid_type", "roots[0].variants[0]", "type=string")]
    public void Produce_MalformedRootReportsTheExactField(string roots, string code, string path, string rule)
    {
        var json = Response("produce");
        json["roots"] = JsonNode.Parse(roots);

        Diagnostic(Reject(json, "produce"), code, path, rule);
    }

    [Theory]
    [InlineData("Valid")]
    [InlineData("VALID")]
    [InlineData("made-up")]
    public void SpellingStatus_RemainsCaseSensitiveAndReportsItsEnumRule(string status)
    {
        var json = Response();
        json["spelling"]!["status"] = status;

        var error = Reject(json);

        Diagnostic(error, "invalid_enum", "spelling.status", "enum=valid|corrected|uncertain|invalid");
        Assert.Contains("拼写状态", error.Message);
    }

    [Theory]
    [InlineData("meanings", "meanings")]
    [InlineData("phonetics.uk", "phonetics.uk")]
    [InlineData("meanings.0.part_of_speech", "meanings[0].part_of_speech")]
    [InlineData("spelling.suggestions", "spelling.suggestions")]
    public void RequiredField_ReportsTheMissingField(string dottedPath, string expectedPath)
    {
        var json = Response();
        var segments = dottedPath.Split('.');
        JsonNode parent = json;
        foreach (var segment in segments[..^1])
            parent = int.TryParse(segment, out var index) ? parent[index]! : parent[segment]!;
        parent.AsObject().Remove(segments[^1]);

        var error = Reject(json);

        Diagnostic(error, "required_field", expectedPath, "required");
        Assert.Contains("缺少", error.Message);
    }

    [Theory]
    [InlineData("[]", "response", "type=object")]
    [InlineData("{\"is_valid_word\":\"true\"}", "is_valid_word", "type=boolean")]
    public void ObjectAndBooleanTypes_ReportTheExpectedType(string json, string path, string rule) =>
        Diagnostic(Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json)), "invalid_type", path, rule);

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"private-key-canary\":")]
    [InlineData("```json\n{}")]
    public void BrokenJson_ReportsSyntaxWithoutEchoingParserInput(string json)
    {
        var error = Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json));

        Diagnostic(error, "invalid_json", "response", "valid_json;max_depth=16");
        Assert.Contains("JSON", error.Message);
        Assert.DoesNotContain("private-key-canary", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("meanings", "[]", "items=1..3", 0)]
    [InlineData("examples", "[]", "items=2..2", 0)]
    [InlineData("collocations", "[]", "items=2..4", 0)]
    [InlineData("roots", "[{}, {}, {}, {}]", "items=0..3", 4)]
    [InlineData("grammar", "[{}, {}, {}, {}]", "items=0..3", 4)]
    [InlineData("related", "[{}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}, {}]", "items=0..12", 13)]
    public void ArrayBounds_ReportTheirExistingLimitsAndActualCounts(string field, string value, string rule, int count)
    {
        var json = Response();
        json[field] = JsonNode.Parse(value);

        Diagnostic(Reject(json), "invalid_count", field, rule, count);
    }

    [Fact]
    public void NestedArrayBounds_ReportTheirParentIndex()
    {
        var json = Response();
        json["roots"] = JsonNode.Parse("""
            [{"root":"duc","meaning":"引导","variants":["duct","du","dou","doc"]}]
            """);

        Diagnostic(Reject(json), "invalid_count", "roots[0].variants", "items=0..3", 4);
    }

    [Theory]
    [InlineData("synonym", 4, "synonyms=0..3")]
    [InlineData("synonym", 6, "synonyms=0..3")]
    [InlineData("antonym", 3, "antonyms=0..2")]
    [InlineData("antonym", 5, "antonyms=0..2")]
    public void RelationBounds_ReportWhichRelationExceededTheLimit(string relation, int count, string rule)
    {
        var json = Response();
        json["related"] = new JsonArray(Enumerable.Range(0, count).Select(_ => (JsonNode)new JsonObject
        { ["word"] = "word", ["relation"] = relation }).ToArray());

        Diagnostic(Reject(json), "invalid_count", "related", rule, count);
    }

    [Fact]
    public void PrimaryPartOfSpeech_MustStillAppearInTheMeanings()
    {
        var json = Response();
        json["primary_pos"] = "noun";

        Diagnostic(Reject(json), "inconsistent_pos", "primary_pos", "must_match=meanings[].part_of_speech");
    }

    [Theory]
    [InlineData("", "invalid_text", "non_empty", null)]
    [InlineData("bad\u0001text", "invalid_text", "no_nonwhitespace_control_characters", null)]
    public void TextRules_ReportTheFieldWithoutTheText(string definition, string code, string rule, int? count)
    {
        var json = Response();
        json["meanings"]![0]!["definition"] = definition;

        Diagnostic(Reject(json), code, "meanings[0].definition", rule, count);
    }

    [Fact]
    public void OversizedText_ReportsTheExistingCharacterLimit()
    {
        var json = Response();
        json["meanings"]![0]!["definition"] = new string('x', 2001);

        Diagnostic(Reject(json), "invalid_text", "meanings[0].definition", "max_chars=2000", 2001);
    }

    [Fact]
    public void LongExample_ReportsItsIndexAndWordCount()
    {
        var json = Response();
        json["examples"]![1]!["english"] = string.Join(' ', Enumerable.Repeat("word", 21));

        Diagnostic(Reject(json), "invalid_count", "examples[1].english", "max_words=20", 21);
    }

    [Theory]
    [InlineData("spelling.status")]
    [InlineData("roots.0.root")]
    [InlineData("phonetics.uk")]
    public void FormatDiagnostics_DoNotRetainRawValuesPrivateSentencesOrKeys(string dottedPath)
    {
        const string sentence = "private-sentence";
        const string key = "sk-key-canary";
        const string canary = sentence + " " + key;
        var json = Response();
        json["roots"] = JsonNode.Parse("[{\"root\":\"duc\",\"meaning\":\"引导\",\"variants\":[]}]");
        var segments = dottedPath.Split('.');
        JsonNode parent = json;
        foreach (var segment in segments[..^1])
            parent = int.TryParse(segment, out var index) ? parent[index]! : parent[segment]!;
        parent[segments[^1]] = dottedPath == "phonetics.uk" ? JsonNode.Parse("[]") : JsonValue.Create(canary);
        json["examples"]![0]!["english"] = canary;
        json[canary] = canary;

        var error = Reject(json);

        foreach (var privateValue in new[] { sentence, key })
        {
            Assert.DoesNotContain(privateValue, error.ToString());
            Assert.DoesNotContain(privateValue, error.DiagnosticCode + error.FieldPath + error.Rule);
        }
        Assert.Empty(error.Data);
        Assert.Null(error.InnerException);
        Assert.Null(error.ActualCount);
    }

    [Fact]
    public void SpellingConsistencyFailure_PreservesTheValidatorDiagnostic()
    {
        var json = Response();
        var archive = AnalysisJson.Parse(json.ToJsonString());
        var direct = Assert.Throws<AnalysisException>(() => SpellingValidator.ResolveWord("different", archive));

        var parsed = Reject(json, "different");

        Assert.Equal(direct.Message, parsed.Message);
        Assert.Equal(direct.DiagnosticCode, parsed.DiagnosticCode);
        Assert.Equal(direct.FieldPath, parsed.FieldPath);
        Assert.Equal(direct.Rule, parsed.Rule);
        Assert.DoesNotContain("解析内容不完整或格式不合规", parsed.Message);
    }

    private static JsonObject Response(string word = "resilient") =>
        JsonNode.Parse(RecordingHttpHandler.ValidJson.Replace("resilient", word))!.AsObject();

    private static AnalysisException Reject(JsonObject json, string? expectedInput = null) =>
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString(), expectedInput));

    private static void Diagnostic(AnalysisException error, string code, string path, string rule, int? count = null)
    {
        Assert.StartsWith("AI 返回的解析内容不完整或格式不合规", error.Message);
        Assert.Equal(code, error.DiagnosticCode);
        Assert.Equal(path, error.FieldPath);
        Assert.Equal(rule, error.Rule);
        Assert.Equal(count, error.ActualCount);
        Assert.Null(error.InnerException);
    }
}
