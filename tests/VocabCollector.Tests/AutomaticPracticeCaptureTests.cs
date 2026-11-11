using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Library;
using VocabCollector.Core.Reading;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class AutomaticPracticeCaptureTests
{
    [Theory]
    [InlineData("words")]
    [InlineData("词根")]
    public void SavedCaptureReturnsBeforePreparationAndCachesEverySenseForLaterPractice(string outputSubfolder) => Run(fixture =>
    {
        Assert.Empty(fixture.Generator.Requests);
        AssertNoPracticeExposure(fixture);
        fixture.Capture("introduced");
        var request = fixture.Request();
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.False(fixture.Window.CapturedPracticePreparation.IsCompleted);
        Assert.Contains("已保存", fixture.ViewModel.Message);
        var saved = Assert.Single(fixture.ViewModel.Notes);
        Assert.True(File.Exists(saved.AbsolutePath));
        Assert.Equal("introduced", request.Word);
        Assert.Equal(fixture.Settings.Api, request.Options);
        var expected = PracticeQuestionBank.Build(fixture.Note("introduced"));
        Assert.Equal(2, expected.Count);
        Assert.Equal(expected.Select(sense => sense.Id).Order(), request.Senses.Select(sense => sense.Id).Order());
        AssertNoPracticeExposure(fixture);

        request.Complete(); Await(fixture.Window.CapturedPracticePreparation);
        var read = new PracticeContextCache(fixture.CachePath).ReadAsync("introduced", expected); Await(read);
        Assert.Equal(2, read.Result.Count);
        Assert.All(read.Result, context => Assert.Contains(context.SenseId, expected.Select(sense => sense.Id)));
        Assert.DoesNotContain(fixture.Settings.Api.ApiKey, File.ReadAllText(fixture.CachePath));
        AssertNoPracticeExposure(fixture);

        fixture.Find<RadioButton>("RoamingTab").IsChecked = true;
        Until(() => Field<LearningPage?>(fixture.Window, "_learningPage") is { HasExposure: true, IsBusy: false });
        var page = Field<LearningPage>(fixture.Window, "_learningPage");
        Assert.Equal(fixture.Metadata, page.LocalStateDirectory);
        Assert.IsType<RadioButton>(page.FindName("RoamingPracticeModeButton")).IsChecked = true;
        Until(() => page.CurrentPracticeQuestion is not null);
        var question = Assert.IsType<PracticeQuestion>(page.CurrentPracticeQuestion);
        Assert.NotEmpty(question.Context);
        Assert.NotEmpty(question.ContextTranslation);
        Assert.Single(fixture.Generator.Requests);
    }, outputSubfolder);

    [Fact]
    public void CorrectedSavedWordAndItsActualNoteDrivePreparationInsteadOfTheNextDraft() => Run(fixture =>
    {
        fixture.Captures.CorrectedWord = "introduced";
        fixture.Capture("introdced");
        fixture.ViewModel.Word = "adopted";
        var request = fixture.Request();
        Assert.Equal("introduced", request.Word);
        Assert.All(request.Senses, sense => Assert.Equal("introduced", sense.Word));
        Assert.Contains(request.Senses, sense => sense.Meaning.Contains("介绍", StringComparison.Ordinal));
        Assert.Contains(request.Senses, sense => sense.Meaning.Contains("引进", StringComparison.Ordinal));
        request.Complete(); Await(fixture.Window.CapturedPracticePreparation);
        Assert.Equal("adopted", fixture.ViewModel.Word);
        var cached = new PracticeContextCache(fixture.CachePath).ReadAsync("introduced", request.Senses); Await(cached);
        Assert.Equal(2, cached.Result.Count);
        AssertNoPracticeExposure(fixture);
    });

    [Theory]
    [InlineData(CaptureStatus.AlreadyExists)]
    [InlineData(CaptureStatus.InvalidInput)]
    [InlineData(CaptureStatus.Failed)]
    [InlineData(CaptureStatus.Cancelled)]
    public void UncommittedOrDuplicateCapturesNeverSpendAnotherContextRequest(CaptureStatus status) => Run(fixture =>
    {
        fixture.Captures.Status = status;
        fixture.Capture("introduced");
        Await(fixture.Window.CapturedPracticePreparation);
        Assert.Empty(fixture.Generator.Requests);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.False(fixture.ViewModel.IsBusy);
        AssertNoPracticeExposure(fixture);
    });

    [Fact]
    public void FailedPreparationKeepsTheSuccessfulNoteAndAllowsAnotherCapture() => Run(fixture =>
    {
        fixture.Capture("introduced");
        var request = fixture.Request();
        var saved = Assert.Single(fixture.ViewModel.Notes);
        var markdown = File.ReadAllText(saved.AbsolutePath);
        request.Result.SetException(new AnalysisException("Isolated provider failure."));
        Await(fixture.Window.CapturedPracticePreparation);
        Assert.Contains("已保存", fixture.ViewModel.Message);
        Assert.Contains("Isolated provider failure.", fixture.ViewModel.Message);
        Assert.Contains("可在漫游中重新准备", fixture.ViewModel.Message);
        Assert.DoesNotContain("处理失败", fixture.ViewModel.Message);
        Assert.Equal(markdown, fixture.ViewModel.Markdown);
        Assert.Equal(markdown, File.ReadAllText(saved.AbsolutePath));
        Assert.Same(saved, fixture.ViewModel.SelectedNote);
        Assert.True(fixture.ViewModel.GenerateCommand.CanExecute(null));
        Assert.False(File.Exists(fixture.CachePath));
        AssertNoPracticeExposure(fixture);
    });

    [Fact]
    public void ClosingCancelsPreparationAndAnIgnoringProvidersLateResultCannotWriteTheCache() => Run(fixture =>
    {
        fixture.Capture("introduced");
        var request = fixture.Request();
        var saved = Assert.Single(fixture.ViewModel.Notes);
        fixture.Window.Close();
        Assert.True(request.Token.IsCancellationRequested);
        request.Complete(); Await(fixture.Window.CapturedPracticePreparation);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.True(File.Exists(saved.AbsolutePath));
    });

    [Fact]
    public void ConsecutiveCapturesPrepareInOrderAndRetainBothWordsWithoutReplacingANewerMessage() => Run(fixture =>
    {
        fixture.Capture("introduced");
        var first = fixture.Request();
        fixture.Capture("adopted");
        var allPreparation = fixture.Window.CapturedPracticePreparation;
        Assert.Single(fixture.Generator.Requests);
        Assert.False(fixture.ViewModel.IsBusy);
        first.Complete();
        Until(() => fixture.Generator.Requests.Count == 2);
        var second = fixture.Generator.Requests[1];
        Assert.Equal("adopted", second.Word);
        fixture.ViewModel.Message = "正在编辑下一篇收录的原句。";
        second.Complete(); Await(allPreparation);
        Assert.Equal("正在编辑下一篇收录的原句。", fixture.ViewModel.Message);
        foreach (var request in fixture.Generator.Requests)
        {
            var cached = new PracticeContextCache(fixture.CachePath).ReadAsync(request.Word, request.Senses); Await(cached);
            Assert.Equal(request.Senses.Count, cached.Result.Count);
        }
        AssertNoPracticeExposure(fixture);
    });

    [Fact]
    public void ReadingCapturePaneUsesTheSameSavedCaptureHookWithoutLeavingTheArticle() => Run(fixture =>
    {
        fixture.Find<RadioButton>("ReadingTab").IsChecked = true;
        Until(() => Field<ReadingPage?>(fixture.Window, "_readingPage") is not null);
        var reading = Field<ReadingPage>(fixture.Window, "_readingPage");
        Await(reading.OpenArticleAsync(Fixture.Article));
        reading.OpenCapturePreview(fixture.ViewModel, new("introduced", "She introduced a useful method to her team."));
        Assert.IsType<Button>(reading.FindName("ReadingCaptureGenerateButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var request = fixture.Request();
        Assert.False(fixture.ViewModel.IsBusy);
        Assert.Single(fixture.ViewModel.Notes);
        Assert.Equal("She introduced a useful method to her team.", fixture.Captures.LastRequest?.Context);
        Assert.True(reading.IsArticleOpen);
        Assert.Equal(Fixture.Article, reading.CurrentArticle);
        request.Complete(); Await(fixture.Window.CapturedPracticePreparation);
        Assert.True(reading.IsArticleOpen);
        Assert.Equal(Fixture.Article, reading.CurrentArticle);
        AssertNoPracticeExposure(fixture);
    });

    private static void AssertNoPracticeExposure(Fixture fixture)
    {
        Assert.False(File.Exists(Path.Combine(fixture.Metadata, "practice.json")));
        var roaming = Path.Combine(fixture.Metadata, "roaming.json");
        if (!File.Exists(roaming)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(roaming));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("CurrentExposure").ValueKind);
        if (json.RootElement.TryGetProperty("Activity", out var activity) && activity.ValueKind == JsonValueKind.Object)
            Assert.Empty(activity.EnumerateObject());
    }

    // Reuse the suite's one Application and nonparallel UI dispatcher.
    private static void Run(Action<Fixture> test, string outputSubfolder = "words") => ((Lazy<Dispatcher>)typeof(ReadingPageTests)
        .GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(() =>
        { using var fixture = new Fixture(outputSubfolder); test(fixture); });
    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Pump() => MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(15));
    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) Pump();
        Assert.True(condition(), "Automatic practice preparation did not reach its expected state.");
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    private sealed class Fixture : IDisposable
    {
        public static readonly ReadingArticle Article = new("An isolated article", "A summary", "https://www.sciencedaily.com/releases/automatic-practice-fixture.htm", "ScienceDaily", ReadingCategory.General);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "automatic-practice-capture-" + Guid.NewGuid().ToString("N"));
        public string StateRoot => Path.Combine(Root, "state");
        public AppSettings Settings { get; }
        public string Metadata => LearningPage.ResolveLocalStateDirectory(Settings.Vault, StateRoot);
        public string CachePath => Path.Combine(Metadata, "practice-contexts.json");
        public FileCapture Captures { get; } = new();
        public ControlledGenerator Generator { get; } = new();
        public MainViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public Fixture(string outputSubfolder = "words")
        {
            var vault = new VaultOptions(Path.Combine(Root, "vault"), outputSubfolder, false);
            Directory.CreateDirectory(vault.RootPath);
            Settings = new(new("https://automatic-fixture.invalid/v1", "local-automatic-fixture-key", "automatic-fixture-model"), vault);
            ViewModel = new MainViewModel(Captures, Settings);
            Window = new MainWindow(ViewModel, new MemoryStore(Settings), new WindowsNavigator(),
                readingService: new Daily(), articleContentService: new Content(), wordTranslationService: new NoTranslation(),
                learningStateDirectory: StateRoot, settingsDirectory: Path.Combine(Root, "settings"), automaticPracticeGenerator: Generator)
            { ShowInTaskbar = false, Width = 1000, Height = 760 };
            // This fixture owns its STA icon instead of reusing the shared
            // Application's decoder from another WPF test thread.
            var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            icon.Freeze(); Window.Icon = icon;
            Window.Show(); Pump();
        }
        public T Find<T>(string name) where T : class => Assert.IsType<T>(Window.FindName(name));
        public void Capture(string word) { ViewModel.Word = word; Await(ViewModel.GenerateAsync()); }
        public GenerationRequest Request()
        { Until(() => Generator.Requests.Count > 0); return Generator.Requests[^1]; }
        public LibraryNote Note(string word)
        {
            var path = Path.Combine(VaultPaths.WordRoot(Settings.Vault), word + ".md");
            return new("captured-" + word, word + ".md", path, File.ReadAllText(path), DateTimeOffset.UtcNow);
        }
        public void Dispose()
        {
            ViewModel.CancelCommand.Execute(null);
            Until(() => !ViewModel.IsBusy);
            Window.Close();
            foreach (var request in Generator.Requests) request.Result.TrySetCanceled();
            Await(Window.CapturedPracticePreparation); Pump();
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected isolated test path.");
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class FileCapture : ICaptureService
    {
        public CaptureStatus Status { get; set; } = CaptureStatus.Saved;
        public string? CorrectedWord { get; set; }
        public CaptureRequest? LastRequest { get; private set; }
        public async Task<CaptureResult> CaptureAsync(CaptureRequest request, AppSettings settings, IProgress<CaptureStage>? progress, CancellationToken ct)
        {
            LastRequest = request;
            var word = CorrectedWord ?? WordInput.Normalize(request.Word);
            if (Status != CaptureStatus.Saved) return new(Status, word, "", [], "本次没有新增笔记。");
            var root = VaultPaths.WordRoot(settings.Vault); Directory.CreateDirectory(root);
            var path = Path.Combine(root, word + ".md");
            var markdown = word == "introduced"
                ? "---\nlemma: introduce\n---\n# introduced\n\n**释义**：\n\n> v. 介绍；使相识\n> v. 引进；首次采用\n"
                : "---\nlemma: adopt\n---\n# adopted\n\n**释义**：\n\n> v. 采用；接受\n> v. 收养\n";
            await File.WriteAllTextAsync(path, markdown, ct);
            return new(CaptureStatus.Saved, word, markdown,
                [new(word, path, settings.Vault.OutputSubfolder.Replace('\\', '/') + "/" + word + ".md")], "已保存。")
            { CorrectedFrom = word == request.Word ? "" : request.Word };
        }
    }
    private sealed class ControlledGenerator : IPracticeContextGenerator
    {
        public List<GenerationRequest> Requests { get; } = [];
        public Task<IReadOnlyList<PracticeContext>> GenerateAsync(string word, IReadOnlyList<PracticeSense> senses, ApiOptions options, CancellationToken ct)
        {
            var request = new GenerationRequest(word, senses.ToArray(), options, ct); Requests.Add(request);
            // Deliberately ignore cancellation so the caller must reject late results.
            return request.Result.Task;
        }
    }
    private sealed record GenerationRequest(string Word, IReadOnlyList<PracticeSense> Senses, ApiOptions Options, CancellationToken Token)
    {
        public TaskCompletionSource<IReadOnlyList<PracticeContext>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete() => Result.TrySetResult(Senses.Select(sense => new PracticeContext(sense.Id,
            Word == "introduced" ? sense.Meaning.Contains("介绍", StringComparison.Ordinal)
                ? "The teacher introduced her new student today." : "Our company introduced a new policy yesterday."
                : sense.Meaning.Contains("收养", StringComparison.Ordinal)
                    ? "The family adopted a young child last year." : "Our team adopted a useful method today.",
            Word == "introduced" ? sense.Meaning.Contains("介绍", StringComparison.Ordinal)
                ? "老师今天介绍了她的新学生。" : "我们公司昨天推行了一项新政策。"
                : sense.Meaning.Contains("收养", StringComparison.Ordinal) ? "这家人去年收养了一个年幼的孩子。" : "我们团队今天采用了一个有用的方法。"
        )).ToArray());
    }
    private sealed class MemoryStore(AppSettings settings) : ISettingsStore
    {
        public Task<SettingsLoadResult> LoadAsync(CancellationToken ct) => Task.FromResult(new SettingsLoadResult(settings, ""));
        public Task SaveAsync(AppSettings value, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Daily : IDailyReadingService
    {
        public Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyReadingResult(DateOnly.FromDateTime(DateTime.Today), [Fixture.Article], false, false, "Isolated article directory."));
    }
    private sealed class Content : IArticleContentService
    {
        public Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new ArticleContent(article, ["She introduced a useful method to her team."], DateTimeOffset.UnixEpoch));
    }
    private sealed class NoTranslation : IWordTranslationService
    {
        public Task<WordTranslation> TranslateAsync(string word, CancellationToken ct) =>
            throw new InvalidOperationException("Automatic practice capture must not request word translation.");
    }
}
