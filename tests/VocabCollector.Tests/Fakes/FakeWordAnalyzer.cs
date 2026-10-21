using VocabCollector.Core;
namespace VocabCollector.Tests.Fakes;
public sealed class FakeWordAnalyzer : IWordAnalyzer
{
    public int Calls { get; private set; }
    public Func<CancellationToken, Task<WordAnalysis>> Analyze { get; set; } = _ => Task.FromResult(TestData.Analysis);
    public Task<WordAnalysis> AnalyzeAsync(string word, string context, ApiOptions options, CancellationToken ct) { Calls++; return Analyze(ct); }
}
public sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
