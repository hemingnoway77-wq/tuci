using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Controls;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    public async Task RunPreparedStartupChecksAsync()
    {
        var run = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "prepared-startup", Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(run, "vault"); var words = Path.Combine(vault, "words");
        Directory.CreateDirectory(words);
        Directory.CreateDirectory(Path.Combine(words, "empty", "nested", "leaf"));
        for (var index = 0; index < 240; index++)
        {
            var word = "sample" + (char)('a' + index / 26) + (char)('a' + index % 26);
            await File.WriteAllTextAsync(Path.Combine(words, word + ".md"), "# " + word + "\n\n**释义**：\n\n> n. 本地启动验收词条\n\n" + string.Join("\n\n", Enumerable.Repeat("A local note keeps readable text available without a network request.", 12)));
        }
        var settings = new AppSettings(new ApiOptions("", "", ""), new VaultOptions(vault, "words", false));
        using var handler = new RoamingNoNetworkHandler(); using var client = new System.Net.Http.HttpClient(handler);
        var store = new VaultStore();
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), store, TimeProvider.System), settings, store);
        var feeds = new RecordingReadingService(); var translations = new RecordingWordTranslationService();
        var themes = new ThemeManager(app.Resources, new AppearanceSettingsStore(Path.Combine(run, "appearance"))); await themes.LoadAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparations = 0;
        var main = new MainWindow(vm, new WindowsSettingsStore(Path.Combine(run, "config")), new RecordingNavigator(), feeds,
            _ => throw new InvalidOperationException("Startup must not open a remote page."), themes,
            learningStateDirectory: Path.Combine(run, "learning"), articleContentService: new RecordingArticleContentService(),
            wordTranslationService: translations, prepareLocalData: () => { preparations++; return release.Task; });
        app.MainWindow = main;
        var measurements = new Dictionary<string, double>();
        main.Show();
        try
        {
            await Until(() => Control<FrameworkElement>(main, "PreparationOverlay").IsVisible && preparations == 1);
            Check(!Control<Grid>(main, "MainNavigation").IsEnabled, "startup navigation waits for real local preparation rather than blocking on the first click");
            Snapshot(main, Path.Combine(run, "preparing.png"));
            var clock = Stopwatch.StartNew(); release.SetResult();
            await main.PrepareForUseAsync(); measurements["localPreparationMilliseconds"] = clock.Elapsed.TotalMilliseconds;
            Check(preparations == 1 && Control<Grid>(main, "MainNavigation").IsEnabled && !Control<FrameworkElement>(main, "PreparationOverlay").IsVisible,
                "startup shares its preparation task and releases the loading surface");
            var learning = Learning(main);
            Check(learning.AllNotes.Count == 240 && !learning.HasExposure && learning.ActivityDays.All(day => day.Count == 0),
                "local prewarm loads the catalog without starting a roaming review");
            var reading = Control<Grid>(main, "PageHost").Children.OfType<ReadingPage>().Single();
            Check(!reading.IsVisible && feeds.Calls == 0 && translations.Calls == 0 && handler.Requests == 0,
                "prewarm constructs hidden native reading UI without feed, AI or translation requests");
            Check(ReadingControl<Grid>(reading, "TranslationRuntimeHost").Children.Count == 0, "startup does not launch the translation browser");
            var browserTree = LearningControl<TreeView>(learning, "BrowserFilesTree");
            var preparedRows = Descendants<TreeViewItem>(browserTree).Count();
            measurements["preparedTreeRows"] = preparedRows;
            measurements["preparedTreeWidth"] = browserTree.ActualWidth;
            measurements["preparedTreeHeight"] = browserTree.ActualHeight;
            Check(browserTree.ActualWidth > 0 && browserTree.ActualHeight > 0 && browserTree.IsMeasureValid && browserTree.IsArrangeValid,
                "startup finishes the real file tree's measure and arrange at the window's actual size");
            Check(Descendants<TreeViewItem>(browserTree).Count(item => item.DataContext is LibraryTreeEntry { IsFolder: true }) == 3,
                "startup realizes expanded empty nested folders as well as note rows");
            Check(browserTree.SelectedItem is null && learning.SelectedNote is null && learning.ReaderControl.Note is null &&
                Control<RadioButton>(main, "CaptureTab").IsChecked == true && !learning.IsVisible,
                "layout prewarm preserves the capture tab and the empty reader without selecting a note");
            clock.Restart();
            Control<RadioButton>(main, "LibraryTab").IsChecked = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            await Until(() => browserTree.IsVisible && browserTree.ActualWidth > 0 && Descendants<TreeViewItem>(browserTree).Any());
            measurements["firstLibraryLayoutMilliseconds"] = clock.Elapsed.TotalMilliseconds;
            await SelectPageAsync(main, "CaptureTab");
            clock.Restart();
            Control<RadioButton>(main, "LibraryTab").IsChecked = true;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            measurements["secondLibraryLayoutMilliseconds"] = clock.Elapsed.TotalMilliseconds;
            Console.WriteLine("Library first-entry evidence: " + JsonSerializer.Serialize(measurements));
            Check(preparedRows > 0, "startup must realize the real file rows before enabling navigation, not just load hidden data");
            clock.Restart();
            for (var index = 0; index < 10; index++) await learning.EnsureLoadedAsync();
            measurements["tenWarmPreparationsMilliseconds"] = clock.Elapsed.TotalMilliseconds;
            clock.Restart(); await SelectPageAsync(main, "RoamingTab"); await Until(() => learning.HasExposure && !learning.IsBusy);
            measurements["firstRoamingContentMilliseconds"] = clock.Elapsed.TotalMilliseconds;
            var exposure = learning.RoamingTitle;
            foreach (var name in new[] { "LibraryTab", "CaptureTab", "RoamingTab", "CaptureTab", "LibraryTab", "ReadingTab", "RoamingTab" })
                await SelectPageAsync(main, name);
            await Until(() => learning.HasExposure && !learning.IsBusy);
            await Task.Delay(300);
            Check(learning.RoamingTitle == exposure && learning.ActivityDays.Sum(day => day.Count) == 1,
                "warm module switches retain the current card and never add an extra encounter");
            var indicator = Control<PageTabIndicator>(main, "PageUnderline");
            var nav = Control<Grid>(main, "MainNavigation"); var target = Control<RadioButton>(main, "RoamingTab");
            var center = target.TranslatePoint(new Point(target.ActualWidth / 2, 0), nav).X;
            var width = indicator.RightEdge - indicator.LeftEdge;
            Check(Math.Abs(indicator.LeftEdge + width / 2 - center) < .1 && width is >= 21 and <= 23 && !indicator.HasAnimatedProperties,
                "rapid real module switches settle one shared underline beneath the current navigation label");
            Check(handler.Requests == 0 && translations.Calls == 0, "warm switching does not initialize a model or call AI");
            Snapshot(main, Path.Combine(run, "warm-roaming.png"));
            await File.WriteAllTextAsync(Path.Combine(run, "timings.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Prepared startup artifacts: " + run);
            Console.WriteLine(JsonSerializer.Serialize(measurements));
        }
        finally { release.TrySetResult(); main.Close(); }
        await CheckInterruptedStartupAsync(run, settings, themes, failPreparation: false);
        await CheckInterruptedStartupAsync(run, settings, themes, failPreparation: true);
    }

    private async Task CheckInterruptedStartupAsync(string run, AppSettings settings, ThemeManager themes, bool failPreparation)
    {
        var scenario = Path.Combine(run, failPreparation ? "failed-preparation" : "closed-preparation");
        using var handler = new RoamingNoNetworkHandler(); using var client = new System.Net.Http.HttpClient(handler);
        var store = new VaultStore();
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), store, TimeProvider.System), settings, store);
        var feeds = new RecordingReadingService(); var translations = new RecordingWordTranslationService();
        var content = new RecordingArticleContentService();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;
        var main = new MainWindow(vm, new WindowsSettingsStore(Path.Combine(scenario, "config")), new RecordingNavigator(), feeds,
            _ => throw new InvalidOperationException("Interrupted startup must not open a remote page."), themes,
            learningStateDirectory: Path.Combine(scenario, "learning"), articleContentService: content,
            wordTranslationService: translations, prepareLocalData: () => { entered = true; return release.Task; });
        app.MainWindow = main;
        main.Show();
        try
        {
            await Until(() => entered && Control<FrameworkElement>(main, "PreparationOverlay").IsVisible);
            var preparing = main.PrepareForUseAsync();
            Check(!preparing.IsCompleted, "interrupted startup uses the pending shared preparation task");
            if (failPreparation) release.SetException(new IOException("Smoke fixture: local preparation failed."));
            else { main.Close(); release.SetResult(); }
            await preparing.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(Control<FrameworkElement>(main, "PreparationOverlay").Visibility == Visibility.Collapsed &&
                !Control<ProgressBar>(main, "PreparationProgress").IsIndeterminate && Control<Grid>(main, "MainNavigation").IsEnabled,
                "closing or failing startup always releases its loading and navigation state");
            Check(!Control<Grid>(main, "PageHost").Children.OfType<LearningPage>().Any() &&
                !Control<Grid>(main, "PageHost").Children.OfType<ReadingPage>().Any(),
                "closed or failed local preparation cannot attach late page controls");
            if (failPreparation)
            {
                Check(main.IsVisible && vm.Message.Contains("本地内容暂未准备好", StringComparison.Ordinal) &&
                    Control<RadioButton>(main, "CaptureTab").IsChecked == true,
                    "local preparation failure leaves a usable capture page with a retry message");
                Control<RadioButton>(main, "LibraryTab").IsChecked = true;
                await Until(() => Control<Grid>(main, "PageHost").Children.OfType<LearningPage>().Any(page => page.AllNotes.Count == 240 && !page.IsBusy));
                var learning = Learning(main);
                Check(!learning.HasExposure && learning.ReaderControl.Note is null,
                    "the library can retry after startup failure without choosing a note or starting a review");
            }
            else Check(!main.IsVisible, "completion after closing does not reopen the startup window");
            Check(feeds.Calls == 0 && content.Calls == 0 && translations.Calls == 0 && handler.Requests == 0,
                "interrupted preparation and local retry never request feeds, article content, translation or AI");
        }
        finally { release.TrySetResult(); main.Close(); }
    }
}
