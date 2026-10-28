using System.Net;
using System.Text.Json;
using System.Text.Encodings.Web;
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
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.DoesNotContain(Canary, handler.Body); Assert.Contains("source sentence", handler.Body);
        Assert.False(body.RootElement.TryGetProperty("response_format", out _)); Assert.Equal(1, handler.RequestCount);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
    }
    [Fact] public async Task Request_DescribesLearningFieldsAndSendsOnlyWordAndContextAsData()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(RecordingHttpHandler.JsonReply(RecordingHttpHandler.ValidJson.Replace("resilient", "introduced"))) };
        using var client = new HttpClient(handler);
        await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("introduced", "The team introduced a new policy.", Options, default);
        using var body = JsonDocument.Parse(handler.Body);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        var instructions = messages[0].GetProperty("content").GetString()!;
        Assert.True(instructions.Length <= 1600, $"Prompt length was {instructions.Length}");
        var schemaLine = instructions.Split('\n').Single(x => x.TrimStart().StartsWith("{\"is_valid_word\":true", StringComparison.Ordinal));
        using var schema = JsonDocument.Parse(schemaLine);
        Assert.True(schema.RootElement.TryGetProperty("lemma", out _));
        Assert.True(schema.RootElement.TryGetProperty("form_note", out _));
        Assert.True(schema.RootElement.TryGetProperty("grammar", out var grammar));
        Assert.True(grammar[0].TryGetProperty("pattern", out _));
        Assert.True(grammar[0].TryGetProperty("explanation", out _));
        Assert.True(schema.RootElement.GetProperty("related")[0].TryGetProperty("explanation", out _));
        Assert.True(schema.RootElement.TryGetProperty("roots", out _));
        var spelling = schema.RootElement.GetProperty("spelling");
        Assert.Equal(new[] { "input_word", "word", "status", "suggestions" }, spelling.EnumerateObject().Select(x => x.Name));
        using var data = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(new[] { "word", "context" }, data.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal("introduced", data.RootElement.GetProperty("word").GetString());
        Assert.Equal("The team introduced a new policy.", data.RootElement.GetProperty("context").GetString());
    }
    [Fact] public async Task CorrectedResponseReturnsFullAnalysisFromOneRequest()
    {
        var json = RecordingHttpHandler.ValidJson.Replace("resilient", "receive")
            .Replace("\"input_word\":\"receive\"", "\"input_word\":\"recieve\"").Replace("\"status\":\"valid\"", "\"status\":\"corrected\"");
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(RecordingHttpHandler.JsonReply(json)) };
        using var client = new HttpClient(handler);
        var result = await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("recieve", "I recieve a letter.", Options, default);
        Assert.Equal("receive", result.Spelling!.Word); Assert.Equal(SpellingStatus.Corrected, result.Spelling.Status);
        Assert.Equal(2, result.Examples.Count); Assert.Single(result.Meanings); Assert.Equal(1, handler.RequestCount);
    }
    [Fact] public async Task AcceptedResponseWithoutSpellingIsRejectedWithoutRetry()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(RecordingHttpHandler.ValidJson)!.AsObject(); json.Remove("spelling");
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(RecordingHttpHandler.JsonReply(json.ToJsonString())) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default));
        Assert.Equal(1, handler.RequestCount);
    }
    [Fact] public async Task ResponseForAnotherInputIsRejectedWithoutRetry()
    {
        using var handler = new RecordingHttpHandler(); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("different", "", Options, default));
        Assert.Equal(1, handler.RequestCount);
    }
    [Fact] public async Task AmbiguousResponseRetainsSuggestionsWithoutExtraRequest()
    {
        const string json = "{\"is_valid_word\":false,\"error\":\"可能有多个拼写，请选择。\",\"spelling\":{\"input_word\":\"wier\",\"word\":\"\",\"status\":\"uncertain\",\"suggestions\":[\"wire\",\"weir\"]}}";
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(RecordingHttpHandler.JsonReply(json)) };
        using var client = new HttpClient(handler);
        var result = await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("wier", "", Options, default);
        Assert.False(result.IsValidWord); Assert.Equal(new[] { "wire", "weir" }, result.Spelling!.Suggestions); Assert.Equal(1, handler.RequestCount);
    }
    [Theory]
    [InlineData("https://api.deepseek.com/v1", "deepseek-flash", true)]
    [InlineData("https://API.DEEPSEEK.COM", "DEEPSEEK-V4-PRO", true)]
    [InlineData("https://api.deepseek.com", "deepseek-chat", false)]
    [InlineData("https://api.deepseek.com", "deepseek-flash-custom", false)]
    [InlineData("http://api.deepseek.com/v1", "deepseek-flash", false)]
    [InlineData("https://api.deepseek.com.example.com/v1", "deepseek-flash", false)]
    [InlineData("https://another-provider.example/v1", "deepseek-flash", false)]
    [InlineData("http://192.168.1.100:1234/v1", "deepseek-flash", false)]
    [InlineData("http://localhost:1234/v1", "deepseek-v4-pro", false)]
    public async Task Request_UsesDocumentedFastModeOnlyForExactOfficialDeepSeekModels(string url, string model, bool fast)
    {
        using var handler = new RecordingHttpHandler(); using var client = new HttpClient(handler);
        await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options with { BaseUrl = url, Model = model }, default);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal(fast, body.RootElement.TryGetProperty("thinking", out var thinking));
        if (fast) Assert.Equal("disabled", thinking.GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(body.RootElement.TryGetProperty("response_format", out _));
    }
    [Fact] public async Task StreamingResponseAssemblesUtf8AndMultilineEvents()
    {
        var json = RecordingHttpHandler.ValidJson;
        var middle = json.Length / 2;
        var jsonOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var first = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = json[..middle] }, finish_reason = (string?)null } } }, jsonOptions);
        var second = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = json[middle..] }, finish_reason = "stop" } } }, jsonOptions);
        var stream = ": heartbeat\r\ndata: " + first.Replace("{\"choices\":", "{\r\ndata: \"choices\":") + "\r\n\r\ndata: " + second + "\r\n\r\ndata: [DONE]\r\n\r\n";
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(StreamReply(stream)) };
        using var client = new HttpClient(handler);
        var analysis = await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default);
        Assert.Equal("有韧性的", Assert.Single(analysis.Meanings).Definition);
        Assert.Equal(1, handler.RequestCount);
    }
    [Fact] public async Task StreamingProgressArrivesBeforeCompletionAndIgnoresReasoning()
    {
        var first = Chunk("{", null);
        var rest = Chunk(RecordingHttpHandler.ValidJson[1..], "stop") + "data: [DONE]\n\n";
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"private reasoning\"},\"finish_reason\":null}]}\n\n" + first;
        var bytes = System.Text.Encoding.UTF8.GetBytes(prefix + rest);
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new GatedReadStream(bytes, System.Text.Encoding.UTF8.GetByteCount(prefix), release.Task)) { Headers = { ContentType = new("text/event-stream") } } }) };
        using var client = new HttpClient(handler);
        List<int> progress = [];
        var running = new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, new InlineProgress<int>(count => { progress.Add(count); firstReceived.TrySetResult(); }), default);
        await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(running.IsCompleted); Assert.Equal(new[] { 1 }, progress);
        release.SetResult();
        Assert.True((await running).IsValidWord);
        Assert.Equal(RecordingHttpHandler.ValidJson.Length, progress[^1]);
    }
    [Fact] public async Task StreamingCancellationAfterFirstContentDoesNotReturnPartialAnalysis()
    {
        using var cts = new CancellationTokenSource();
        var first = Chunk("{", null);
        var bytes = System.Text.Encoding.UTF8.GetBytes(first + Chunk(RecordingHttpHandler.ValidJson[1..], "stop") + "data: [DONE]\n\n");
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new GatedReadStream(bytes, System.Text.Encoding.UTF8.GetByteCount(first), new TaskCompletionSource().Task)) { Headers = { ContentType = new("text/event-stream") } } }) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, new InlineProgress<int>(_ => cts.Cancel()), cts.Token));
    }
    [Theory][InlineData("length", true)][InlineData(null, true)][InlineData("stop", false)]
    public async Task StreamingRequiresStopAndDoneEvenForCompleteContent(string? reason, bool done)
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(StreamReply(Chunk(RecordingHttpHandler.ValidJson, reason) + (done ? "data: [DONE]\n\n" : ""))) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default));
    }
    [Fact] public async Task StreamingAcceptsUtf8BomAndCrOnlyLines()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(StreamReply("\uFEFF" + (Chunk(RecordingHttpHandler.ValidJson, "stop") + "data: [DONE]\n\n").Replace("\n", "\r"))) };
        using var client = new HttpClient(handler);
        Assert.True((await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default)).IsValidWord);
    }
    [Fact] public async Task StreamingUnknownLengthIsBoundedBeforeAnEventCompletes()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new UnknownLengthStream(System.Text.Encoding.UTF8.GetBytes("data: " + new string('x', 1048577)))) { Headers = { ContentType = new("text/event-stream") } } }) };
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default));
        Assert.Contains("过大", error.Message);
    }
    [Fact] public async Task StreamingDoesNotAcceptSecretEchoesAsNoteContent()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(StreamReply(Chunk(RecordingHttpHandler.ValidJson.Replace("有韧性的", Canary), "stop") + "data: [DONE]\n\n")) };
        using var client = new HttpClient(handler);
        var result = await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default);
        Assert.DoesNotContain(Canary, Assert.Single(result.Meanings).Definition);
    }
    private static string Chunk(string content, string? reason) => "data: " + JsonSerializer.Serialize(new
    { choices = new[] { new { index = 0, delta = new { content }, finish_reason = reason } } }) + "\n\n";
    [Theory]
    [InlineData("data: {\"choices\":[{\"delta\":{\"refusal\":\"no\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n")]
    [InlineData("data: {\"error\":{\"message\":\"secret-key-canary\"}}\n\n")]
    [InlineData("data: {broken}\n\n")]
    [InlineData("data: [DONE]\n\n")]
    public async Task StreamingMalformedRefusedOrIncompleteResponseIsRejected(string stream)
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(StreamReply(stream)) };
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default));
        Assert.DoesNotContain("secret-key-canary", error.Message); Assert.Equal(1, handler.RequestCount);
    }
    [Theory]
    [InlineData(400, "stream is not supported")]
    [InlineData(422, "stream not supported")]
    [InlineData(400, "unsupported parameter: stream")]
    public async Task ExplicitUnsupportedStreamingFallsBackOnce(int status, string message)
    {
        using var handler = new RecordingHttpHandler();
        handler.Reply = _ => Task.FromResult(handler.RequestCount == 1
            ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } })) }
            : RecordingHttpHandler.JsonReply(RecordingHttpHandler.ValidJson));
        using var client = new HttpClient(handler);
        Assert.True((await new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default)).IsValidWord);
        Assert.Equal(2, handler.RequestCount);
        using var body = JsonDocument.Parse(handler.Body); Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
    }
    [Fact] public async Task GenericBadRequestDoesNotRetry()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid model") }) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default));
        Assert.Equal(1, handler.RequestCount);
    }
    [Theory]
    [InlineData(400, "upstream model is not supported")]
    [InlineData(422, "upstream model is unsupported")]
    [InlineData(400, "model is not supported; stream is enabled")]
    public async Task UnsupportedUpstreamOrModelDoesNotRetry(int status, string message)
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } })) }) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default));
        Assert.Equal(1, handler.RequestCount);
    }
    private static HttpResponseMessage StreamReply(string body) => new(HttpStatusCode.OK)
    { Content = new StreamContent(new FragmentedReadStream(System.Text.Encoding.UTF8.GetBytes(body))) { Headers = { ContentType = new("text/event-stream") } } };

    private sealed class FragmentedReadStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
    }
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    { public override bool CanSeek => false; }
    private sealed class GatedReadStream(byte[] data, int boundary, Task release) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= boundary) await release.WaitAsync(cancellationToken);
            var limit = Position < boundary ? Math.Min(buffer.Length, boundary - (int)Position) : buffer.Length;
            return await base.ReadAsync(buffer[..limit], cancellationToken);
        }
    }
    [Theory][InlineData(401)][InlineData(429)][InlineData(500)][InlineData(404)]
    public async Task ApiError_DoesNotEchoKeyOrRetry(int status)
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("leaked " + Canary) }) };
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default));
        Assert.DoesNotContain(Canary,error.Message); Assert.Equal(1,handler.RequestCount);
    }
    [Fact] public async Task MalformedOrTruncatedResponseIsNotAccepted()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"length\"}]}") }) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default));
    }
    [Fact] public async Task CancellationAndTimeoutAreDistinct()
    {
        using var handler = new RecordingHttpHandler { Reply = async ct => { await Task.Delay(5000, ct); return RecordingHttpHandler.JsonReply(RecordingHttpHandler.ValidJson); } };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var analyzer = new OpenAiCompatibleAnalyzer(client);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => analyzer.AnalyzeAsync("resilient", "", Options, default));
        Assert.Contains("超时", error.Message);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync("resilient", "", Options, cts.Token));
    }
    [Fact] public async Task OversizedResponseIsRejected()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1048577)) }) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client).AnalyzeAsync("resilient", "", Options, default));
    }
}
