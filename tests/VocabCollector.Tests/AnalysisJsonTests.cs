using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Tests.Fakes;
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
}
