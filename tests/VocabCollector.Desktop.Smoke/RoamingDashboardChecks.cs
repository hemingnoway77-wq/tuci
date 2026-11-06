using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    public async Task RunRoamingChecksOnlyAsync()
    {
        var output = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "roaming-dashboard-smoke");
        var run = Path.Combine(output, Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(run, "isolated-vault");
        var wordRoot = Path.Combine(vault, "words");
        var config = Path.Combine(run, "config");
        var state = Path.Combine(config, "learning");
        Directory.CreateDirectory(wordRoot);
        var names = new[] { "apple", "steady", "resilient" };
        foreach (var name in names)
        {
            var links = string.Join(" / ", names.Where(target => target != name).Select(target => "[[" + target + "]]"));
            var text = "# " + name + "\n\n**发音**：/ˈsməʊk/\n\n**释义**：\n\n> n. 隔离词条的完整释义\n\n## 关联阅读\n\n" + links + "\n\n" +
                string.Join("\n\n", Enumerable.Range(1, 30).Select(index => $"Paragraph {index}: This complete local note belongs to {name} and remains selectable without an API or an external application."));
            await File.WriteAllTextAsync(Path.Combine(wordRoot, name + ".md"), text);
        }
        Directory.CreateDirectory(Path.Combine(wordRoot, "词根"));
        await File.WriteAllTextAsync(Path.Combine(wordRoot, "词根", "sal.md"), "# sal\n\n词根可阅读，不参与漫游。\n");
        await File.WriteAllTextAsync(Path.Combine(wordRoot, "阅读说明.md"), "# 阅读说明\n\n普通中文笔记不参与随机复习。\n");
        var originals = Directory.GetFiles(wordRoot, "*.md", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText);
        using var handler = new RoamingNoNetworkHandler();
        using var client = new HttpClient(handler);
        var store = new VaultStore();
        var settings = new AppSettings(new ApiOptions("", "", ""), new VaultOptions(vault, "words", false));
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), store, TimeProvider.System), settings, store);
        var settingsStore = new WindowsSettingsStore(config);
        var themes = new ThemeManager(app.Resources, new AppearanceSettingsStore(config));
        await themes.LoadAsync();
        var navigator = new RecordingNavigator();
        var bindings = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var main = new MainWindow(vm, settingsStore, navigator, new RecordingReadingService(),
            _ => throw new InvalidOperationException("Roaming smoke must not request a remote article"), themes, learningStateDirectory: state);
        app.MainWindow = main;
        main.Show();
        try
        {
            var page = await OpenLibraryAsync(main);
            await CheckRoamingDashboardAsync(main, page, state, output, requirePreviewLink: true);
            await CheckRoamingThemeScreenshotsAsync(main, page, themes, output);
            Check(handler.Requests == 0 && navigator.NoteOpens == 0, "roaming, preview and statistics need no model API or external note application");
            Check(!File.Exists(Path.Combine(config, "settings.json")), "roaming never creates or rewrites API configuration");
            foreach (var original in originals)
                Check(await File.ReadAllTextAsync(original.Key) == original.Value, "roaming and its preview preserve the original Markdown file: " + Path.GetFileName(original.Key));
            Check(bindings.Errors.Count == 0, "roaming dashboard has no WPF binding errors: " + string.Join("; ", bindings.Errors));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindings);
            main.Close();
        }
        Console.WriteLine("Roaming dashboard artifacts: " + output);
    }

    private static MarkdownPreview RoamingReadingPreview(LearningPage page) =>
        Descendants<MarkdownPreview>(page.RoamingReaderControl).Single(preview => preview.Name == "ReadingPreview");

    private static async Task CheckRoamingDashboardAsync(MainWindow main, LearningPage page, string learningState, string output, bool requirePreviewLink)
    {
        await SelectPageAsync(main, "RoamingTab");
        await Until(() => page.HasExposure && !page.IsBusy && page.ActivityDays.Count == 84 && LearningControl<Grid>(page, "RoamingSurface").IsVisible);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var stateFile = Directory.GetFiles(learningState, "roaming.json", SearchOption.AllDirectories).Single();
        var first = ReviewSnapshot(stateFile);
        Check(first.Remembered == 0 && first.Skipped == 0, "first displayed roaming card is not a completed review");
        var revealed = LearningControl<HoldToReveal>(page, "RoamingMeaningReveal");
        Check(revealed.CanReveal && revealed.Text.Length > 0 && !revealed.IsRevealed, "roaming initially masks a real meaning until the user holds to reveal");
        Check(page.AllNotes.Any(note => note.Id == RoamingNoteId(stateFile) && note.IsWord), "roaming chooses an English word rather than a root or Chinese note");
        var visits = new HashSet<string> { RoamingNoteId(stateFile) };
        CheckRoamingStatistics(page, visits.Count, expectedVisits: 1);
        await CheckRoamingIdleActionsAsync(page, stateFile);
        await CheckRoamingRevealAsync(main, page, stateFile, output);
        Snapshot(main, Path.Combine(output, "roaming-dashboard.png"));

        Invoke(LearningControl<Button>(page, "RoamingSkipButton"));
        await Until(() => !page.IsBusy && ReviewSnapshot(stateFile).Exposure != first.Exposure);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var skipped = ReviewSnapshot(stateFile);
        visits.Add(RoamingNoteId(stateFile));
        Check(skipped.Skipped == 1 && skipped.Remembered == 0 && !revealed.IsRevealed, "next card completes exactly one skipped exposure and hides its new meaning");
        Invoke(LearningControl<Button>(page, "RoamingRememberButton"));
        await Until(() => !page.IsBusy && ReviewSnapshot(stateFile).Exposure != skipped.Exposure);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var remembered = ReviewSnapshot(stateFile);
        visits.Add(RoamingNoteId(stateFile));
        Check(remembered.Skipped == 1 && remembered.Remembered == 1 && !revealed.IsRevealed, "remember records one completion, draws again and leaves the new meaning masked");
        CheckRoamingStatistics(page, visits.Count, expectedVisits: 3);
        await SelectTabAsync(LearningControl<RadioButton>(page, "RoamingMonthButton"));
        Check(page.CoverageDescription.StartsWith("本月", StringComparison.Ordinal), "month selector changes the real coverage period");
        CheckRoamingStatistics(page, visits.Count, expectedVisits: 3);
        await SelectTabAsync(LearningControl<RadioButton>(page, "RoamingWeekButton"));
        Check(page.CoverageDescription.StartsWith("本周", StringComparison.Ordinal), "week selector restores the real weekly coverage period");
        Check(ReviewSnapshot(stateFile).Json == remembered.Json, "statistics switching never writes another exposure or completion");

        var title = page.RoamingTitle;
        await OpenRoamingPreviewAsync(main, page);
        Check(page.RoamingReaderControl.Note?.Id == RoamingNoteId(stateFile), "third pane starts with the current roaming card's complete note");
        CheckRoamingColumns(main, page);
        var preview = RoamingReadingPreview(page);
        preview.SelectAll();
        Check(preview.Selection.Text.Length > 0, "third-column Markdown reading permits selecting and copying text");
        var sourceId = page.RoamingReaderControl.Note!.Id;
        var link = Links(preview.Document).FirstOrDefault(candidate => candidate.Tag is PreviewLink { IsWikiLink: true });
        if (requirePreviewLink) Check(link is not null, "isolated roaming fixture exposes a real internal note relationship");
        if (link is not null)
        {
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, link));
            await Until(() => page.RoamingReaderControl.Note?.Id != sourceId);
            Check(Control<RadioButton>(main, "RoamingTab").IsChecked == true && page.RoamingTitle == title,
                "internal preview navigation stays in the independent third column without changing the roaming card");
            Invoke(LearningControl<Button>(page, "RoamingPreviewBackButton"));
            await Until(() => page.RoamingReaderControl.Note?.Id == sourceId);
        }
        Check(ReviewSnapshot(stateFile).Json == remembered.Json, "opening, following links and returning inside the preview do not count as reviews");
        Snapshot(main, Path.Combine(output, "roaming-dashboard-preview.png"));
        Invoke(LearningControl<Button>(page, "CloseRoamingPreviewButton"));
        await Until(() => !page.IsRoamingPreviewOpen);
        Check(Control<RadioButton>(main, "RoamingTab").IsChecked == true && page.RoamingTitle == title, "third-column close leaves the user on the same roaming card");
        await SelectPageAsync(main, "CaptureTab");
        await SelectPageAsync(main, "LibraryTab");
        await Until(() => !page.IsBusy);
        await SelectPageAsync(main, "RoamingTab");
        await Until(() => !page.IsBusy && page.HasExposure && LearningControl<Grid>(page, "RoamingSurface").IsVisible);
        Check(page.RoamingTitle == title && ReviewSnapshot(stateFile).Json == remembered.Json && !revealed.IsRevealed,
            "leaving and returning to roaming preserves its exposure without revealing the meaning or counting another review");
    }

    private static async Task CheckRoamingIdleActionsAsync(LearningPage page, string stateFile)
    {
        var before = ReviewSnapshot(stateFile).Json;
        var skip = LearningControl<Button>(page, "RoamingSkipButton");
        var remember = LearningControl<Button>(page, "RoamingRememberButton");
        var changes = new List<string>();
        void Changed(object sender, DependencyPropertyChangedEventArgs args) => changes.Add(((Button)sender).Name + "=" + args.NewValue);
        skip.IsEnabledChanged += Changed; remember.IsEnabledChanged += Changed;
        try
        {
            page.RequestReload();
            await Task.Delay(900);
            await Until(() => !page.IsBusy);
            await page.ReloadAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(changes.Count == 0 && skip.IsEnabled && remember.IsEnabled, "unchanged background/debounced scans never disable or flash the current roaming actions: " + string.Join(" -> ", changes));
            Check(ReviewSnapshot(stateFile).Json == before, "idle refresh never advances or completes a roaming exposure");
        }
        finally { skip.IsEnabledChanged -= Changed; remember.IsEnabledChanged -= Changed; }
    }

    private static async Task CheckRoamingRevealAsync(MainWindow main, LearningPage page, string stateFile, string output)
    {
        var before = ReviewSnapshot(stateFile).Json;
        var reveal = LearningControl<HoldToReveal>(page, "RoamingMeaningReveal");
        reveal.BringIntoView();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        RevealMouse(reveal, down: true);
        Check(reveal.IsRevealed, "real routed mouse-down reveals the meaning while held");
        Snapshot(main, Path.Combine(output, "roaming-meaning-held.png"));
        RevealMouse(reveal, down: false);
        Check(!reveal.IsRevealed, "mouse release immediately masks the meaning");
        reveal.Focus();
        RevealKey(reveal, down: true);
        Check(reveal.IsRevealed, "holding Space reveals the focused meaning control");
        RevealKey(reveal, down: false);
        Check(!reveal.IsRevealed, "Space release masks the meaning");
        RevealMouse(reveal, down: true);
        reveal.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        Check(!reveal.IsRevealed, "leaving the reveal surface masks the meaning even while a press was active");
        reveal.Hide();
        Check(ReviewSnapshot(stateFile).Json == before, "revealing meaning never creates another visit or completion");
    }

    private static void RevealMouse(UIElement target, bool down)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = down ? Mouse.PreviewMouseDownEvent : Mouse.PreviewMouseUpEvent };
        target.RaiseEvent(args);
        args.RoutedEvent = down ? Mouse.MouseDownEvent : Mouse.MouseUpEvent;
        target.RaiseEvent(args);
    }
    private static void RevealKey(UIElement target, bool down)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, Key.Space)
        { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent };
        target.RaiseEvent(args);
        args.RoutedEvent = down ? Keyboard.KeyDownEvent : Keyboard.KeyUpEvent;
        target.RaiseEvent(args);
    }

    private static void CheckRoamingStatistics(LearningPage page, int uniqueVisits, int expectedVisits)
    {
        var weight = LearningControl<ProgressBar>(page, "RoamingWeightBar");
        var coverage = LearningControl<ProgressBar>(page, "RoamingCoverageBar");
        var words = page.AllNotes.Count(note => note.IsWord);
        Check(page.WeightLevel is >= 0 and <= 1 && weight.Maximum == 1 && Math.Abs(weight.Value - page.WeightLevel) < 0.000001,
            "relative draw weight is a bounded bar rather than the former numeric weight explanation");
        CheckRoamingMeter(weight);
        Check(coverage.Maximum == 1 && Math.Abs(coverage.Value - (double)uniqueVisits / words) < 0.000001 && page.CoverageLabel == $"{uniqueVisits} / {words} 词",
            "coverage bar reports distinct exposed words among the current vocabulary");
        CheckRoamingMeter(coverage);
        Check(!Descendants<TextBlock>(LearningControl<Border>(page, "RoamingStatusPanel")).Any(text => text.IsVisible && text.Text.StartsWith("本次抽取权重", StringComparison.Ordinal)),
            "status panel omits the old numeric draw-weight text");
        Check(page.ActivityDays.Count == 84 && page.ActivityDays.Single(day => day.Day == DateOnly.FromDateTime(DateTime.Now)).Count == expectedVisits,
            "activity heatmap counts one visit for each actual displayed exposure");
        for (var row = 0; row < 7; row++)
            for (var column = 0; column < 12; column++)
                Check(page.ActivityDays[row * 12 + column].Day == page.ActivityDays[0].Day.AddDays(column * 7 + row), "activity calendar places weekdays in rows and chronological weeks in columns");
        var grid = LearningControl<ItemsControl>(page, "RoamingActivityGrid");
        grid.UpdateLayout();
        var cells = Descendants<Border>(grid).Where(cell => cell.DataContext is RoamingActivityCell && cell.ToolTip is string).ToArray();
        Check(cells.Length == 84 && cells.Select(cell => cell.ToolTip).Distinct().Count() == 84, "every visible calendar cell has its date/count tooltip");
        Check(page.ActivityDays.Where(day => day.IsFuture).All(day => day.Count == 0 && day.Opacity < 1), "future dates are muted and contain no invented activity");
    }

    private static async Task OpenRoamingPreviewAsync(MainWindow main, LearningPage page)
    {
        Invoke(LearningControl<Button>(page, "RoamingReadNoteButton"));
        await Until(() => page.IsRoamingPreviewOpen && LearningControl<FrameworkElement>(page, "RoamingPreviewPane").IsVisible && page.RoamingReaderControl.Note is not null);
        await Task.Delay(230);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(Control<RadioButton>(main, "RoamingTab").IsChecked == true, "full-note action opens a third column while remaining on the roaming tab");
    }

    private static void CheckRoamingMeter(ProgressBar bar)
    {
        bar.ApplyTemplate(); bar.UpdateLayout();
        var track = bar.Template.FindName("PART_Track", bar) as FrameworkElement;
        var indicator = bar.Template.FindName("PART_Indicator", bar) as FrameworkElement;
        Check(track is { ActualWidth: > 0 } && indicator is not null &&
            Math.Abs(indicator.ActualWidth - track.ActualWidth * (bar.Value - bar.Minimum) / (bar.Maximum - bar.Minimum)) <= 2,
            "static roaming meter visibly fills the correct fraction: " + bar.Name);
    }

    private static void CheckRoamingColumns(MainWindow main, LearningPage page)
    {
        var card = VisibleBounds(LearningControl<FrameworkElement>(page, "RoamingWordCard"), main);
        var status = VisibleBounds(LearningControl<FrameworkElement>(page, "RoamingStatusPanel"), main);
        var preview = VisibleBounds(LearningControl<FrameworkElement>(page, "RoamingPreviewPane"), main);
        Check(card.Width >= 235 && status.Width >= 205 && preview.Width >= 285 && card.Right <= status.Left + 1 && status.Right <= preview.Left + 1,
            "word card, statistics and independent full-note preview remain three distinct usable columns");
    }

    private static async Task CheckRoamingThemeScreenshotsAsync(MainWindow main, LearningPage page, ThemeManager themes, string output)
    {
        var width = main.Width; var height = main.Height; var theme = themes.CurrentTheme;
        try
        {
            foreach (var (option, target, slug) in new[] { ("ThemeWarmOption", AppTheme.WarmNeutral, "warm"), ("ThemeRoseOption", AppTheme.Pink, "pink"), ("ThemeMonoOption", AppTheme.Mono, "mono") })
            {
                await SwitchThemeAsync(Control<RadioButton>(main, option), themes, target);
                await OpenRoamingPreviewAsync(main, page);
                CheckRoamingColumns(main, page);
                Snapshot(main, Path.Combine(output, "roaming-dashboard-" + slug + ".png"));
                Invoke(LearningControl<Button>(page, "CloseRoamingPreviewButton"));
                await Until(() => !page.IsRoamingPreviewOpen);
            }
            main.Width = main.MinWidth; main.Height = main.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await OpenRoamingPreviewAsync(main, page);
            main.UpdateLayout();
            CheckRoamingColumns(main, page);
            CheckRoamingMeter(LearningControl<ProgressBar>(page, "RoamingWeightBar"));
            CheckRoamingMeter(LearningControl<ProgressBar>(page, "RoamingCoverageBar"));
            CheckReachable(LearningControl<Button>(page, "RoamingSkipButton"), main, "next card remains reachable with a third pane at minimum size");
            CheckReachable(LearningControl<Button>(page, "RoamingRememberButton"), main, "remember remains reachable with a third pane at minimum size");
            CheckReachable(LearningControl<Button>(page, "CloseRoamingPreviewButton"), main, "independent preview close remains reachable at minimum size");
            Check(RoamingReadingPreview(page).ActualWidth >= 240 && RoamingReadingPreview(page).ActualHeight >= 180, "minimum three-column layout retains a useful text-reading area");
            Snapshot(main, Path.Combine(output, "roaming-dashboard-minimum.png"));
            var statisticsScroll = Descendants<ScrollViewer>(LearningControl<Border>(page, "RoamingStatusPanel")).Single();
            statisticsScroll.ScrollToEnd();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var activityGrid = LearningControl<ItemsControl>(page, "RoamingActivityGrid");
            CheckReachable(activityGrid, main, "all 84 activity cells remain reachable without horizontal clipping at minimum size");
            var lastWeek = page.ActivityDays[0].Day.AddDays(77);
            var rightmostWeek = Descendants<Border>(activityGrid).Where(cell => cell.DataContext is RoamingActivityCell day && day.Day >= lastWeek).ToArray();
            Check(rightmostWeek.Length == 7, "rightmost heatmap column contains the seven current-week dates");
            foreach (var cell in rightmostWeek) CheckReachable(cell, main, "rightmost activity cell stays fully inside the status viewport: " + cell.ToolTip);
            Snapshot(main, Path.Combine(output, "roaming-dashboard-activity-minimum.png"));
        }
        finally
        {
            page.CloseRoamingPreview(); main.Width = width; main.Height = height;
            await themes.SetThemeAsync(theme);
        }
    }

    private static string RoamingNoteId(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("CurrentNoteId").GetString()!;
    }

    private sealed class RoamingNoNetworkHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; throw new InvalidOperationException("Roaming is local; unexpected HTTP request: " + request.RequestUri); }
    }
}
