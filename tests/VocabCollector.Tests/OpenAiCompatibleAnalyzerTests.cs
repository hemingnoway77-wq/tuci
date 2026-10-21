using System.Net;
using System.Text.Json;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class OpenAiCompatibleAnalyzerTests
{
    private const string Canary = "sk-unit-test-canary-784d";
    private static readonly ApiOptions Options = new("https://host/v1", Canary, "configured-model");
    [Fact] public async Task Request_AllowsHttpLanApiAndPreservesPortAndPrefix()
    {
        using var handler = new RecordingHttpHandler(); using var client = new HttpClient(handler);
        var result = await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options with { BaseUrl = "http://192.168.1.100:1234/v1" }, default);
        Assert.True(result.IsValidWord);
        Assert.Equal("http://192.168.1.100:1234/v1/chat/completions", handler.Address!.AbsoluteUri);
        Assert.Equal("Bearer " + Canary, handler.Authorization);
    }
    [Fact] public async Task Request_UsesConfiguredModelAndKeepsKeyOutOfMessages()
    {
        using var handler = new RecordingHttpHandler(); using var client = new HttpClient(handler);
        var result = await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "source sentence", Options, default);
        Assert.True(result.IsValidWord); Assert.Equal("Bearer " + Canary, handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("configured-model", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.DoesNotContain(Canary, handler.Body); Assert.Contains("source sentence", handler.Body);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _)); Assert.Equal(1, handler.RequestCount);
    }
    [Fact] public async Task Request_DescribesLearningFieldsAndSendsOnlyWordAndContextAsData()
    {
        using var handler = new RecordingHttpHandler(); using var client = new HttpClient(handler);
        await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("introduced", "The team introduced a new policy.", Options, default);
        using var body = JsonDocument.Parse(handler.Body);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        var instructions = messages[0].GetProperty("content").GetString()!;
        var schemaLine = instructions.Split('\n').Single(x => x.TrimStart().StartsWith("{\"is_valid_word\":true", StringComparison.Ordinal));
        using var schema = JsonDocument.Parse(schemaLine);
        Assert.True(schema.RootElement.TryGetProperty("lemma", out _));
        Assert.True(schema.RootElement.TryGetProperty("form_note", out _));
        Assert.True(schema.RootElement.TryGetProperty("grammar", out var grammar));
        Assert.True(grammar[0].TryGetProperty("pattern", out _));
        Assert.True(grammar[0].TryGetProperty("explanation", out _));
        Assert.True(schema.RootElement.GetProperty("related")[0].TryGetProperty("explanation", out _));
        using var data = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(new[] { "word", "context" }, data.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal("introduced", data.RootElement.GetProperty("word").GetString());
        Assert.Equal("The team introduced a new policy.", data.RootElement.GetProperty("context").GetString());
    }
    [Theory][InlineData(401)][InlineData(429)][InlineData(500)][InlineData(404)]
    public async Task ApiError_DoesNotEchoKeyOrRetry(int status)
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("leaked " + Canary) }) };
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("word", "", Options, default));
        Assert.DoesNotContain(Canary,error.Message); Assert.Equal(1,handler.RequestCount);
    }
    [Fact] public async Task MalformedOrTruncatedResponseIsNotAccepted()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"length\"}]}") }) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("word", "", Options, default));
    }
    [Fact] public async Task CancellationAndTimeoutAreDistinct()
    {
        using var handler = new RecordingHttpHandler { Reply = async ct => { await Task.Delay(5000, ct); return RecordingHttpHandler.JsonReply(RecordingHttpHandler.ValidJson); } };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var analyzer = new OpenAiCompatibleAnalyzer(client);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => analyzer.AnalyzeAsync("word", "", Options, default));
        Assert.Contains("超时", error.Message);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync("word", "", Options, cts.Token));
    }
    [Fact] public async Task OversizedResponseIsRejected()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1048577)) }) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("word", "", Options, default));
    }
}
