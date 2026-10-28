using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Desktop.Smoke;
public enum ApiMode { Normal, SpellingCorrected, SpellingUncertain, SpellingInvalid, MissingSpelling, FormatViolation, Unauthorized, Malformed, Slow }
public sealed class MockApiServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Task _loop;
    private int _requests;
    public int Requests => Volatile.Read(ref _requests);
    public ApiMode Mode { get; set; }
    public string BaseUrl { get; }
    public MockApiServer()
    {
        _listener.Start(); BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";
        _loop = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream(); using var header = new MemoryStream(); var next = new byte[1];
                while (header.Length < 65536)
                {
                    await stream.ReadExactlyAsync(next, _stop.Token); header.WriteByte(next[0]);
                    var bytes = header.GetBuffer(); var n = (int)header.Length;
                    if (n >= 4 && bytes[n-4] == 13 && bytes[n-3] == 10 && bytes[n-2] == 13 && bytes[n-1] == 10) break;
                }
                var headers = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                if (!headers[0].StartsWith("POST /v1/chat/completions ")) throw new IOException("Unexpected request endpoint");
                var length = int.Parse(headers.Single(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
                if (length is < 0 or > 65536) throw new IOException("Request too large");
                var requestBytes = new byte[length]; await stream.ReadExactlyAsync(requestBytes, _stop.Token);
                using var requestJson = JsonDocument.Parse(requestBytes);
                var userContent = requestJson.RootElement.GetProperty("messages").EnumerateArray()
                    .Single(message => message.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
                using var input = JsonDocument.Parse(userContent);
                var requestWord = input.RootElement.GetProperty("word").GetString()!;
                var mode = Mode; Interlocked.Increment(ref _requests);
                if (mode == ApiMode.Slow) await Task.Delay(3000, _stop.Token);
                var body = mode switch
                {
                    ApiMode.Unauthorized => "echo sk-smoke-canary-secret",
                    ApiMode.Malformed => "not JSON",
                    _ => Analysis(mode, requestWord).ToJsonString()
                };
                if (mode is ApiMode.Normal or ApiMode.SpellingCorrected or ApiMode.SpellingUncertain or ApiMode.SpellingInvalid or ApiMode.MissingSpelling or ApiMode.FormatViolation)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"), _stop.Token);
                    for (var offset = 0; offset < body.Length; offset += 60)
                    {
                        var fragment = body.Substring(offset, Math.Min(60, body.Length - offset));
                        var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = fragment }, finish_reason = (string?)null } } });
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + chunk + "\n\n"), _stop.Token);
                        await Task.Delay(20, _stop.Token);
                    }
                    var finish = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { }, finish_reason = "stop" } } });
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + finish + "\n\ndata: [DONE]\n\n"), _stop.Token);
                    return;
                }
                var payload = Encoding.UTF8.GetBytes(body);
                var status = mode == ApiMode.Unauthorized ? "401 Unauthorized" : "200 OK";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(payload, _stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
        }
    }
    private static JsonNode Analysis(ApiMode mode, string requestWord)
    {
        if (mode == ApiMode.FormatViolation)
        {
            var invalid = JsonNode.Parse(RecordingHttpHandler.ValidJson.Replace("resilient", requestWord))!;
            invalid["phonetics"]!["uk"] = new JsonArray("sk-smoke-canary-secret");
            return invalid;
        }
        if (mode is ApiMode.SpellingUncertain or ApiMode.SpellingInvalid)
            return JsonSerializer.SerializeToNode(new
            {
                is_valid_word = false, error = mode == ApiMode.SpellingUncertain ? "存在多个可能的拼写" : "无法识别为有效英文单词",
                spelling = new
                {
                    input_word = requestWord, word = "", status = mode == ApiMode.SpellingUncertain ? "uncertain" : "invalid",
                    suggestions = mode == ApiMode.SpellingUncertain ? new[] { "their", "there" } : Array.Empty<string>()
                }
            })!;
        if (mode == ApiMode.SpellingCorrected)
            return JsonSerializer.SerializeToNode(new
            {
                is_valid_word = true, error = "",
                spelling = new { input_word = requestWord, word = "introduce", status = "corrected", suggestions = Array.Empty<string>() },
                lemma = "introduce", form_note = "introduce的过去式和过去分词是introduced。",
                phonetics = new { uk = "/ˌɪntrəˈdjuːs/", us = "/ˌɪntrəˈduːs/" }, primary_pos = "verb",
                meanings = new[]
                {
                    new { part_of_speech = "verb", definition = "介绍某人或某事" },
                    new { part_of_speech = "verb", definition = "引入新的事物或方法" }
                },
                examples = new[]
                {
                    new { english = "Let me introduce my friend to you.", chinese = "让我把我的朋友介绍给你。" },
                    new { english = "Our school will introduce a new rule tomorrow.", chinese = "我们学校明天会推出一条新规定。" }
                },
                collocations = new[]
                {
                    new { english = "introduce a friend", chinese = "介绍一位朋友" },
                    new { english = "introduce a new idea", chinese = "引入一个新想法" },
                    new { english = "introduce someone to a team", chinese = "将某人介绍给团队" }
                },
                grammar = new[] { new { pattern = "introduce someone to someone", explanation = "把前一个人介绍给后一个人。" } },
                related = Array.Empty<object>(), roots = Array.Empty<object>()
            })!;
        var analysis = JsonNode.Parse(RecordingHttpHandler.ValidJson)!;
        analysis["roots"] = JsonSerializer.SerializeToNode(new[] { new { root = "sal", meaning = "跳跃，源自拉丁语 salire", variants = new[] { "sult" } } });
        analysis["meanings"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { part_of_speech = "adjective", definition = "能迅速恢复的" }));
        analysis["lemma"] = "resilient";
        analysis["form_note"] = "resilient的副词是resiliently。";
        if (mode == ApiMode.MissingSpelling) analysis.AsObject().Remove("spelling");
        else analysis["spelling"] = JsonSerializer.SerializeToNode(new { input_word = requestWord, word = requestWord, status = "valid", suggestions = Array.Empty<string>() });
        return analysis;
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _loop; await Task.WhenAll(_connections); _stop.Dispose();
    }
}
