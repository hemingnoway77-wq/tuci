using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VocabCollector.Core.Library;
using VocabCollector.Core.Sync;

namespace VocabCollector.Tests;

public sealed class PracticeBankClientTests
{
    [Fact]
    public async Task BanksUseTheExistingDeviceCredentialsAndOnlyTheRequestedNoteScope()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://sync.fixture.invalid/v1/practice/banks", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture-token", request.Headers.Authorization!.ToString());
            Assert.Equal("fixture-vault", request.Headers.GetValues("X-Tuci-Vault").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(new[] { "note-a", "note-b" }, body.RootElement.GetProperty("noteIds").EnumerateArray().Select(id => id.GetString()));
            return Reply(new CloudPracticeBankPage([Bank("note-b")]));
        });
        using var http = new HttpClient(handler);
        var page = await Client(http).PracticeBanksAsync(["note-a", "note-b", "note-a"], default);
        Assert.Equal("note-b", Assert.Single(page.Banks).NoteId);
        Assert.Equal(PracticeContextGenerationTests.Contexts(), page.Banks[0].Contexts);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("large")]
    public async Task InvalidOrOversizedBatchesAreRejectedBeforeHttp(string input)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Reply(new CloudPracticeBankPage([]))));
        using var http = new HttpClient(handler);
        string[] ids = input switch { "empty" => [], "invalid" => new[] { "note-a", "../other-vault" }, _ => Enumerable.Range(0, 33).Select(i => "note-" + i).ToArray() };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Client(http).PracticeBanksAsync(ids, default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null-banks")]
    [InlineData("null-bank")]
    [InlineData("foreign-note")]
    [InlineData("duplicate-note")]
    [InlineData("invalid-word")]
    [InlineData("empty-contexts")]
    [InlineData("null-context")]
    public async Task IncompleteOrOutOfScopeResponsesAreNeverReturnedToTheImporter(string damage)
    {
        var bank = Bank("note-a");
        var json = damage switch
        {
            "missing" => "{}", "null-banks" => "{\"banks\":null}", "null-bank" => "{\"banks\":[null]}",
            "foreign-note" => Serialize(new CloudPracticeBankPage([bank with { NoteId = "another-account-note" }])),
            "duplicate-note" => Serialize(new CloudPracticeBankPage([bank, bank])),
            "invalid-word" => Serialize(new CloudPracticeBankPage([bank with { Word = "../secret" }])),
            "empty-contexts" => Serialize(new CloudPracticeBankPage([bank with { Contexts = [] }])),
            _ => "{\"banks\":[{\"noteId\":\"note-a\",\"word\":\"introduced\",\"contexts\":[null]}]}"
        };
        using var handler = new Handler((_, _) => Task.FromResult(Json(json)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(http).PracticeBanksAsync(["note-a"], default));
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"banks\":[]} trailing-data")]
    public async Task TheEntireBoundedJsonDocumentMustDeserialize(string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json(json)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<JsonException>(() => Client(http).PracticeBanksAsync(["note-a"], default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseLimitAppliesToBothKnownLengthAndStreamingBodies(bool knownLength)
    {
        using var handler = new Handler((_, _) =>
        {
            HttpContent content = knownLength ? new ByteArrayContent(new byte[2 * 1024 * 1024 + 1])
                : new StreamContent(new NonSeekableBody(new byte[2 * 1024 * 1024 + 1]));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Client(http).PracticeBanksAsync(["note-a"], default));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task HttpStatusesRemainAvailableForDesktopSessionAndOldServerPolicy(HttpStatusCode status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        { Content = new StringContent("{\"error\":\"请求不可用\"}", Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SyncRequestException>(() => Client(http).PracticeBanksAsync(["note-a"], default));
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task CallerCancellationStopsTheRequestInsteadOfReturningAPartialBank()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            started.SetResult(); await Task.Delay(Timeout.Infinite, token); return Reply(new CloudPracticeBankPage([]));
        });
        using var http = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
        var pending = Client(http).PracticeBanksAsync(["note-a"], cancellation.Token);
        await started.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    internal static CloudPracticeBank Bank(string id) => new(id, "introduced", PracticeContextGenerationTests.Contexts());
    private static SyncClient Client(HttpClient http) => new(http, new Uri("https://sync.fixture.invalid/"), "fixture-token", "fixture-vault");
    private static string Serialize(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class NonSeekableBody(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return reply(request, ct); }
    }
}
