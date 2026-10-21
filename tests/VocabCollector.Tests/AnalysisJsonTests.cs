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
