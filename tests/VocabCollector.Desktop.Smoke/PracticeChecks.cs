using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    public async Task RunPracticeChecksOnlyAsync()
    {
        var run = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "practice-smoke", Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(run, "isolated-vault");
        var words = Path.Combine(vault, "words");
        var config = Path.Combine(run, "config");
        var state = Path.Combine(config, "learning");
        Directory.CreateDirectory(words);
        var source = Path.Combine(words, "introduced.md");
        const string markdown = "---\nword: introduced\nlemma: introduce\n---\n# introduced\n\n**发音**：/ˌɪntrəˈdjuːst/\n\n**释义**：\n\n> v. 介绍；使相识\n> v. 引进；首次采用\n\n## 遇见时的原句\n\nShe introduced a new method.\n\n**词形**：introduce 的过去式和过去分词。\n";
        await File.WriteAllTextAsync(source, markdown);
        using var handler = new RoamingNoNetworkHandler();
        using var client = new HttpClient(handler);
        var store = new VaultStore();
        var settings = new AppSettings(new ApiOptions("", "", ""), new VaultOptions(vault, "words", false));
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), store, TimeProvider.System), settings, store);
        var themes = new ThemeManager(app.Resources, new AppearanceSettingsStore(config));
        await themes.LoadAsync();
        var navigator = new RecordingNavigator();
        // App(false) + no sync + the default requireAccount:false constructor never starts login or reads a user profile.
        var main = new MainWindow(vm, new WindowsSettingsStore(config), navigator, new RecordingReadingService(),
            _ => throw new InvalidOperationException("Practice must not fetch source websites"), themes, learningStateDirectory: state);
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.MainWindow = main; main.Show();
        try
        {
            await SelectPageAsync(main, "RoamingTab");
            await Until(() => Descendants<LearningPage>(main).Any(page => page.HasExposure && !page.IsBusy));
            var page = Descendants<LearningPage>(main).Single();
            await SeedPracticeContextsAsync(page);
            var stateFile = Directory.GetFiles(state, "roaming.json", SearchOption.AllDirectories).Single();
            var browse = PracticeControl<RadioButton>(page, "RoamingBrowseModeButton");
            Check(browse.IsChecked == true && PracticeControl<StackPanel>(page, "NormalRoamingBody").IsVisible,
                "ordinary roaming remains the default and keeps the existing frosted meaning");
            Check(!PracticeControl<HoldToReveal>(page, "RoamingMeaningReveal").IsRevealed, "ordinary roaming starts concealed");
            var originalExposure = ReviewSnapshot(stateFile);
            page.OpenRoamingPreview();
            await Until(() => page.IsRoamingPreviewOpen);
            SelectPracticeMode(page, true);
            await Until(() => page.CurrentPracticeQuestion is not null && PracticeControl<TextBox>(page, "PracticeAnswerBox").IsEnabled);
            Check(!page.IsRoamingPreviewOpen && ReviewSnapshot(stateFile).Json == originalExposure.Json,
                "entering practice closes an already-open answer preview and reuses the existing exposure");

            await CheckPracticeResumeAsync(main, page, stateFile);
            main.Width = main.MinWidth; main.Height = main.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); main.UpdateLayout();
            var themeCases = new[] { (AppTheme.WarmNeutral, "ThemeWarmOption", "warm"), (AppTheme.Pink, "ThemeRoseOption", "pink"), (AppTheme.Mono, "ThemeMonoOption", "mono") };
            foreach (var (theme, name, file) in themeCases)
            {
                await SwitchThemeAsync(Control<RadioButton>(main, name), themes, theme);
                CheckPracticeInput(page, main);
                Snapshot(main, Path.Combine(run, "practice-minimum-" + file + ".png"));
            }

            var combinations = new HashSet<(string, PracticeKind)>();
            var spelling = 0; var meaning = 0;
            for (var index = 0; index < 4; index++)
            {
                await Until(() => page.CurrentPracticeQuestion is not null && !page.IsPracticeRevealed && PracticeControl<TextBox>(page, "PracticeAnswerBox").IsEnabled);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var question = page.CurrentPracticeQuestion!;
                combinations.Add((question.SenseId, question.Kind));
                CheckPracticeInput(page, main);
                Check(question.SenseCount == 2, "two true senses remain separate rather than combining their meanings into one quiz");
                CheckPracticeContext(question);
                if (question.Kind == PracticeKind.Spelling && spelling == 0)
                    Snapshot(main, Path.Combine(run, "practice-prereveal-spelling.png"));
                if (question.Kind == PracticeKind.Meaning && meaning == 0)
                    Snapshot(main, Path.Combine(run, "practice-prereveal-meaning.png"));
                var before = ReviewSnapshot(stateFile);
                var visits = page.ActivityDays.Sum(day => day.Count);
                var answer = PracticeControl<TextBox>(page, "PracticeAnswerBox");
                var lemma = question.Kind == PracticeKind.Spelling && spelling++ == 0;
                answer.Text = question.Kind == PracticeKind.Meaning ? "这是我用自己的话写的解释" : lemma ? "introduce" : "  INTRODUCED  ";
                // Exercise the real TextBox preview-key route as well as the rendered submit control.
                if (index == 0)
                    answer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(answer)!, Environment.TickCount, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                else Invoke(PracticeControl<Button>(page, "PracticeSubmitButton"));
                await Until(() => page.IsPracticeRevealed);
                await CheckPracticeResultAsync(page, main, question);
                PracticeControl<Button>(page, "PracticeSubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(page.CurrentPracticeQuestion?.ExposureId == question.ExposureId && ReviewSnapshot(stateFile).Json == before.Json,
                    "submission and repeated submit reveal once and never advance or write a roaming completion");
                Snapshot(main, Path.Combine(run, $"practice-result-{index}-{question.Kind}.png"));
                if (question.Kind == PracticeKind.Spelling)
                {
                    var label = PracticeControl<TextBlock>(page, "PracticeResultText").Text;
                    Check(label.Contains(lemma ? "原形正确" : "拼写正确", StringComparison.Ordinal), "exact saved forms and confirmed lemmas receive their distinct feedback");
                    if (lemma)
                    {
                        Check(!string.IsNullOrWhiteSpace(PracticeControl<TextBlock>(page, "PracticeFormText").Text), "lemma success explains the saved inflected form");
                        Invoke(PracticeControl<Button>(page, "PracticeReadNoteButton"));
                        await Until(() => page.IsRoamingPreviewOpen);
                        Check(page.RoamingReaderControl.Note?.Title == question.Word,
                            "a revealed practice result can open the current word's complete local note");
                    }
                    Invoke(PracticeControl<Button>(page, "PracticeNextButton"));
                }
                else
                {
                    Invoke(PracticeControl<Button>(page, meaning++ == 0 ? "PracticeYesButton" : "PracticeNoButton"));
                }
                await Until(() => page.CurrentPracticeQuestion?.ExposureId != question.ExposureId && page.CurrentPracticeQuestion is not null && !page.IsBusy);
                if (lemma)
                {
                    Check(!page.IsRoamingPreviewOpen && !page.IsPracticeRevealed &&
                        !PracticeControl<StackPanel>(page, "PracticeResultPanel").IsVisible &&
                        !PracticeControl<Button>(page, "PracticeReadNoteButton").IsVisible,
                        "manual next closes the previous answer-bearing preview and hides the new answer even when the same word repeats");
                }
                Check(page.ActivityDays.Sum(day => day.Count) == visits + 1, "one manual next or Chinese self-assessment draws exactly one new exposure");
                var after = ReviewSnapshot(stateFile);
                if (lemma) Check(after.Remembered == before.Remembered && after.Skipped == before.Skipped,
                    "a confirmed lemma preserves remembered and skipped weighting counters");
                else Check(after.Remembered + after.Skipped == before.Remembered + before.Skipped + 1,
                    "exact spelling and explicit Chinese self-assessment persist one ordinary review result");
            }
            Check(combinations.Count == 4 && spelling == 2 && meaning == 2, "four prepared exposures cover both kinds of both available senses");
            var skippedQuestion = page.CurrentPracticeQuestion!;
            var skippedBefore = ReviewSnapshot(stateFile).Json;
            Invoke(PracticeControl<Button>(page, "PracticeSkipButton"));
            await Until(() => page.IsPracticeRevealed);
            Check(page.CurrentPracticeQuestion == skippedQuestion && ReviewSnapshot(stateFile).Json == skippedBefore,
                "cannot-recall reveals the answer and still waits for a manual next or self-assessment");
            await CheckPracticeResultAsync(page, main, skippedQuestion);
            Check(handler.Requests == 0 && navigator.NoteOpens == 0, "practice makes no model request and opens no external note application");
            Check(!File.Exists(Path.Combine(config, "settings.json")), "practice does not create API credentials or production configuration");
            Check(await File.ReadAllTextAsync(source) == markdown && Directory.GetFiles(words).Length == 1,
                "all practice actions preserve the original Markdown and create no word notes");
            Check(errors.Errors.Count == 0, "practice has no WPF binding errors: " + string.Join("; ", errors.Errors));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            main.Close();
        }
        await CheckPracticeNoteFallbacksAsync(run);
        await File.WriteAllTextAsync(Path.Combine(run, "result.json"), JsonSerializer.Serialize(new
        { passed = true, combinations = 4, modelRequests = handler.Requests, bindingErrors = errors.Errors.Count, vault, state }));
        Console.WriteLine("Practice artifacts: " + run);
    }

    private async Task CheckPracticeNoteFallbacksAsync(string run)
    {
        var root = Path.Combine(run, "fallbacks");
        var vault = new VaultOptions(Path.Combine(root, "vault"), "words", false);
        Directory.CreateDirectory(vault.RootPath);
        var words = VaultPaths.WordRoot(vault); Directory.CreateDirectory(words);
        var path = Path.Combine(words, "banana.md");
        const string source = "# banana\n\n**释义**：\n\n> n. 香蕉\n\n## 遇见时的原句\n\nShe ate a banana after lunch.\n";
        await File.WriteAllTextAsync(path, source);
        using var page = new LearningPage(vault, Path.Combine(root, "state"));
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); icon.Freeze();
        var window = new Window { Content = page, Width = 920, Height = 700, ShowInTaskbar = false, Icon = icon };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try
        {
            window.Show(); await page.ShowRoamingAsync();
            await SeedPracticeContextsAsync(page);
            SelectPracticeMode(page, true);
            await Until(() => page.CurrentPracticeQuestion is not null && PracticeControl<TextBox>(page, "PracticeAnswerBox").IsEnabled);
            Check(page.CurrentPracticeQuestion!.Word == "banana" && page.CurrentPracticeQuestion.SenseCount == 1,
                "a note without a saved lemma still produces a local practice question");
            Check(page.CurrentPracticeQuestion.Context.Contains(page.CurrentPracticeQuestion.Kind == PracticeKind.Spelling ? "___" : "banana", StringComparison.OrdinalIgnoreCase),
                "a cached single-sense sentence remains usable and spelling masks its answer");
            CheckPracticeContext(page.CurrentPracticeQuestion);
            Check(await File.ReadAllTextAsync(path) == source, "optional morphology lookup never rewrites a note without lemma metadata");
        }
        finally { page.Dispose(); window.Close(); }

        // A fresh question/session tests genuinely absent definitions, rather than editing an already pending card.
        const string noMeaning = "# banana\n\nThis note has no parsed definition to quiz.\n";
        await File.WriteAllTextAsync(path, noMeaning);
        using var fallback = new LearningPage(vault, Path.Combine(root, "no-meaning-state"));
        var fallbackOwner = new Window { Content = fallback, Width = 920, Height = 700, ShowInTaskbar = false, Icon = icon };
        fallbackOwner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try
        {
            fallbackOwner.Show(); await fallback.ShowRoamingAsync();
            var stateFile = Directory.GetFiles(Path.Combine(root, "no-meaning-state"), "roaming.json", SearchOption.AllDirectories).Single();
            var before = ReviewSnapshot(stateFile).Json;
            SelectPracticeMode(fallback, true);
            await Until(() => fallback.Message.Contains("没有可用释义", StringComparison.Ordinal));
            Check(fallback.CurrentPracticeQuestion is null && PracticeControl<StackPanel>(fallback, "NormalRoamingBody").IsVisible &&
                !PracticeControl<StackPanel>(fallback, "PracticePanel").IsVisible && ReviewSnapshot(stateFile).Json == before,
                "missing parsed meanings fall back to normal browsing without invented questions or extra exposures");
            Check(await File.ReadAllTextAsync(path) == noMeaning, "fallback preserves the ordinary Markdown note");
            Snapshot(fallbackOwner, Path.Combine(run, "practice-no-meaning-fallback.png"));
        }
        finally { fallback.Dispose(); fallbackOwner.Close(); }
    }

    private static async Task CheckPracticeResumeAsync(MainWindow main, LearningPage page, string stateFile)
    {
        var question = page.CurrentPracticeQuestion!;
        var state = ReviewSnapshot(stateFile).Json;
        var answer = PracticeControl<TextBox>(page, "PracticeAnswerBox"); answer.Text = "unfinished answer";
        SelectPracticeMode(page, false);
        Check(PracticeControl<StackPanel>(page, "NormalRoamingBody").IsVisible && !page.IsRoamingPreviewOpen,
            "turning practice off restores normal browsing without leaving an answer-bearing preview");
        SelectPracticeMode(page, true);
        await Until(() => PracticeControl<StackPanel>(page, "PracticePanel").IsVisible);
        Check(page.CurrentPracticeQuestion == question && answer.Text == "unfinished answer" && ReviewSnapshot(stateFile).Json == state,
            "mode reversal preserves the pending answer and never draws or grades another question");
        await SelectPageAsync(main, "CaptureTab");
        var paused = page.PracticeTimeRemaining;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(page.PracticeTimeRemaining == paused, "a hidden practice surface pauses its active-view timer");
        await SelectPageAsync(main, "RoamingTab");
        Check(page.CurrentPracticeQuestion == question && answer.Text == "unfinished answer" && ReviewSnapshot(stateFile).Json == state,
            "returning to roaming resumes the same question without duplicate exposure or completion");
        answer.Text = "";
    }

    private static T PracticeControl<T>(LearningPage page, string name) where T : FrameworkElement =>
        (T)(page.FindName(name) ?? throw new InvalidOperationException("Missing practice control: " + name));
    private static void SelectPracticeMode(LearningPage page, bool practice)
    {
        var button = PracticeControl<RadioButton>(page, practice ? "RoamingPracticeModeButton" : "RoamingBrowseModeButton");
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(button).GetPattern(PatternInterface.SelectionItem)!).Select();
    }

    private static void CheckPracticeInput(LearningPage page, MainWindow main)
    {
        var question = page.CurrentPracticeQuestion!;
        CheckPracticeContext(question);
        var prompt = PracticeControl<TextBlock>(page, "PracticePromptText").Text;
        Check(prompt.Contains(question.Context, StringComparison.Ordinal) && prompt != question.Meaning,
            "every rendered question contains its actual English context rather than a Chinese-definition fallback");
        Check(!page.IsPracticeRevealed && !PracticeControl<StackPanel>(page, "PracticeResultPanel").IsVisible, "answers stay hidden until explicit submission");
        Check(!PracticeControl<TextBlock>(page, "PracticeReferenceMeaning").IsVisible &&
            !PracticeControl<TextBlock>(page, "PracticeTranslationText").IsVisible,
            "reference meanings and sentence translations remain hidden before submission");
        Check(!PracticeControl<StackPanel>(page, "NormalRoamingBody").IsVisible && !PracticeControl<Button>(page, "RoamingReadNoteButton").IsVisible &&
            !page.IsRoamingPreviewOpen, "practice conceals the ordinary card, phonetics and full-note preview entry");
        var visible = string.Join("\n", Descendants<TextBlock>(PracticeControl<StackPanel>(page, "PracticePanel")).Where(text => text.IsVisible).Select(text => text.Text));
        if (question.Kind == PracticeKind.Spelling)
        {
            Check(!visible.Contains(question.Word, StringComparison.OrdinalIgnoreCase) && !visible.Contains("/ˌɪntrəˈdjuːst/", StringComparison.Ordinal),
                "spelling prompts expose neither the target form nor its pronunciation");
            Check(PracticeControl<TextBlock>(page, "PracticeMeaningHint").Text == question.ContextTranslation,
                "the spelling hint translates the actual English situation rather than substituting a dictionary definition");
        }
        else Check(PracticeControl<TextBlock>(page, "PracticePromptText").Text.Contains(question.Word, StringComparison.OrdinalIgnoreCase) &&
            !PracticeControl<TextBlock>(page, "PracticeMeaningHint").IsVisible, "Chinese recall shows the word and hides the reference meaning");
        CheckReachable(PracticeControl<TextBox>(page, "PracticeAnswerBox"), main, "practice answer input fits at minimum window size");
        CheckReachable(PracticeControl<Button>(page, "PracticeSubmitButton"), main, "submit remains reachable at minimum size");
        CheckReachable(PracticeControl<RadioButton>(page, "RoamingBrowseModeButton"), main, "browsing mode remains reachable at minimum size");
        CheckReachable(PracticeControl<RadioButton>(page, "RoamingPracticeModeButton"), main, "practice mode remains reachable at minimum size");
        Check(!string.IsNullOrWhiteSpace(PracticeControl<TextBlock>(page, "PracticeTimerText").Text), "practice displays its active-view countdown");
    }

    private static async Task SeedPracticeContextsAsync(LearningPage page)
    {
        var cache = new PracticeContextCache(Path.Combine(page.LocalStateDirectory!, "practice-contexts.json"));
        foreach (var note in page.AllNotes.Where(note => note.IsWord))
        {
            var senses = PracticeQuestionBank.Build(note);
            if (senses.Count == 0) continue;
            var contexts = senses.Select(sense =>
            {
                var (english, chinese) = PracticeSentence(note.Title, sense.Meaning);
                return new PracticeContext(sense.Id, english, chinese);
            }).ToArray();
            await cache.SaveAsync(note.Title, senses, contexts);
        }
    }

    private static (string English, string Chinese) PracticeSentence(string word, string meaning) => word == "banana"
        ? ("She ate a banana after lunch.", "她午饭后吃了一根香蕉。")
        : meaning.Contains("介绍", StringComparison.Ordinal)
            ? ("She introduced me to her teacher.", "她把我介绍给了她的老师。")
            : ("The company introduced a new policy.", "公司推行了一项新政策。");

    private static void CheckPracticeContext(PracticeQuestion question)
    {
        var (english, chinese) = PracticeSentence(question.Word, question.Meaning);
        var expected = question.Kind == PracticeKind.Spelling ? english.Replace(question.Word, "____", StringComparison.Ordinal) : english;
        Check(question.Context == expected && question.ContextTranslation == chinese,
            "each sense uses its exact cached English sentence and natural Chinese translation");
        if (question.Kind == PracticeKind.Spelling)
            Check(question.Prompt.Contains("____", StringComparison.Ordinal) && !question.Prompt.Contains(question.Word, StringComparison.OrdinalIgnoreCase),
                "spelling is a real blank within an English sentence without leaking the answer");
        else Check(question.Prompt.Contains(question.Word, StringComparison.OrdinalIgnoreCase) && question.Prompt.Contains(english, StringComparison.Ordinal),
            "Chinese meaning recall shows the target word together with its English situation");
    }

    private static async Task CheckPracticeResultAsync(LearningPage page, MainWindow main, PracticeQuestion question)
    {
        // Visibility is synchronous; wait for the newly visible result/action controls to be arranged before checking their hit areas.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(PracticeControl<StackPanel>(page, "PracticeResultPanel").IsVisible && PracticeControl<TextBox>(page, "PracticeAnswerBox").IsReadOnly,
            "submitted answer remains visible and read-only alongside its reference");
        Check(PracticeControl<TextBlock>(page, "PracticeCorrectWord").Text == question.Word &&
            PracticeControl<TextBlock>(page, "PracticeReferenceMeaning").Text == question.Meaning,
            "the result reveals the exact saved form and the current sense rather than all meanings");
        CheckReachable(PracticeControl<TextBlock>(page, "PracticeCorrectWord"), main, "revealed saved form is fully visible at minimum size");
        CheckReachable(PracticeControl<TextBlock>(page, "PracticeReferenceMeaning"), main, "revealed reference meaning is fully visible at minimum size");
        CheckReachable(PracticeControl<Button>(page, question.Kind == PracticeKind.Spelling ? "PracticeNextButton" : "PracticeYesButton"), main,
            "manual progression remains visible at minimum size");
        if (question.Kind == PracticeKind.Meaning)
        {
            Check(PracticeControl<TextBlock>(page, "PracticeTranslationText").IsVisible &&
                PracticeControl<TextBlock>(page, "PracticeTranslationText").Text == question.ContextTranslation,
                "Chinese self-assessment reveals the current meaning together with the full sentence's natural translation");
            Check(!PracticeControl<Button>(page, "PracticeNextButton").IsVisible, "Chinese recall uses yes/no without requiring another next click");
            CheckReachable(PracticeControl<Button>(page, "PracticeNoButton"), main, "negative Chinese self-assessment remains reachable");
        }
        else Check(!PracticeControl<TextBlock>(page, "PracticeTranslationText").IsVisible, "spelling results avoid duplicating the sentence translation already shown as a hint");
    }
}
