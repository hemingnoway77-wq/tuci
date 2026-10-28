using System.Text.Json;
using System.Text.Json.Nodes;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

public class AnalysisDiagnosticsTests
{
    private const string CanaryKey = "sk-diagnostic-canary-7654";
    private const string PrivateContext = "private-context-never-log-9832";
    private static readonly ApiOptions Options = new("https://host/v1", CanaryKey, "mock");

    [Fact]
    public async Task FormatFailureRecordsFieldMetadataWithoutKeyContextOrResponseAndUsesOneRequest()
    {
        var json = JsonNode.Parse(RecordingHttpHandler.ValidJson)!;
        var meaning = json["meanings"]![0]!.DeepClone();
        meaning["definition"] = PrivateContext + CanaryKey;
        json["meanings"] = new JsonArray(meaning.DeepClone(), meaning.DeepClone(), meaning.DeepClone(), meaning.DeepClone());
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(RecordingHttpHandler.JsonReply(json.ToJsonString())) };
        using var client = new HttpClient(handler);
        var sink = new RecordingDiagnostics();

        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client, sink)
            .AnalyzeAsync("resilient", PrivateContext, Options, default));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal("resilient", entry.Word);
        Assert.Equal(error.FieldPath, entry.FieldPath);
        Assert.Contains("meanings", entry.FieldPath);
        Assert.Equal(4, entry.ActualCount);
        var record = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain(CanaryKey, record);
        Assert.DoesNotContain(PrivateContext, record);
        Assert.DoesNotContain("phonetics", record);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SuccessfulAnalysisDoesNotWriteDiagnosticsOrIssueExtraRequests()
    {
        using var handler = new RecordingHttpHandler();
        using var client = new HttpClient(handler);
        var sink = new RecordingDiagnostics();
        await new OpenAiCompatibleAnalyzer(client, sink).AnalyzeAsync("resilient", PrivateContext, Options, default);
        Assert.Empty(sink.Entries);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UnwritableDiagnosticsCannotReplaceTheOriginalAnalysisError()
    {
        using var handler = new RecordingHttpHandler { Reply = _ => Task.FromResult(RecordingHttpHandler.JsonReply("{}")) };
        using var client = new HttpClient(handler);
        var sink = new RecordingDiagnostics { Fail = true };
        var error = await Assert.ThrowsAsync<AnalysisException>(() => new OpenAiCompatibleAnalyzer(client, sink)
            .AnalyzeAsync("resilient", "", Options, default));
        Assert.Contains("AI 返回", error.Message);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, sink.Attempts);
    }

    [Fact]
    public async Task FileSinkWritesOneBoundedJsonLineAndCreatesNoApiConfiguration()
    {
        using var fixture = new LogFixture();
        await fixture.Sink.RecordAsync(new(DateTimeOffset.Now, "credit", "invalid_count", "meanings", "1..3 items", 4));
        var file = Assert.Single(Directory.GetFiles(fixture.Directory, "*.jsonl"));
        var line = Assert.Single(await File.ReadAllLinesAsync(file));
        using var document = JsonDocument.Parse(line);
        Assert.Equal("credit", document.RootElement.GetProperty("word").GetString());
        Assert.Equal("meanings", document.RootElement.GetProperty("fieldPath").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("actualCount").GetInt32());
        Assert.DoesNotContain("apiKey", line, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "settings.json")));
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }

    [Fact]
    public async Task FileSinkRotatesLargeLogsAndPrunesOnlyItsOldDatedFiles()
    {
        using var fixture = new LogFixture();
        Directory.CreateDirectory(fixture.Directory);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var path = Path.Combine(fixture.Directory, "analysis-" + today + ".jsonl");
        await File.WriteAllTextAsync(path, new string('x', 256 * 1024));
        var old = Path.Combine(fixture.Directory, "analysis-" + DateTime.Now.AddDays(-10).ToString("yyyy-MM-dd") + ".jsonl");
        await File.WriteAllTextAsync(old, "old diagnostic");
        var unrelated = Path.Combine(fixture.Directory, "my-notes.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        var similarlyNamed = Path.Combine(fixture.Directory, "analysis-" + DateTime.Now.AddDays(-10).ToString("yyyy-MM-dd") + "-my-notes.jsonl");
        await File.WriteAllTextAsync(similarlyNamed, "keep unrelated notes");

        await fixture.Sink.RecordAsync(new(DateTimeOffset.Now, "produce", "invalid_type", "phonetics.uk", "string"));

        Assert.True(new FileInfo(path).Length < 2048);
        Assert.Equal(256 * 1024, new FileInfo(path + ".1").Length);
        Assert.False(File.Exists(old));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.True(File.Exists(similarlyNamed), "Retention must only remove exact log filenames.");
    }

    [Fact]
    public async Task ConcurrentRecordsRemainSeparateJsonLines()
    {
        using var fixture = new LogFixture();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => fixture.Sink.RecordAsync(
            new(DateTimeOffset.Now, "credit", "invalid_count", "meanings", "1..3", index))));
        var lines = await File.ReadAllLinesAsync(Assert.Single(Directory.GetFiles(fixture.Directory, "*.jsonl")));
        Assert.Equal(20, lines.Length);
        foreach (var line in lines) using (JsonDocument.Parse(line)) { }
    }

    private sealed class RecordingDiagnostics : IAnalysisDiagnostics
    {
        public List<AnalysisDiagnostic> Entries { get; } = [];
        public int Attempts;
        public bool Fail;
        public Task RecordAsync(AnalysisDiagnostic diagnostic, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Fail) throw new IOException("diagnostic-write-failed");
            Entries.Add(diagnostic);
            return Task.CompletedTask;
        }
    }

    private sealed class LogFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "vocab-logs-" + Guid.NewGuid().ToString("N"));
        public FileAnalysisDiagnostics Sink => _sink ??= new(Directory);
        private FileAnalysisDiagnostics? _sink;
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
