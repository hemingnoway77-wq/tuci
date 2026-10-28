using System.Text;
using System.Text.Json;

namespace VocabCollector.Core.Ai;

public sealed record AnalysisDiagnostic(DateTimeOffset TimestampUtc, string Word, string ErrorCode,
    string FieldPath, string Rule, int? ActualCount = null);

public interface IAnalysisDiagnostics
{
    Task RecordAsync(AnalysisDiagnostic diagnostic, CancellationToken cancellationToken = default);
}

/// <summary>Small local failure records: never store prompts, model text, headers or credentials.</summary>
public sealed class FileAnalysisDiagnostics(string directory) : IAnalysisDiagnostics
{
    private const long FileLimit = 256 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task RecordAsync(AnalysisDiagnostic diagnostic, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "analysis-" + diagnostic.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= FileLimit) File.Move(path, path + ".1", true);
            var bounded = diagnostic with
            {
                Word = Limit(diagnostic.Word, 64), ErrorCode = Limit(diagnostic.ErrorCode, 80),
                FieldPath = Limit(diagnostic.FieldPath, 120), Rule = Limit(diagnostic.Rule, 180)
            };
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(bounded, Options) + "\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            // Only prune our own dated log names. Other application files are never touched.
            foreach (var old in Directory.EnumerateFiles(directory, "analysis-*.jsonl*"))
            {
                var name = Path.GetFileName(old);
                if (name.Length < 24 || !DateOnly.TryParseExact(name.AsSpan(9, 10), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day) || day >= DateOnly.FromDateTime(DateTime.Now).AddDays(-7)) continue;
                var expectedName = "analysis-" + day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl";
                if (name != expectedName && name != expectedName + ".1") continue;
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        finally { gate.Release(); }
    }

    private static string Limit(string? value, int length) => string.IsNullOrEmpty(value) ? "" : value.Length > length ? value[..length] : value;
}
