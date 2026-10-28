using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Tests.Fakes;
using System.Text.Json.Nodes;
namespace VocabCollector.Tests;
public class AnalysisJsonTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void Parse_AcceptsPlainOrOneJsonFence(bool fence)
    {
        var text = fence ? "```json\n" + RecordingHttpHandler.ValidJson + "\n```" : RecordingHttpHandler.ValidJson;
        var analysis = AnalysisJson.Parse(text);
        Assert.Equal(PartOfSpeech.Adjective, analysis.PrimaryPartOfSpeech);
        Assert.Equal(2, analysis.Examples.Count); Assert.Equal(2, analysis.Collocations.Count);
    }
    [Fact] public void Parse_InvalidWordDoesNotRequireDefinitions() => Assert.False(AnalysisJson.Parse("{\"is_valid_word\":false,\"error\":\"未识别为英文单词\"}").IsValidWord);
    [Fact] public void Parse_ProductionRequiresSpellingOnAcceptedWord()
    {
        var json = JsonNode.Parse(RecordingHttpHandler.ValidJson)!.AsObject(); json.Remove("spelling");
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString(), "resilient"));
        Assert.Null(AnalysisJson.Parse(json.ToJsonString()).Spelling);
    }
    [Fact] public void Parse_PreservesNormalizedCorrectionAndFullAnalysis()
    {
        var json = JsonNode.Parse(RecordingHttpHandler.ValidJson.Replace("resilient", "receive"))!.AsObject();
        json["spelling"] = JsonNode.Parse("{\"input_word\":\" RECIEVE \",\"word\":\" RECEIVE \",\"status\":\"corrected\",\"suggestions\":[]}");
        var analysis = AnalysisJson.Parse(json.ToJsonString(), "recieve");
        Assert.Equal("recieve", analysis.Spelling!.InputWord); Assert.Equal("receive", analysis.Spelling.Word);
        Assert.Equal(SpellingStatus.Corrected, analysis.Spelling.Status); Assert.Empty(analysis.Spelling.Suggestions);
        Assert.Equal(2, analysis.Examples.Count); Assert.Single(analysis.Meanings);
    }
    [Theory][InlineData("uncertain")][InlineData("invalid")]
    public void Parse_RejectionRetainsSafeSuggestionsWithoutDefinitions(string status)
    {
        var json = "{\"is_valid_word\":false,\"error\":\"请检查拼写\",\"spelling\":{\"input_word\":\"wier\",\"word\":\"\",\"status\":\"" + status + "\",\"suggestions\":[\" WIRE \",\"weir\"]}}";
        var analysis = AnalysisJson.Parse(json, "wier");
        Assert.False(analysis.IsValidWord);
        Assert.Equal("wier", analysis.Spelling!.InputWord); Assert.Equal("", analysis.Spelling.Word);
        Assert.Equal(new[] { "wire", "weir" }, analysis.Spelling.Suggestions);
    }
    [Theory]
    [InlineData("null")][InlineData("[]")][InlineData("42")][InlineData("{}")]
    [InlineData("{\"input_word\":\"resilient\",\"word\":\"resilient\",\"status\":\"made-up\",\"suggestions\":[]}")]
    [InlineData("{\"input_word\":\"resilient\",\"word\":\"resilient\",\"status\":1,\"suggestions\":[]}")]
    [InlineData("{\"input_word\":\"resilient\",\"word\":\"resilient\",\"status\":\"valid\",\"suggestions\":null}")]
    [InlineData("{\"input_word\":null,\"word\":\"resilient\",\"status\":\"valid\",\"suggestions\":[]}")]
    [InlineData("{\"input_word\":\"../resilient\",\"word\":\"resilient\",\"status\":\"valid\",\"suggestions\":[]}")]
    [InlineData("{\"input_word\":\"resilient\",\"word\":\"two words\",\"status\":\"valid\",\"suggestions\":[]}")]
    [InlineData("{\"input_word\":\"resilient\",\"word\":\"resilient\",\"status\":\"valid\",\"suggestions\":[42]}")]
    [InlineData("{\"input_word\":\"resilient\",\"word\":\"resilient\",\"status\":\"valid\",\"suggestions\":[\"../word\"]}")]
    [InlineData("{\"input_word\":\"resilient\",\"word\":\"resilient\",\"status\":\"valid\",\"suggestions\":[\"one\",\"two\",\"three\",\"four\"]}")]
    public void Parse_RejectsMalformedSpellingEvenInArchiveMode(string spelling)
    {
        var json = JsonNode.Parse(RecordingHttpHandler.ValidJson)!.AsObject(); json["spelling"] = JsonNode.Parse(spelling);
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Fact] public void Parse_RejectsMalformedSpellingBeforeInvalidEarlyReturn() =>
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse("{\"is_valid_word\":false,\"error\":\"错误\",\"spelling\":{}}", "word"));
    [Fact] public void Parse_ProductionRejectsUnrelatedInputEcho() =>
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(RecordingHttpHandler.ValidJson, "word"));
    [Fact] public void Parse_ProductionRejectsExampleForAnotherWord()
    {
        var json = JsonNode.Parse(RecordingHttpHandler.ValidJson)!.AsObject();
        json["examples"]![1]!["english"] = "The economy is strong.";
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString(), "resilient"));
    }
    [Fact] public void Parse_ProductionRejectsUncertainWordMarkedValid()
    {
        var json = JsonNode.Parse(RecordingHttpHandler.ValidJson)!.AsObject();
        json["spelling"] = JsonNode.Parse("{\"input_word\":\"resilient\",\"word\":\"\",\"status\":\"uncertain\",\"suggestions\":[\"resilient\"]}");
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString(), "resilient"));
    }
    [Fact] public void Parse_ProductionAcceptsLegacyRejection() =>
        Assert.False(AnalysisJson.Parse("{\"is_valid_word\":false,\"error\":\"请检查拼写\"}", "word").IsValidWord);
    [Theory][InlineData("adjective", "made-up")][InlineData("same_root", "unknown")]
    [InlineData("有韧性的", "")][InlineData("\"is_valid_word\":true", "\"is_valid_word\":\"true\"")]
    [InlineData("\"primary_pos\":\"adjective\"", "\"primary_pos\":\"noun\"")]
    public void Parse_RejectsInvalidShape(string oldValue, string replacement) => Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(RecordingHttpHandler.ValidJson.Replace(oldValue, replacement)));
    [Fact] public void Parse_RejectsMissingOrTruncatedContent()
    {
        foreach (var json in new[] { "{}", "text " + RecordingHttpHandler.ValidJson, RecordingHttpHandler.ValidJson[..^1], RecordingHttpHandler.ValidJson.Replace("有韧性的", new string('a',2001)) })
            Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json));
    }
    [Fact] public void Parse_PreservesLemmaFormNoteGrammarAndRelationContext()
    {
        var json = RichResponse();
        var analysis = AnalysisJson.Parse(json.ToJsonString());
        Assert.Equal("introduce", analysis.Lemma);
        Assert.Equal("introduce 的过去式和过去分词。", analysis.FormNote);
        var pattern = Assert.Single(analysis.Grammar);
        Assert.Equal("introduce A to B", pattern.Pattern);
        Assert.Equal("把 A 介绍给 B。", pattern.Explanation);
        Assert.Equal("推出产品或服务时意思接近。", Assert.Single(analysis.Related).Explanation);
    }
    [Fact] public void Parse_LegacyResponseDefaultsNewFieldsWithoutInventingContent()
    {
        var analysis = AnalysisJson.Parse(RecordingHttpHandler.ValidJson);
        Assert.Equal("", analysis.Lemma);
        Assert.Equal("", analysis.FormNote);
        Assert.Empty(analysis.Grammar);
        Assert.Empty(analysis.Roots);
        Assert.Equal("", Assert.Single(analysis.Related).Explanation);
    }
    [Theory]
    [InlineData("lemma", "null")][InlineData("lemma", "42")][InlineData("lemma", "\"two words\"")]
    [InlineData("lemma", "\"../introduce\"")][InlineData("lemma", "\"\"")]
    [InlineData("form_note", "null")][InlineData("form_note", "[]")]
    [InlineData("grammar", "null")][InlineData("grammar", "{}")]
    [InlineData("grammar", "[{\"pattern\":\"introduce A to B\"}]")]
    [InlineData("grammar", "[{\"pattern\":\"\",\"explanation\":\"解释\"}]")]
    [InlineData("grammar", "[{\"pattern\":\"pattern\",\"explanation\":\"\"}]")]
    public void Parse_RejectsMalformedNewFields(string field, string value)
    {
        var json = RichResponse(); json[field] = JsonNode.Parse(value);
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Theory][InlineData("lemma", 65)][InlineData("form_note", 201)]
    public void Parse_RejectsOversizedNewText(string field, int length)
    {
        var json = RichResponse(); json[field] = new string('a', length);
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Theory][InlineData("pattern", 121)][InlineData("explanation", 201)]
    public void Parse_RejectsOversizedGrammarText(string field, int length)
    {
        var json = RichResponse(); json["grammar"]![0]![field] = new string('a', length);
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Fact] public void Parse_RejectsTooManyGrammarPatterns()
    {
        var json = RichResponse();
        json["grammar"] = new JsonArray(Enumerable.Range(0,4).Select(_ => (JsonNode)new JsonObject { ["pattern"] = "introduce A to B", ["explanation"] = "把 A 介绍给 B。" }).ToArray());
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Theory][InlineData("null")][InlineData("42")]
    public void Parse_RejectsNonTextRelationContext(string value)
    {
        var json = RichResponse(); json["related"]![0]!["explanation"] = JsonNode.Parse(value);
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Fact] public void Parse_RejectsOversizedRelationContext()
    {
        var json = RichResponse(); json["related"]![0]!["explanation"] = new string('a',201);
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Theory][InlineData("synonym",4)][InlineData("antonym",3)]
    public void Parse_RejectsExcessiveSynonymsOrAntonyms(string relation, int count)
    {
        var json = RichResponse();
        json["related"] = new JsonArray(Enumerable.Range(0,count).Select(_ => (JsonNode)new JsonObject { ["word"] = "launch", ["relation"] = relation, ["explanation"] = "语境说明" }).ToArray());
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Fact] public void Parse_RejectsExampleLongerThanTwentyWords()
    {
        var json = RichResponse(); json["examples"]![0]!["english"] = string.Join(' ', Enumerable.Repeat("word",21));
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Fact] public void Parse_PreservesNormalizedRootsAndVariants()
    {
        var json = RichResponse();
        json["roots"] = JsonNode.Parse("[{\"root\":\" DUC \",\"variants\":[\"DUCT\"],\"meaning\":\" 引导 \"}]");
        var root = Assert.Single(AnalysisJson.Parse(json.ToJsonString()).Roots);
        Assert.Equal("duc", root.Root); Assert.Equal("引导", root.Meaning); Assert.Equal(new[] { "duct" }, root.Variants);
    }
    [Theory]
    [InlineData("null")][InlineData("{}")][InlineData("[{}]")]
    [InlineData("[{\"root\":\"duc\",\"variants\":null,\"meaning\":\"引导\"}]")]
    [InlineData("[{\"root\":42,\"variants\":[],\"meaning\":\"引导\"}]")]
    [InlineData("[{\"root\":\"../duc\",\"variants\":[],\"meaning\":\"引导\"}]")]
    [InlineData("[{\"root\":\"duc\",\"variants\":[\"[[duc]]\"],\"meaning\":\"引导\"}]")]
    [InlineData("[{\"root\":\"duc\",\"variants\":[1],\"meaning\":\"引导\"}]")]
    [InlineData("[{\"root\":\"duc\",\"variants\":[],\"meaning\":\"\"}]")]
    [InlineData("[{\"root\":\"duc\",\"variants\":[],\"meaning\":null}]")]
    public void Parse_RejectsMalformedRootFields(string roots)
    {
        var json = RichResponse(); json["roots"] = JsonNode.Parse(roots);
        Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
    }
    [Fact] public void Parse_RejectsRootCountMeaningAndVariantLimits()
    {
        foreach (var roots in new JsonNode[]
        {
            new JsonArray(Enumerable.Range(0, 4).Select(_ => JsonNode.Parse("{\"root\":\"duc\",\"variants\":[],\"meaning\":\"引导\"}")).ToArray()),
            JsonNode.Parse("[{\"root\":\"duc\",\"variants\":[\"duct\",\"du\",\"dou\",\"doc\"],\"meaning\":\"引导\"}]")!,
            new JsonArray(new JsonObject { ["root"] = "duc", ["variants"] = new JsonArray(), ["meaning"] = new string('长', 81) })
        })
        {
            var json = RichResponse(); json["roots"] = roots;
            Assert.Throws<AnalysisException>(() => AnalysisJson.Parse(json.ToJsonString()));
        }
    }
    private static JsonObject RichResponse()
    {
        var json = JsonNode.Parse(RecordingHttpHandler.ValidJson)!.AsObject();
        json["lemma"] = " INTRODUCE ";
        json["form_note"] = "introduce 的过去式和过去分词。";
        json["grammar"] = new JsonArray(new JsonObject { ["pattern"] = "introduce A to B", ["explanation"] = "把 A 介绍给 B。" });
        json["related"] = new JsonArray(new JsonObject { ["word"] = "launch", ["relation"] = "synonym", ["explanation"] = "推出产品或服务时意思接近。" });
        return json;
    }
}
