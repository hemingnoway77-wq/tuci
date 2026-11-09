using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Controls;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    // The baseline uses precisely the same workload, recording the old motion and
    // layout behavior without treating those known regressions as test failures.
    public async Task RunNavigationSwitchChecksAsync(bool baseline = false)
    {
        var run = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "navigation-switch", Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(run, "vault"); var words = Path.Combine(vault, "words");
        Directory.CreateDirectory(words);
        for (var index = 0; index < 240; index++)
        {
            var word = "sample" + (char)('a' + index / 26) + (char)('a' + index % 26);
            await File.WriteAllTextAsync(Path.Combine(words, word + ".md"), "# " + word + "\n\n**释义**：\n\n> n. 本地切换验收词条\n\n" +
                string.Join("\n\n", Enumerable.Range(1, 45).Select(number => $"Paragraph {number}: A local note remains readable while quickly switching between application modules.")));
        }
        using var handler = new RoamingNoNetworkHandler(); using var client = new System.Net.Http.HttpClient(handler);
        var settings = new AppSettings(new ApiOptions("", "", ""), new VaultOptions(vault, "words", false));
        var store = new VaultStore();
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), store, TimeProvider.System), settings, store);
        var feeds = new RecordingReadingService(); var content = new RecordingArticleContentService();
        var translations = new RecordingWordTranslationService();
        var themes = new ThemeManager(app.Resources, new AppearanceSettingsStore(Path.Combine(run, "appearance"))); await themes.LoadAsync();
        var main = new MainWindow(vm, new WindowsSettingsStore(Path.Combine(run, "config")), new RecordingNavigator(), feeds,
            _ => throw new InvalidOperationException("Navigation smoke must not open a remote page."), themes,
            learningStateDirectory: Path.Combine(run, "learning"), articleContentService: content,
            wordTranslationService: translations, prepareLocalData: () => Task.CompletedTask);
        var bindingErrors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.MainWindow = main; main.Show();
        try
        {
            await main.PrepareForUseAsync();
            var learning = Learning(main);
            await SelectPageAsync(main, "LibraryTab");
            await SelectLibraryNoteAsync(learning, learning.AllNotes[0]);
            var notePreview = ReadingPreview(learning);
            notePreview.ScrollToVerticalOffset(180);
            await SelectPageAsync(main, "RoamingTab"); await Until(() => learning.HasExposure && !learning.IsBusy);
            var exposure = learning.RoamingTitle; var encounters = learning.ActivityDays.Sum(day => day.Count);
            await SelectPageAsync(main, "ReadingTab");
            var reading = Control<Grid>(main, "PageHost").Children.OfType<ReadingPage>().Single();
            await Until(() => reading.ViewModel.Articles.Count == feeds.Articles.Count && !reading.ViewModel.IsBusy);
            await reading.OpenArticleAsync(feeds.Articles[0]);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); await Task.Delay(300);
            var articleReader = ReadingControl<RichTextBox>(reading, "ArticleBodyReader");
            articleReader.ScrollToVerticalOffset(180);
            await SelectPageAsync(main, "CaptureTab");
            await Dispatcher.Yield(DispatcherPriority.ContextIdle); await Task.Delay(300);
            var tree = LearningControl<TreeView>(learning, "BrowserFilesTree");
            var rows = Descendants<TreeViewItem>(tree).ToArray();
            Check(rows.Length == 240 && articleReader.Document.Blocks.Count == 45, "navigation fixture has real tree rows and two fully rendered long documents");
            var noteOffset = notePreview.VerticalOffset; var articleOffset = articleReader.VerticalOffset;
            Check(noteOffset > 0 && articleOffset > 0, "navigation fixture exercises retained local-note and article scroll positions");
            var surfaces = Control<Grid>(main, "PageHost").Children.OfType<FrameworkElement>().ToArray();
            var retained = new FrameworkElement[] { tree, notePreview, articleReader };
            var results = new List<NavigationBatch>();
            var tabs = new[] { "CaptureTab", "LibraryTab", "RoamingTab", "ReadingTab" };
            for (var batch = 0; batch < 4; batch++)
            {
                // Four bursts finish on four different destinations. Delays allow
                // real input/render work to interleave instead of only changing DPs.
                var result = new NavigationBatch { FinalTab = tabs[batch] };
                using (var responsiveness = new NavigationResponsivenessProbe())
                {
                    for (var step = 0; step < 40; step++)
                    {
                        var tab = tabs[(step + batch + 1) % tabs.Length];
                        var watch = Stopwatch.StartNew();
                        Control<RadioButton>(main, tab).IsChecked = true;
                        result.HandlerMilliseconds.Add(watch.Elapsed.TotalMilliseconds);
                        result.NonImmediateContentSamples += surfaces.Count(surface => surface.IsVisible && !NavigationContentIsImmediate(surface));
                        result.TreeMeasureInvalidSamples += tree.IsMeasureValid ? 0 : 1;
                        result.TreeArrangeInvalidSamples += tree.IsArrangeValid ? 0 : 1;
                        result.RetainedLayoutInvalidSamples += retained.Count(element => !element.IsMeasureValid || !element.IsArrangeValid);
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        result.SwitchToIdleMilliseconds.Add(watch.Elapsed.TotalMilliseconds);
                        await Task.Delay(12);
                    }
                    await Task.Delay(350);
                    result.HeartbeatMilliseconds = responsiveness.Heartbeats.ToArray();
                    result.RenderIntervalMilliseconds = responsiveness.RenderIntervals.ToArray();
                }
                CheckNavigationDestination(main, learning, reading, result.FinalTab);
                CheckNavigationClocksSettled(main, surfaces);
                Check(Descendants<TreeViewItem>(tree).SequenceEqual(rows), "rapid navigation reuses the original generated file rows");
                Check(learning.RoamingTitle == exposure && learning.ActivityDays.Sum(day => day.Count) == encounters,
                    "module navigation preserves the current roaming card without recording an additional encounter");
                results.Add(result);
            }
            // Measure a genuinely idle shell too; the sampling handler itself
            // subscribes to Rendering, so its frame count is diagnostic, not a
            // claim that the application has an idle render loop.
            double[] idleHeartbeats, idleFrames;
            using (var idle = new NavigationResponsivenessProbe())
            {
                await Task.Delay(400);
                idleHeartbeats = idle.Heartbeats.ToArray(); idleFrames = idle.RenderIntervals.ToArray();
            }
            var staleRoamingWasIgnored = await CheckDelayedRoamingSelectionAsync(main, learning);
            Check(bindingErrors.Errors.Count == 0, "rapid module navigation has no binding errors: " + string.Join(" | ", bindingErrors.Errors));
            Check(handler.Requests == 0 && translations.Calls == 0 && feeds.Calls == 1 && content.Calls == 1,
                "rapid navigation uses only the isolated loaded data and never reloads feeds, articles, translation or AI");
            await SelectPageAsync(main, "LibraryTab"); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Math.Abs(notePreview.VerticalOffset - noteOffset) <= 3, "rapid module switches retain the local-note reading position");
            await SelectPageAsync(main, "ReadingTab"); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Math.Abs(articleReader.VerticalOffset - articleOffset) <= 3 && reading.CurrentArticle == feeds.Articles[0],
                "rapid module switches retain the article and its reading position");
            await Task.Delay(350);
            CheckNavigationClocksSettled(main, surfaces);
            var measurements = new
            {
                baseline, noteCount = 240, switches = results.Sum(result => result.HandlerMilliseconds.Count), staleRoamingWasIgnored,
                batches = results.Select(result => new
                {
                    result.FinalTab, result.NonImmediateContentSamples, result.TreeMeasureInvalidSamples,
                    result.TreeArrangeInvalidSamples, result.RetainedLayoutInvalidSamples,
                    handler = NavigationDistribution(result.HandlerMilliseconds), switchToIdle = NavigationDistribution(result.SwitchToIdleMilliseconds),
                    heartbeat = NavigationDistribution(result.HeartbeatMilliseconds), renderInterval = NavigationDistribution(result.RenderIntervalMilliseconds)
                }),
                idleHeartbeat = NavigationDistribution(idleHeartbeats), idleRenderInterval = NavigationDistribution(idleFrames)
            };
            await File.WriteAllTextAsync(Path.Combine(run, "timings.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            Snapshot(main, Path.Combine(run, "final-reading.png"));
            Console.WriteLine("Navigation switch artifacts: " + run);
            Console.WriteLine(JsonSerializer.Serialize(measurements));
            if (!baseline)
            {
                Check(results.All(result => result.NonImmediateContentSamples == 0), "every rapid module selection reveals fully opaque, unshifted content immediately");
                Check(results.All(result => result.TreeMeasureInvalidSamples == 0 && result.TreeArrangeInvalidSamples == 0 && result.RetainedLayoutInvalidSamples == 0),
                    "warm switches keep the generated tree and both rendered documents measured and arranged");
                Check(staleRoamingWasIgnored, "a delayed roaming continuation cannot replace the newer library selection");
            }
        }
        finally
        {
            main.Close();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
        }
    }

    private static bool NavigationContentIsImmediate(FrameworkElement surface) => Math.Abs(surface.Opacity - 1) < .001 &&
        surface.RenderTransform.Value.IsIdentity && !surface.HasAnimatedProperties && !surface.RenderTransform.HasAnimatedProperties;

    private static void CheckNavigationDestination(MainWindow main, LearningPage learning, ReadingPage reading, string tab)
    {
        Check(Control<RadioButton>(main, tab).IsChecked == true && new[] { "CaptureTab", "LibraryTab", "RoamingTab", "ReadingTab" }
            .Count(name => Control<RadioButton>(main, name).IsChecked == true) == 1, "the last rapid selection owns the sole checked navigation tab");
        Check(Control<FrameworkElement>(main, "CapturePage").IsVisible == (tab == "CaptureTab") &&
            reading.IsVisible == (tab == "ReadingTab") && learning.IsVisible == (tab is "LibraryTab" or "RoamingTab"),
            "the last rapid selection owns the sole visible module");
        if (learning.IsVisible)
            Check(LearningControl<FrameworkElement>(learning, "LibrarySurface").IsVisible == (tab == "LibraryTab") &&
                LearningControl<FrameworkElement>(learning, "RoamingSurface").IsVisible == (tab == "RoamingTab"),
                "the last library/roaming selection also owns the correct inner surface");
    }

    private static void CheckNavigationClocksSettled(MainWindow main, FrameworkElement[] surfaces)
    {
        Check(surfaces.All(NavigationContentIsImmediate), "repeated module switches leave no whole-page opacity or transform animation clocks");
        var indicator = Control<PageTabIndicator>(main, "PageUnderline");
        var nav = Control<Grid>(main, "MainNavigation");
        var selected = new[] { "CaptureTab", "LibraryTab", "RoamingTab", "ReadingTab" }.Select(name => Control<RadioButton>(main, name)).Single(tab => tab.IsChecked == true);
        var left = selected.TranslatePoint(new Point(), nav).X;
        Check(!indicator.HasAnimatedProperties && Math.Abs(indicator.LeftEdge - left) < .1 && Math.Abs(indicator.RightEdge - left - selected.ActualWidth) < .1,
            "interrupted underline animations release their clocks and settle beneath the final tab");
    }

    private static async Task<bool> CheckDelayedRoamingSelectionAsync(MainWindow main, LearningPage learning)
    {
        await SelectPageAsync(main, "CaptureTab");
        var field = typeof(LearningPage).GetField("_loadTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previous = field.GetValue(learning);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        field.SetValue(learning, pending.Task);
        try
        {
            var method = typeof(LearningPage).GetMethod(nameof(LearningPage.ShowRoamingAsync), Type.EmptyTypes)!;
            var arguments = method.GetParameters().Select(parameter => parameter.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None : Type.Missing).ToArray();
            var oldRoaming = (Task)method.Invoke(learning, arguments)!;
            Check(!oldRoaming.IsCompleted, "delayed navigation fixture holds a real roaming continuation during local loading");
            Control<RadioButton>(main, "LibraryTab").IsChecked = true;
            pending.SetResult();
            await oldRoaming.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            return Control<RadioButton>(main, "LibraryTab").IsChecked == true && learning.IsVisible &&
                LearningControl<FrameworkElement>(learning, "LibrarySurface").IsVisible && !LearningControl<FrameworkElement>(learning, "RoamingSurface").IsVisible;
        }
        finally { pending.TrySetResult(); field.SetValue(learning, previous); }
    }

    private static object NavigationDistribution(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        return new { count = sorted.Length, medianMilliseconds = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2],
            p95Milliseconds = sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling((sorted.Length - 1) * .95)], maxMilliseconds = sorted.LastOrDefault() };
    }

    private sealed class NavigationBatch
    {
        public required string FinalTab { get; init; }
        public int NonImmediateContentSamples, TreeMeasureInvalidSamples, TreeArrangeInvalidSamples, RetainedLayoutInvalidSamples;
        public List<double> HandlerMilliseconds { get; } = [];
        public List<double> SwitchToIdleMilliseconds { get; } = [];
        public double[] HeartbeatMilliseconds { get; set; } = [];
        public double[] RenderIntervalMilliseconds { get; set; } = [];
    }

    private sealed class NavigationResponsivenessProbe : IDisposable
    {
        private readonly DispatcherTimer _heartbeat = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastHeartbeat, _lastRender;
        private bool _rendered;
        public List<double> Heartbeats { get; } = [];
        public List<double> RenderIntervals { get; } = [];
        public NavigationResponsivenessProbe() { _heartbeat.Tick += Tick; _heartbeat.Start(); CompositionTarget.Rendering += Render; }
        private void Tick(object? sender, EventArgs args) { var now = _clock.Elapsed.TotalMilliseconds; Heartbeats.Add(now - _lastHeartbeat); _lastHeartbeat = now; }
        private void Render(object? sender, EventArgs args)
        {
            var now = _clock.Elapsed.TotalMilliseconds;
            if (_rendered) RenderIntervals.Add(now - _lastRender);
            _rendered = true; _lastRender = now;
        }
        public void Dispose() { _heartbeat.Stop(); _heartbeat.Tick -= Tick; CompositionTarget.Rendering -= Render; }
    }
}
