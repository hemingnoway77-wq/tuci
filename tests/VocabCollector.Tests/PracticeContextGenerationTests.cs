using System.Net;
using System.Text;
using System.Text.Json;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public sealed class PracticeContextGenerationTests
{
    private const string Secret = "sk-practice-private-canary";
    private static readonly ApiOptions Options = new("http://127.0.0.1:8765/custom/v1", Secret, "configured-model");
    internal static PracticeSense[] Senses() =>
    [
        new("teacher-sense", "introduced", "introduce", "vt. 介绍；引见", "", "过去式", 1, 2),
        new("policy-sense", "introduced", "introduce", "vt. 引进；采用", "", "过去式", 2, 2)
    ];
    internal static PracticeContext[] Contexts() =>
    [
        new("teacher-sense", "The teacher introduced her new student to the class.", "老师向全班介绍了她的新学生。"),
        new("policy-sense", "Our company introduced a new policy last week.", "我们公司上周采用了一项新政策。")
    ];

    [Fact]
    public async Task ExplicitGenerationUsesConfiguredNonStreamingEndpointAndBindsEverySenseAsData()
    {
        using var handler = new Handler(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:8765/custom/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer " + Secret, request.Headers.Authorization!.ToString());
            var body = await request.Content!.ReadAsStringAsync(); Assert.DoesNotContain(Secret, body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("configured-model", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            var messages = json.RootElement.GetProperty("messages"); Assert.Equal(2, messages.GetArrayLength());
            Assert.Equal("system", messages[0].GetProperty("role").GetString());
            Assert.Contains("6", messages[0].GetProperty("content").GetString());
            Assert.Contains("24", messages[0].GetProperty("content").GetString());
            Assert.Contains("指令", messages[0].GetProperty("content").GetString());
            using var data = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Assert.Equal("introduced", data.RootElement.GetProperty("word").GetString());
            Assert.Equal(new[] { "teacher-sense", "policy-sense" }, data.RootElement.GetProperty("senses").EnumerateArray().Select(value => value.GetProperty("sense_id").GetString()));
            Assert.Contains("介绍", data.RootElement.GetProperty("senses")[0].GetProperty("meaning").GetString());
            return Completion(Contexts());
        });
        using var client = new HttpClient(handler);
        var result = await new OpenAiPracticeContextGenerator(client).GenerateAsync(" Introduced ", Senses(), Options, default);
        Assert.Equal(Contexts(), result); Assert.Equal(1, handler.Calls);
        Assert.Equal(2, PracticeQuestionBank.WithContexts(Senses(), result).Count);
    }

    [Theory]
    [InlineData(null, "\n", true)]
    [InlineData("json", "\n", true)]
    [InlineData("JSON", "\r\n", true)]
    [InlineData("json", "\n", false)]
    public async Task BondContextBankAcceptsCompleteJsonFencesAndStillRejectsUnapprovedPlural(string? fenceLanguage, string newline, bool exactForm)
    {
        var note = new LibraryNote("bond-note", "名词/bond.md", "bond.md", """
            # bond

            **释义**：
            > n. 纽带；感情联系
            > n. 债券
            > vt. 粘合；使连接
            """, DateTimeOffset.UnixEpoch);
        var senses = PracticeQuestionBank.Build(note);
        Assert.Equal(3, senses.Count);
        var debtSentence = exactForm ? "She bought a government bond for her retirement savings."
            : "She bought some government bonds for her retirement savings.";
        PracticeContext[] contexts =
        [
            new(senses[0].Id, "The two sisters share a close bond after years apart.", "姐妹俩分别多年后仍关系亲密。"),
            new(senses[1].Id, debtSentence, "她买了一只政府债券作为退休储蓄。"),
            new(senses[2].Id, "Use this glue to bond the two pieces of wood.", "用这种胶水把两块木头粘起来。")
        ];
        var modelContent = JsonSerializer.Serialize(new { contexts = contexts.Select((context, index) => new
        {
            sense_id = context.SenseId, english = context.English, chinese = context.Chinese,
            answer_form = !exactForm && index == 1 ? "bonds" : "bond"
        }) });
        if (fenceLanguage is not null) modelContent = "```" + fenceLanguage + newline + modelContent + newline + "```";
        using var handler = new Handler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = modelContent } } }
        }))));
        using var client = new HttpClient(handler);
        var generator = new OpenAiPracticeContextGenerator(client);
        if (exactForm) Assert.Equal(contexts, await generator.GenerateAsync("bond", senses, Options, default));
        else await Assert.ThrowsAsync<AnalysisException>(() => generator.GenerateAsync("bond", senses, Options, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SecretsEchoedInsideModelJsonAreRemovedBeforeContextsCanReachCacheOrUi()
    {
        var contexts = Contexts(); contexts[0] = contexts[0] with { Chinese = "老师介绍了新学生。 " + Secret };
        using var handler = new Handler((_, _) => Task.FromResult(Completion(contexts)));
        using var client = new HttpClient(handler);
        var result = await new OpenAiPracticeContextGenerator(client).GenerateAsync("introduced", Senses(), Options, default);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("key")]
    [InlineData("model")]
    [InlineData("word")]
    [InlineData("senses")]
    public async Task InvalidInputsCannotStartAnyHttpRequest(string invalid)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("HTTP must not start."));
        using var client = new HttpClient(handler);
        var options = invalid switch { "api" => Options with { BaseUrl = "file:///secret" }, "key" => Options with { ApiKey = "" }, "model" => Options with { Model = "" }, _ => Options };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new OpenAiPracticeContextGenerator(client).GenerateAsync(
            invalid == "word" ? "whole private sentence" : "introduced", invalid == "senses" ? [] : Senses(), options, default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailuresAreFriendlyAndNeverEchoProviderDetailsOrRetry(int status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("private-response-canary " + Secret) }));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiPracticeContextGenerator(client).GenerateAsync("introduced", Senses(), Options, default));
        Assert.DoesNotContain("private-response-canary", error.Message); Assert.DoesNotContain(Secret, error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("lemma")]
    [InlineData("markup")]
    [InlineData("chinese")]
    public async Task InvalidOrIncompleteSenseBindingNeverBecomesAUsableContextSet(string invalid)
    {
        var contexts = Contexts();
        contexts = invalid switch
        {
            "missing" => [contexts[0]], "duplicate" => [contexts[0], contexts[0]],
            "unknown" => [contexts[0], contexts[1] with { SenseId = "wrong-sense" }],
            "lemma" => [contexts[0] with { English = "The teacher will introduce her new student tomorrow." }, contexts[1]],
            "markup" => [contexts[0] with { English = "<b>The teacher introduced her new student today.</b>" }, contexts[1]],
            _ => [contexts[0] with { Chinese = "" }, contexts[1]]
        };
        using var handler = new Handler((_, _) => Task.FromResult(Completion(contexts)));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiPracticeContextGenerator(client).GenerateAsync("introduced", Senses(), Options, default));
        Assert.DoesNotContain(contexts[0].English, error.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"private\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{bad\"}}]}")]
    public async Task TruncatedAndMalformedResponsesAreRejectedWithoutResponseEcho(string response)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(response)));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiPracticeContextGenerator(client).GenerateAsync("introduced", Senses(), Options, default));
        Assert.DoesNotContain("private", error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseLimitAppliesToDeclaredAndStreamingLengths(bool declared)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024 + 1));
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = declared ? new ByteArrayContent(bytes) : new StreamContent(new Unseekable(bytes)) }));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiPracticeContextGenerator(client).GenerateAsync("introduced", Senses(), Options, default));
        Assert.Contains("过大", error.Message);
    }

    [Fact]
    public async Task TimeoutIsRetryableButCallerCancellationRemainsCancellationAndLateResultsAreIgnored()
    {
        using var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Completion(Contexts()); });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var generator = new OpenAiPracticeContextGenerator(client);
        Assert.Contains("超时", (await Assert.ThrowsAsync<AnalysisException>(() => generator.GenerateAsync("introduced", Senses(), Options, default))).Message);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var before = handler.Calls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync("introduced", Senses(), Options, cancellation.Token));
        Assert.Equal(before, handler.Calls);
    }

    [Fact]
    public async Task NetworkErrorsAndFaultMessagesCannotExposeTheConfiguredKey()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("private-response-canary " + Secret));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiPracticeContextGenerator(client).GenerateAsync("introduced", Senses(), Options, default));
        Assert.DoesNotContain(Secret, error.ToString()); Assert.DoesNotContain("private-response-canary", error.ToString());
    }

    private static HttpResponseMessage Completion(IReadOnlyList<PracticeContext> contexts) => Json(JsonSerializer.Serialize(new
    { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new { contexts = contexts.Select(item => new { sense_id = item.SenseId, english = item.English, chinese = item.Chinese }) }) } } } }));
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return reply(request, ct); }
    }
    private sealed class Unseekable(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
}
