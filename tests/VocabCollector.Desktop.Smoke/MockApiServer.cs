using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Desktop.Smoke;
public enum ApiMode { Normal, Unauthorized, Malformed, Slow }
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
                var mode = Mode; Interlocked.Increment(ref _requests);
                if (mode == ApiMode.Slow) await Task.Delay(3000, _stop.Token);
                var body = mode switch
                {
                    ApiMode.Unauthorized => "echo sk-smoke-canary-secret",
                    ApiMode.Malformed => "not JSON",
                    _ => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = RecordingHttpHandler.ValidJson }, finish_reason = "stop" } } })
                };
                var payload = Encoding.UTF8.GetBytes(body);
                var status = mode == ApiMode.Unauthorized ? "401 Unauthorized" : "200 OK";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(payload, _stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _loop; await Task.WhenAll(_connections); _stop.Dispose();
    }
}
