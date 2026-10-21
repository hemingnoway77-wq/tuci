using System.Net;
using System.Text;
using System.Text.Json;
namespace VocabCollector.Tests.Fakes;
public sealed class RecordingHttpHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    public string Body { get; private set; } = "";
    public string Authorization { get; private set; } = "";
    public Uri? Address { get; private set; }
    public Func<CancellationToken, Task<HttpResponseMessage>> Reply { get; set; } = _ => Task.FromResult(JsonReply(ValidJson));
    public const string ValidJson = """
    {"is_valid_word":true,"error":"","phonetics":{"uk":"/rɪˈzɪliənt/","us":""},"primary_pos":"adjective","meanings":[{"part_of_speech":"adjective","definition":"有韧性的"}],"examples":[{"english":"She remained resilient.","chinese":"她依然坚韧。"},{"english":"The economy is resilient.","chinese":"经济具有韧性。"}],"collocations":[{"english":"a resilient economy","chinese":"韧性强的经济体"},{"english":"resilient communities","chinese":"具有韧性的社区"}],"related":[{"word":"resilience","relation":"same_root"}]}
    """;
    public static HttpResponseMessage JsonReply(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } }), Encoding.UTF8, "application/json")
    };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestCount++; Address = request.RequestUri; Authorization = request.Headers.Authorization?.ToString() ?? "";
        Body = await request.Content!.ReadAsStringAsync(ct);
        return await Reply(ct);
    }
}
