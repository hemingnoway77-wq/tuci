using VocabCollector.Core;
using System.Text.RegularExpressions;
namespace VocabCollector.Tests.Fakes;
public sealed class FakeWordAnalyzer : IWordAnalyzer
{
    public int Calls { get; private set; }
    public Func<CancellationToken, Task<WordAnalysis>> Analyze { get; set; } = _ => Task.FromResult(TestData.Analysis);
    public async Task<WordAnalysis> AnalyzeAsync(string word, string context, ApiOptions options, CancellationToken ct)
    {
        Calls++;
        var analysis = await Analyze(ct);
        if (analysis.Spelling is not null || !analysis.IsValidWord) return analysis;
        // Legacy trusted fixtures serve several surface forms; explicit protocol fixtures stay untouched.
        return analysis with
        {
            Spelling = new(word, word, SpellingStatus.Valid, []),
            Examples = analysis.Examples.Select(example => Regex.IsMatch(example.English,
                @"(?<![A-Za-z'-])" + Regex.Escape(word) + @"(?![A-Za-z'-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                ? example : new BilingualText($"I learned {word} today.", "我今天学了这个词。")).ToArray()
        };
    }
}
public sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
