using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Reading;
using VocabCollector.Core.Storage;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    public async Task RunManualReadingChecksAsync()
    {
        var run = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "reading-manual", Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(run, "isolated-vault");
        var config = Path.Combine(run, "config");
        Directory.CreateDirectory(vault);
        await CheckManualCaptureFixtureAsync(run);
        var analyzer = new ManualReadingAnalyzer();
        var translations = new ManualReadingTranslations();
        var store = new VaultStore();
        var settings = new AppSettings(new ApiOptions("https://manual-fixture.invalid/v1", "isolated-fixture-key", "fixture-model"), new VaultOptions(vault, "words", true));
        var capture = new MainViewModel(new CaptureService(analyzer, store, TimeProvider.System), settings, store);
        var themes = new ThemeManager(app.Resources, new AppearanceSettingsStore(config));
        await themes.LoadAsync();
        var feed = new RecordingReadingService();
        var article = feed.Articles[^1];
        var content = new ManualReadingContent();
        var main = new MainWindow(capture, new WindowsSettingsStore(config), new RecordingNavigator(), feed,
            url => Console.WriteLine("MANUAL source callback (no browser request): " + url), themes,
            learningStateDirectory: Path.Combine(config, "learning"), articleContentService: content, wordTranslationService: translations)
        {
            Title = "兔词 · READING MANUAL · isolated fixtures",
            Width = 1200,
            Height = 820
        };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Closed += (_, _) => closed.TrySetResult();
        app.MainWindow = main;
        main.Show();
        try
        {
            await SelectPageAsync(main, "ReadingTab");
            await Until(() => Descendants<ReadingPage>(main).Any(page => page.IsVisible && !page.ViewModel.IsBusy));
            var page = Descendants<ReadingPage>(main).Single(page => page.IsVisible);
            await page.OpenArticleAsync(article);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(page.IsArticleOpen && content.Calls == 1 && analyzer.Calls == 0 && translations.Calls == 0,
                "manual session starts with isolated full text and no automatic translation or generation");
            Console.WriteLine("MANUAL READY: " + main.Title);
            Console.WriteLine("MANUAL DIRECTORY: " + run);
            Console.WriteLine("Use the real pointer on resilient / apple / steady, then 翻译 or 收录. The right panel saves only to the isolated vault. Close this window to finish.");
            await closed.Task;
        }
        finally
        {
            if (main.IsVisible) main.Close();
            while (capture.IsBusy) await Task.Delay(20);
            var report = new
            {
                run,
                analyzerCalls = analyzer.Calls,
                analyzerCancelled = analyzer.Cancelled,
                translationCalls = translations.Calls,
                contentCalls = content.Calls,
                savedFiles = Directory.GetFiles(vault, "*.md", SearchOption.AllDirectories),
                captureMessage = capture.Message
            };
            await File.WriteAllTextAsync(Path.Combine(run, "session.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("MANUAL CLOSED: " + JsonSerializer.Serialize(report));
        }
    }

    private static async Task CheckManualCaptureFixtureAsync(string run)
    {
        var vault = Path.Combine(run, "fixture-self-check");
        Directory.CreateDirectory(vault);
        const string context = "A resilient learner can stay steady when plans change.";
        var settings = new AppSettings(new ApiOptions("https://manual-fixture.invalid/v1", "isolated-fixture-key", "fixture-model"), new VaultOptions(vault, "words", true));
        var service = new CaptureService(new ManualReadingAnalyzer(), new VaultStore(), TimeProvider.System);
        var result = await service.CaptureAsync(new("resilient", context), settings, null, CancellationToken.None);
        Check(result.Status == CaptureStatus.Saved && result.Notes.Count == 1, "manual analyzer passes the real CaptureService spelling and save rules: " + result.Message);
        var note = result.Notes.Single();
        VaultPaths.EnsureInside(vault, note.AbsolutePath);
        var markdown = await File.ReadAllTextAsync(note.AbsolutePath);
        Check(markdown == result.Markdown && markdown.Contains("隔离验证", StringComparison.Ordinal) && markdown.Contains(context, StringComparison.Ordinal),
            "manual fixture saves a genuine isolated Markdown note with the article sentence");
        Console.WriteLine("MANUAL FIXTURE SELF-CHECK PASSED: " + note.AbsolutePath);
    }

    private sealed class ManualReadingContent : IArticleContentService
    {
        public int Calls { get; private set; }
        public Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            IReadOnlyList<string> paragraphs =
            [
                "A resilient learner can stay steady when plans change. An apple tree offers a quiet place to read and think.",
                "Small habits help us build knowledge over time. Reading an interesting sentence gives each new word a useful context.",
                .. Enumerable.Range(1, 24).Select(index => $"A steady practice makes difficult ideas feel familiar. The resilient learner reads paragraph {index}, pauses to reflect, and continues with a fresh question.")
            ];
            return Task.FromResult(new ArticleContent(article, paragraphs, DateTimeOffset.UtcNow));
        }
    }

    private sealed class ManualReadingTranslations : IWordTranslationService
    {
        public int Calls { get; private set; }
        public async Task<WordTranslation> TranslateAsync(string word, CancellationToken cancellationToken)
        {
            Calls++;
            Console.WriteLine("MANUAL TRANSLATE: " + word);
            await Task.Delay(300, cancellationToken);
            return new(word, ManualMeaning(word));
        }
    }

    private sealed class ManualReadingAnalyzer : IWordAnalyzer
    {
        public int Calls { get; private set; }
        public int Cancelled { get; private set; }
        public async Task<WordAnalysis> AnalyzeAsync(string word, string context, ApiOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            Console.WriteLine("MANUAL GENERATE: " + word);
            try { await Task.Delay(1800, cancellationToken); }
            catch (OperationCanceledException) { Cancelled++; Console.WriteLine("MANUAL GENERATE CANCELLED: " + word); throw; }
            var pos = word.Equals("apple", StringComparison.OrdinalIgnoreCase) ? PartOfSpeech.Noun : PartOfSpeech.Adjective;
            var example = context.Contains(word, StringComparison.OrdinalIgnoreCase) ? context : $"I learned the word {word} today.";
            return new(true, "", "/ˈfiːkstʃə/", "/ˈfɪkstʃər/", pos,
                [new(pos, ManualMeaning(word) + "（隔离验证）")],
                [new(example, "隔离验证：这个例句来自当前阅读文章。"), new($"We can use {word} in a meaningful sentence.", "隔离验证：我们能在有意义的句子中使用这个单词。")], [], [])
            {
                Lemma = word,
                Spelling = new(word, word, SpellingStatus.Valid, []),
                Grammar = [new("Learn a word in its sentence", "在原句中理解并复习这个单词。")]
            };
        }
    }

    private static string ManualMeaning(string word) => word.ToLowerInvariant() switch
    {
        "resilient" => "有韧性的；能迅速恢复的",
        "apple" => "苹果；苹果树的果实",
        "steady" => "稳定的；持续的",
        _ => "从当前文章收录的示例单词"
    };
}
