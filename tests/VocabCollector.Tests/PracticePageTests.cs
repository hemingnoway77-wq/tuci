using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Controls;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class PracticePageTests
{
    [Fact]
    public void PracticeIsOptInAndNormalRoamingKeepsItsMaskedMeaning() => Run(fixture =>
    {
        var browse = fixture.Find<RadioButton>("RoamingBrowseModeButton");
        var practice = fixture.Find<RadioButton>("RoamingPracticeModeButton");
        Assert.Equal("翻卡", browse.Content);
        Assert.Equal("作答", practice.Content);
        Assert.True(browse.IsChecked);
        Assert.False(practice.IsChecked);
        Assert.True(practice.IsEnabled, "The user must be able to enable practice after the first roaming exposure finishes loading.");
        Assert.False(fixture.Find<StackPanel>("PracticePanel").IsVisible);
        Assert.True(fixture.Find<StackPanel>("NormalRoamingBody").IsVisible);
        var reveal = fixture.Find<HoldToReveal>("RoamingMeaningReveal");
        Assert.True(reveal.CanReveal);
        Assert.False(reveal.IsRevealed);
        Assert.Equal(1, fixture.Page.ActivityDays.Sum(day => day.Count));
    });

    [Fact]
    public void PracticeEntryHidesTheOriginalAnswerAndDoesNotDrawAnotherExposure() => Run(fixture =>
    {
        var before = File.ReadAllText(fixture.RoamingState);
        var practice = fixture.Find<RadioButton>("RoamingPracticeModeButton");
        practice.IsChecked = true;
        Until(() => fixture.Page.CurrentPracticeQuestion is not null && fixture.Find<TextBox>("PracticeAnswerBox").IsEnabled);
        Assert.False(fixture.Find<StackPanel>("NormalRoamingBody").IsVisible);
        Assert.False(fixture.Find<HoldToReveal>("RoamingMeaningReveal").IsVisible);
        Assert.False(fixture.Find<Button>("RoamingReadNoteButton").IsVisible);
        Assert.False(fixture.Find<StackPanel>("PracticeResultPanel").IsVisible);
        Assert.False(fixture.Find<TextBlock>("PracticeReferenceMeaning").IsVisible);
        Assert.False(fixture.Find<TextBlock>("PracticeTranslationText").IsVisible);
        Assert.True(fixture.Find<TextBox>("PracticeAnswerBox").IsVisible);
        Assert.NotEmpty(fixture.Find<TextBlock>("PracticeKindText").Text);
        Assert.NotEmpty(fixture.Find<TextBlock>("PracticeTimerText").Text);
        AssertRealContext(fixture);
        Assert.False(fixture.Find<Button>("PracticeSubmitButton").IsEnabled);
        var question = fixture.Page.CurrentPracticeQuestion;
        var answer = fixture.Find<TextBox>("PracticeAnswerBox");
        answer.Text = "my answer";
        Assert.True(fixture.Find<Button>("PracticeSubmitButton").IsEnabled);
        Assert.Equal(before, File.ReadAllText(fixture.RoamingState));
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        fixture.Find<RadioButton>("RoamingBrowseModeButton").IsChecked = true;
        Pump();
        Assert.True(fixture.Find<StackPanel>("NormalRoamingBody").IsVisible);
        Assert.False(fixture.Find<HoldToReveal>("RoamingMeaningReveal").IsRevealed);
        var paused = fixture.Page.PracticeTimeRemaining;
        Assert.Equal(TimeSpan.FromSeconds(23), paused);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(paused, fixture.Page.PracticeTimeRemaining);
        practice.IsChecked = true;
        Assert.Equal(question, fixture.Page.CurrentPracticeQuestion);
        Assert.Equal("my answer", answer.Text);
        Assert.Equal(paused, fixture.Page.PracticeTimeRemaining);
        var underline = fixture.Find<PageTabIndicator>("RoamingModeUnderline");
        Until(() => !underline.HasAnimatedProperties);
        var group = fixture.Find<Grid>("RoamingModeSelector");
        var center = practice.TranslatePoint(new Point(practice.ActualWidth / 2, 0), group).X;
        Assert.Equal(center, (underline.LeftEdge + underline.RightEdge) / 2, 2);
        Assert.Equal(21, underline.RightEdge - underline.LeftEdge, 2);
        Assert.Equal(before, File.ReadAllText(fixture.RoamingState));
    });

    [Fact]
    public void MissingContextsRequireOneExplicitPreparationThenCacheEverySenseWithoutAutomaticRequests() => Run(fixture =>
    {
        var before = fixture.Counts();
        fixture.Find<RadioButton>("RoamingPracticeModeButton").IsChecked = true;
        Until(() => fixture.Find<Button>("PracticePrepareButton").IsVisible && fixture.Find<Button>("PracticePrepareButton").IsEnabled);
        Assert.Null(fixture.Page.CurrentPracticeQuestion);
        Assert.Empty(fixture.Generator.Requests);
        Assert.True(fixture.Find<StackPanel>("PracticePanel").IsVisible);
        Assert.False(fixture.Find<StackPanel>("NormalRoamingBody").IsVisible);
        Assert.False(fixture.Find<TextBox>("PracticeAnswerBox").IsVisible);
        Assert.False(fixture.Find<Button>("RoamingReadNoteButton").IsVisible);
        Assert.False(fixture.Find<StackPanel>("PracticeResultPanel").IsVisible);
        Assert.False(fixture.Find<TextBlock>("PracticeReferenceMeaning").IsVisible);
        Assert.False(fixture.Find<TextBlock>("PracticeTranslationText").IsVisible);
        var explanation = fixture.Find<TextBlock>("PracticePreparationText").Text;
        Assert.Contains("当前", explanation); Assert.Contains("AI", explanation); Assert.Contains("本机", explanation);
        Assert.DoesNotContain("introduced", fixture.Find<TextBlock>("PracticePromptText").Text, StringComparison.OrdinalIgnoreCase);
        fixture.Clock.Advance(TimeSpan.FromSeconds(45)); Pump();
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Page.PracticeTimeRemaining);
        Assert.Equal("", fixture.Find<TextBlock>("PracticeTimerText").Text);
        Assert.Equal(before, fixture.Counts());
        Assert.Empty(fixture.Generator.Requests);

        fixture.Api = new ApiOptions("https://current-fixture.invalid/v1", "local-test-key", "current-fixture-model");
        fixture.Click("PracticePrepareButton");
        Until(() => fixture.Generator.Requests.Count == 1);
        var request = Assert.Single(fixture.Generator.Requests);
        Assert.Equal("introduced", request.Word);
        Assert.Equal(fixture.Api, request.Options);
        Assert.Equal(fixture.Senses.Select(sense => sense.Id).Order(), request.Senses.Select(sense => sense.Id).Order());
        Assert.Null(fixture.Page.CurrentPracticeQuestion);
        Assert.False(fixture.Find<Button>("PracticePrepareButton").IsEnabled);
        request.Result.SetResult(fixture.Contexts());
        Until(() => fixture.Page.CurrentPracticeQuestion is not null && fixture.Find<TextBox>("PracticeAnswerBox").IsEnabled);
        var cached = new PracticeContextCache(Path.Combine(fixture.Page.LocalStateDirectory!, "practice-contexts.json"))
            .ReadAsync("introduced", fixture.Senses);
        Await(cached);
        Assert.Equal(2, cached.Result.Count);
        Assert.All(fixture.Contexts(), context => Assert.Contains(context, cached.Result));
        var covered = new HashSet<(string, PracticeKind)>();
        for (var index = 0; index < 4; index++)
        {
            var question = fixture.Page.CurrentPracticeQuestion!;
            covered.Add((question.SenseId, question.Kind));
            AssertRealContext(fixture);
            fixture.Find<TextBox>("PracticeAnswerBox").Text = question.Kind == PracticeKind.Spelling ? question.Word : "按情境写的中文解释";
            fixture.Click("PracticeSubmitButton");
            fixture.Click(question.Kind == PracticeKind.Spelling ? "PracticeNextButton" : "PracticeYesButton");
            Until(() => fixture.Page.CurrentPracticeQuestion is not null && fixture.Page.CurrentPracticeQuestion.ExposureId != question.ExposureId);
        }
        Assert.Equal(4, covered.Count);
        Assert.Single(fixture.Generator.Requests);
    }, seedContexts: false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnchangedSyncOrReloadKeepsTheSingleInFlightContextRequest(bool mergeSync) => Run(fixture =>
    {
        fixture.Find<RadioButton>("RoamingPracticeModeButton").IsChecked = true;
        Until(() => fixture.Find<Button>("PracticePrepareButton").IsVisible && fixture.Find<Button>("PracticePrepareButton").IsEnabled);
        fixture.Click("PracticePrepareButton");
        Until(() => fixture.Generator.Requests.Count == 1);
        var request = Assert.Single(fixture.Generator.Requests);
        var exposure = fixture.ExposureId();
        var before = fixture.Counts();
        if (mergeSync) Await(fixture.Page.MergeLearningStateAsync([], CancellationToken.None));
        else Await(fixture.Page.ReloadAsync());
        Assert.Equal(exposure, fixture.ExposureId());
        Assert.Equal(before, fixture.Counts());
        Assert.False(request.Token.IsCancellationRequested,
            $"An unchanged {(mergeSync ? "sync merge" : "library reload")} must not cancel the explicitly requested context generation.");
        Assert.Single(fixture.Generator.Requests);
        Assert.False(fixture.Find<Button>("PracticePrepareButton").IsEnabled);
        request.Result.SetResult(fixture.Contexts());
        Until(() => fixture.Page.CurrentPracticeQuestion is not null && fixture.Find<TextBox>("PracticeAnswerBox").IsEnabled);
        Assert.Equal(exposure, fixture.Page.CurrentPracticeQuestion!.ExposureId);
        AssertRealContext(fixture);
        Assert.Single(fixture.Generator.Requests);
        Assert.Equal(before, fixture.Counts());
    }, seedContexts: false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EditingMeaningsInvalidatesReadyOrGeneratingContextsEvenForTheSameExposure(bool generating) => Run(fixture =>
    {
        fixture.Find<RadioButton>("RoamingPracticeModeButton").IsChecked = true;
        GenerationRequest? request = null;
        if (generating)
        {
            Until(() => fixture.Find<Button>("PracticePrepareButton").IsVisible && fixture.Find<Button>("PracticePrepareButton").IsEnabled);
            fixture.Click("PracticePrepareButton");
            Until(() => fixture.Generator.Requests.Count == 1);
            request = Assert.Single(fixture.Generator.Requests);
        }
        else Until(() => fixture.Page.CurrentPracticeQuestion is not null && fixture.Find<TextBox>("PracticeAnswerBox").IsEnabled);
        var exposure = fixture.ExposureId();
        var before = fixture.Counts();
        var oldContexts = fixture.Contexts();
        fixture.ExpectedMarkdown = fixture.ExpectedMarkdown.Replace("v. 介绍；使相识", "v. 正式介绍；使双方认识", StringComparison.Ordinal);
        File.WriteAllText(fixture.NotePath, fixture.ExpectedMarkdown);
        Await(fixture.Page.ReloadAsync());
        Until(() => fixture.Page.CurrentPracticeQuestion is null && fixture.Find<Button>("PracticePrepareButton").IsVisible &&
            fixture.Find<Button>("PracticePrepareButton").IsEnabled);
        Assert.Equal(exposure, fixture.ExposureId());
        Assert.Equal(before, fixture.Counts());
        Assert.True(fixture.Find<StackPanel>("PracticePanel").IsVisible);
        Assert.False(fixture.Find<StackPanel>("NormalRoamingBody").IsVisible);
        Assert.False(fixture.Find<TextBlock>("PracticeReferenceMeaning").IsVisible);
        Assert.False(fixture.Find<StackPanel>("PracticeResultPanel").IsVisible);
        if (request is not null)
        {
            Assert.True(request.Token.IsCancellationRequested, "The request for the old sense set must be cancelled after the note changes.");
            request.Result.TrySetResult(oldContexts); Pump();
            Assert.Null(fixture.Page.CurrentPracticeQuestion);
            Assert.Single(fixture.Generator.Requests);
        }
        else Assert.Empty(fixture.Generator.Requests);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Page.PracticeTimeRemaining);
        Assert.Equal("", fixture.Find<TextBlock>("PracticeTimerText").Text);
    }, seedContexts: !generating);

    [Fact]
    public void ConfirmedLemmaRevealsSavedFormAndWaitsForANeutralManualNext() => Run(fixture =>
    {
        var question = fixture.Question(PracticeKind.Spelling);
        var before = fixture.Counts();
        fixture.Find<TextBox>("PracticeAnswerBox").Text = "introduce";
        fixture.Click("PracticeSubmitButton");
        Assert.True(fixture.Page.IsPracticeRevealed);
        Assert.Contains("原形正确", fixture.Find<TextBlock>("PracticeResultText").Text);
        Assert.Equal("introduced", fixture.Find<TextBlock>("PracticeCorrectWord").Text);
        Assert.Equal(question.Meaning, fixture.Find<TextBlock>("PracticeReferenceMeaning").Text);
        Assert.Equal(before, fixture.Counts());
        fixture.Click("PracticeSubmitButton");
        Assert.Equal(question, fixture.Page.CurrentPracticeQuestion);
        fixture.Click("PracticeNextButton");
        Until(() => fixture.Page.CurrentPracticeQuestion?.ExposureId != question.ExposureId && fixture.Page.CurrentPracticeQuestion is not null);
        var after = fixture.Counts();
        Assert.Equal(before.Remembered, after.Remembered);
        Assert.Equal(before.Skipped, after.Skipped);
        Assert.Equal(before.Visits + 1, after.Visits);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChineseAnswersUseExplicitSelfAssessmentAndAdvanceWithThatSingleAction(bool matches) => Run(fixture =>
    {
        var question = fixture.Question(PracticeKind.Meaning);
        var before = fixture.Counts();
        Assert.False(fixture.Find<TextBlock>("PracticeReferenceMeaning").IsVisible);
        Assert.False(fixture.Find<TextBlock>("PracticeTranslationText").IsVisible);
        fixture.Find<TextBox>("PracticeAnswerBox").Text = "我用自己的话解释这个意思，无须逐字匹配。";
        fixture.Click("PracticeSubmitButton");
        Assert.True(fixture.Page.IsPracticeRevealed);
        Assert.Equal(before, fixture.Counts());
        Assert.Equal(question.Meaning, fixture.Find<TextBlock>("PracticeReferenceMeaning").Text);
        Assert.True(fixture.Find<TextBlock>("PracticeTranslationText").IsVisible);
        Assert.Equal(question.ContextTranslation, fixture.Find<TextBlock>("PracticeTranslationText").Text);
        Assert.False(fixture.Find<Button>("PracticeNextButton").IsVisible);
        Assert.True(fixture.Find<Button>("PracticeYesButton").IsVisible);
        Assert.True(fixture.Find<Button>("PracticeNoButton").IsVisible);
        fixture.Click(matches ? "PracticeYesButton" : "PracticeNoButton");
        Until(() => fixture.Page.CurrentPracticeQuestion?.ExposureId != question.ExposureId && fixture.Page.CurrentPracticeQuestion is not null);
        var after = fixture.Counts();
        Assert.Equal(before.Visits + 1, after.Visits);
        Assert.Equal(before.Remembered + (matches ? 1 : 0), after.Remembered);
        Assert.Equal(before.Skipped + (matches ? 0 : 1), after.Skipped);
    });

    [Theory]
    [InlineData(PracticeKind.Spelling)]
    [InlineData(PracticeKind.Meaning)]
    public void ActiveTimePausesOffSurfaceAndTimeoutOnlyRevealsThePendingQuestion(PracticeKind kind) => Run(fixture =>
    {
        var question = fixture.Question(kind);
        var before = fixture.Counts();
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        Assert.InRange(fixture.Page.PracticeTimeRemaining.TotalSeconds, 22.9, 23.1);
        fixture.Page.ShowLibrary();
        var paused = fixture.Page.PracticeTimeRemaining;
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(paused, fixture.Page.PracticeTimeRemaining);
        Assert.False(fixture.Page.IsPracticeRevealed);
        Await(fixture.Page.ShowRoamingAsync());
        Assert.Equal(question, fixture.Page.CurrentPracticeQuestion);
        fixture.Clock.Advance(TimeSpan.FromSeconds(24));
        Until(() => fixture.Page.IsPracticeRevealed);
        Assert.Equal(TimeSpan.Zero, fixture.Page.PracticeTimeRemaining);
        Assert.Equal(before, fixture.Counts());
        Assert.Equal(question, fixture.Page.CurrentPracticeQuestion);
        Assert.Equal(question.Meaning, fixture.Find<TextBlock>("PracticeReferenceMeaning").Text);
        Assert.True(fixture.Find<Button>(kind == PracticeKind.Spelling ? "PracticeNextButton" : "PracticeYesButton").IsVisible);
        if (kind == PracticeKind.Meaning)
        {
            fixture.Click("PracticeNoButton");
            Until(() => fixture.Page.CurrentPracticeQuestion?.ExposureId != question.ExposureId);
            Assert.Equal(before.Skipped + 1, fixture.Counts().Skipped);
        }
    });

    [Fact]
    public void WindowDeactivationPausesTheSameQuestionUntilItsOwnerIsActiveAgain() => Run(fixture =>
    {
        var question = fixture.Question(PracticeKind.Spelling);
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        var before = fixture.Counts();
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); icon.Freeze();
        var other = new Window { Owner = fixture.Owner, Width = 240, Height = 120, ShowInTaskbar = false, Icon = icon };
        try
        {
            other.Show(); other.Activate(); Pump();
            Assert.True(other.IsActive && !fixture.Owner.IsActive, "The isolated auxiliary window must own activation for the pause check.");
            var paused = fixture.Page.PracticeTimeRemaining;
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(paused, fixture.Page.PracticeTimeRemaining);
            Assert.False(fixture.Page.IsPracticeRevealed);
        }
        finally { other.Close(); }
        fixture.Owner.Activate(); Pump();
        Assert.True(fixture.Owner.IsActive, "The practice owner must regain activation before resuming its clock.");
        var resumed = fixture.Page.PracticeTimeRemaining;
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(resumed - TimeSpan.FromSeconds(2), fixture.Page.PracticeTimeRemaining);
        Assert.Equal(question, fixture.Page.CurrentPracticeQuestion);
        Assert.Equal(before, fixture.Counts());
    });

    [Fact]
    public void FailedGradeWriteKeepsTheSubmittedAnswerAndRetryCompletesExactlyOnce() => Run(fixture =>
    {
        var question = fixture.Question(PracticeKind.Spelling);
        fixture.Find<TextBox>("PracticeAnswerBox").Text = question.Word;
        fixture.Click("PracticeSubmitButton");
        Assert.True(fixture.Page.IsPracticeRevealed);
        var before = fixture.Counts();
        var practicePath = Directory.GetFiles(fixture.Root, "practice.json", SearchOption.AllDirectories).Single();
        var originalState = File.ReadAllText(practicePath);
        // Deny replacement of this fixture's grade file, then release it for a real retry.
        using (var locked = new FileStream(practicePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fixture.Click("PracticeNextButton");
            Until(() => fixture.Page.Message.Contains("练习记录暂未保存", StringComparison.Ordinal));
            Assert.Equal(question, fixture.Page.CurrentPracticeQuestion);
            Assert.True(fixture.Page.IsPracticeRevealed);
            Assert.Equal(question.Word, fixture.Find<TextBox>("PracticeAnswerBox").Text);
            Assert.Equal(originalState, File.ReadAllText(practicePath));
            Assert.Equal(before, fixture.Counts());
        }
        fixture.Click("PracticeNextButton");
        Until(() => fixture.Page.CurrentPracticeQuestion?.ExposureId != question.ExposureId && fixture.Page.CurrentPracticeQuestion is not null);
        var after = fixture.Counts();
        Assert.Equal(before.Visits + 1, after.Visits);
        Assert.Equal(before.Remembered + 1, after.Remembered);
        Assert.Equal(before.Skipped, after.Skipped);
    });

    [Fact]
    public void MissingParsedMeaningFallsBackToNormalRoamingWithoutInventingAQuestion() => Run(fixture =>
    {
        File.WriteAllText(fixture.NotePath, "# introduced\n\nAn ordinary note without parsed definitions.\n");
        fixture.ExpectedMarkdown = File.ReadAllText(fixture.NotePath);
        Await(fixture.Page.ReloadAsync());
        var before = fixture.Counts();
        fixture.Find<RadioButton>("RoamingPracticeModeButton").IsChecked = true;
        Until(() => fixture.Page.Message.Contains("没有可用释义", StringComparison.Ordinal));
        Assert.Null(fixture.Page.CurrentPracticeQuestion);
        Assert.True(fixture.Find<StackPanel>("NormalRoamingBody").IsVisible);
        Assert.False(fixture.Find<StackPanel>("PracticePanel").IsVisible);
        Assert.Equal(before, fixture.Counts());
    });

    private static void AssertRealContext(Fixture fixture)
    {
        var question = Assert.IsType<PracticeQuestion>(fixture.Page.CurrentPracticeQuestion);
        var context = fixture.Contexts().Single(item => item.SenseId == question.SenseId);
        var expectedEnglish = question.Kind == PracticeKind.Spelling ? context.English.Replace("introduced", "____", StringComparison.Ordinal) : context.English;
        Assert.Equal(expectedEnglish, question.Context);
        Assert.Equal(context.Chinese, question.ContextTranslation);
        var prompt = fixture.Find<TextBlock>("PracticePromptText").Text;
        Assert.Contains(expectedEnglish, prompt);
        Assert.NotEqual(question.Meaning, prompt);
        if (question.Kind == PracticeKind.Spelling)
        {
            Assert.Contains("____", prompt);
            Assert.DoesNotContain("introduced", prompt, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(context.Chinese, fixture.Find<TextBlock>("PracticeMeaningHint").Text);
        }
        else
        {
            Assert.Contains(question.Word, prompt);
            Assert.False(fixture.Find<TextBlock>("PracticeMeaningHint").IsVisible);
        }
    }

    private static void Run(Action<Fixture> test, bool seedContexts = true) => MarkdownPreviewTests.OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var fixture = new Fixture(seedContexts);
        test(fixture);
        if (seedContexts) Assert.Empty(fixture.Generator.Requests);
    });
    private static void Pump() => MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(20));
    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) Pump();
        Assert.True(condition(), "Practice page did not reach the expected UI state.");
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "practice-page-" + Guid.NewGuid().ToString("N"));
        public LearningPage Page { get; }
        public PracticeClock Clock { get; } = new();
        public Window Owner => _owner;
        public ControlledGenerator Generator { get; } = new();
        public ApiOptions Api { get; set; } = new("https://fixture.invalid/v1", "local-test-key", "fixture-model");
        public IReadOnlyList<PracticeSense> Senses => PracticeQuestionBank.Build(Page.AllNotes.Single(note => note.Title == "introduced"));
        public string RoamingState => Directory.GetFiles(Root, "roaming.json", SearchOption.AllDirectories).Single();
        private readonly Window _owner;
        public string NotePath { get; }
        public string ExpectedMarkdown { get; set; }
        public Fixture(bool seedContexts = true)
        {
            var vault = new VaultOptions(Path.Combine(Root, "vault"), "words", false);
            Directory.CreateDirectory(vault.RootPath);
            var words = VaultPaths.WordRoot(vault); Directory.CreateDirectory(words);
            NotePath = Path.Combine(words, "introduced.md");
            ExpectedMarkdown = "---\nword: introduced\nlemma: introduce\n---\n# introduced\n\n**发音**：/ˌɪntrəˈdjuːst/\n\n**释义**：\n\n> v. 介绍；使相识\n> v. 引进；首次采用\n\n## 遇见时的原句\n\nShe introduced a new method.\n\n**词形**：introduce 的过去式和过去分词。\n";
            File.WriteAllText(NotePath, ExpectedMarkdown);
            Page = new LearningPage(vault, Path.Combine(Root, "state"), Clock, Generator, () => Api);
            // Each test owns its STA icon; do not use the shared Application's decoder.
            var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); icon.Freeze();
            _owner = new Window { Content = Page, Width = 920, Height = 700, ShowInTaskbar = false, Icon = icon };
            _owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
            _owner.Show(); Pump(); _owner.Activate();
            Await(Page.ShowRoamingAsync()); Pump();
            if (seedContexts)
                Await(new PracticeContextCache(Path.Combine(Page.LocalStateDirectory!, "practice-contexts.json"))
                    .SaveAsync("introduced", Senses, Contexts()));
        }
        public IReadOnlyList<PracticeContext> Contexts() => Senses.Select(sense => sense.Meaning.Contains("介绍", StringComparison.Ordinal)
            ? new PracticeContext(sense.Id, "She introduced me to her teacher.", "她把我介绍给了她的老师。")
            : new PracticeContext(sense.Id, "The company introduced a new policy.", "公司推行了一项新政策。")).ToArray();
        public T Find<T>(string name) where T : class => Assert.IsType<T>(Page.FindName(name));
        public void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        public PracticeQuestion Question(PracticeKind kind)
        {
            Find<RadioButton>("RoamingPracticeModeButton").IsChecked = true;
            for (var index = 0; index < 5; index++)
            {
                Until(() => Page.CurrentPracticeQuestion is not null && Find<TextBox>("PracticeAnswerBox").IsEnabled);
                var question = Page.CurrentPracticeQuestion!;
                if (question.Kind == kind) { AssertRealContext(this); return question; }
                Click("PracticeSkipButton");
                Click(question.Kind == PracticeKind.Meaning ? "PracticeNoButton" : "PracticeNextButton");
                Until(() => Page.CurrentPracticeQuestion?.ExposureId != question.ExposureId);
            }
            throw new InvalidOperationException("Practice deck did not interleave both question kinds.");
        }
        public (int Remembered, int Skipped, int Visits) Counts()
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RoamingState));
            var records = json.RootElement.GetProperty("Records").EnumerateObject().Select(item => item.Value).ToArray();
            return (records.Sum(record => record.GetProperty("Remembered").GetInt32()),
                records.Sum(record => record.GetProperty("Skipped").GetInt32()), Page.ActivityDays.Sum(day => day.Count));
        }
        public Guid ExposureId()
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RoamingState));
            return json.RootElement.GetProperty("CurrentExposure").GetGuid();
        }
        public void Dispose()
        {
            Page.Dispose(); _owner.Close();
            Assert.Equal(ExpectedMarkdown, File.ReadAllText(NotePath));
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected temporary fixture path.");
            Directory.Delete(Root, true);
        }
    }

    private sealed class ControlledGenerator : IPracticeContextGenerator
    {
        public List<GenerationRequest> Requests { get; } = [];
        public Task<IReadOnlyList<PracticeContext>> GenerateAsync(string word, IReadOnlyList<PracticeSense> senses, ApiOptions options, CancellationToken ct)
        {
            var request = new GenerationRequest(word, senses.ToArray(), options, ct);
            Requests.Add(request); return request.Result.Task.WaitAsync(ct);
        }
    }
    private sealed record GenerationRequest(string Word, IReadOnlyList<PracticeSense> Senses, ApiOptions Options, CancellationToken Token)
    {
        public TaskCompletionSource<IReadOnlyList<PracticeContext>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PracticeClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 10, 4, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan amount) => _ticks += amount.Ticks;
    }
}
