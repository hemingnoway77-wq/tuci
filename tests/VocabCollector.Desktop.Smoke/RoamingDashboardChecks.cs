using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
        CheckRoamingVisualSimplicity(page);
        CheckRoamingStatusFits(main, page);
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
        await CheckRoamingPeriodSwitchAsync(page, monthly: true);
        CheckRoamingStatistics(page, visits.Count, expectedVisits: 3);
        await CheckRoamingPeriodSwitchAsync(page, monthly: false);
        Check(ReviewSnapshot(stateFile).Json == remembered.Json, "statistics switching never writes another exposure or completion");

        var title = page.RoamingTitle;
        await OpenRoamingPreviewAsync(main, page);
        Check(page.RoamingReaderControl.Note?.Id == RoamingNoteId(stateFile), "third pane starts with the current roaming card's complete note");
        CheckRoamingColumns(main, page);
        CheckRoamingStatusFits(main, page);
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
        CheckBareReveal(reveal);
        RevealMouse(reveal, down: true);
        Check(reveal.IsRevealed, "real routed mouse-down reveals the meaning while held");
        CheckBareReveal(reveal);
        Snapshot(main, Path.Combine(output, "roaming-meaning-held.png"));
        RevealMouse(reveal, down: false);
        Check(!reveal.IsRevealed, "mouse release immediately masks the meaning");
        Check(Descendants<TextBlock>(reveal).Any(text => text.Name == "RoamingMeaningText" && text.Effect is BlurEffect), "unheld meaning is represented by bare blurred text");
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
                CheckRoamingVisualSimplicity(page);
                CheckRoamingStatusFits(main, page);
                Snapshot(main, Path.Combine(output, "roaming-dashboard-" + slug + ".png"));
                Invoke(LearningControl<Button>(page, "CloseRoamingPreviewButton"));
                await Until(() => !page.IsRoamingPreviewOpen);
            }
            main.Width = main.MinWidth; main.Height = main.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await OpenRoamingPreviewAsync(main, page);
            main.UpdateLayout();
            CheckRoamingColumns(main, page);
            CheckRoamingVisualSimplicity(page);
            CheckRoamingStatusFits(main, page);
            CheckRoamingMeter(LearningControl<ProgressBar>(page, "RoamingWeightBar"));
            CheckRoamingMeter(LearningControl<ProgressBar>(page, "RoamingCoverageBar"));
            CheckReachable(LearningControl<Button>(page, "RoamingSkipButton"), main, "next card remains reachable with a third pane at minimum size");
            CheckReachable(LearningControl<Button>(page, "RoamingRememberButton"), main, "remember remains reachable with a third pane at minimum size");
            CheckReachable(LearningControl<Button>(page, "CloseRoamingPreviewButton"), main, "independent preview close remains reachable at minimum size");
            Check(RoamingReadingPreview(page).ActualWidth >= 240 && RoamingReadingPreview(page).ActualHeight >= 180, "minimum three-column layout retains a useful text-reading area");
            Snapshot(main, Path.Combine(output, "roaming-dashboard-minimum.png"));
            Snapshot(main, Path.Combine(output, "roaming-dashboard-activity-minimum.png"));
            await CheckRoamingAdaptiveHeightAsync(main, page, output);
        }
        finally
        {
            page.CloseRoamingPreview(); main.Width = width; main.Height = height;
            await themes.SetThemeAsync(theme);
        }
    }

    private static void CheckRoamingVisualSimplicity(LearningPage page)
    {
        var surface = LearningControl<Grid>(page, "RoamingSurface");
        var banned = new[] { "旧词、新词", "相对本轮", "重复遇见", "遇见的不同单词", "近 12 周", "每格一天", "浅色较少", "深色较多", "旧版未记录每日历史", "每日记录实际漫游次数", "记录保存在本机" };
        foreach (var text in Descendants<TextBlock>(surface).Where(text => text.IsVisible))
            Check(!banned.Any(fragment => text.Text.Contains(fragment, StringComparison.Ordinal)), "roaming omits explanatory copy removed from the compact dashboard: " + text.Text);
        CheckBareReveal(LearningControl<HoldToReveal>(page, "RoamingMeaningReveal"));
        Check(!Descendants<ScrollViewer>(LearningControl<Border>(page, "RoamingStatusPanel")).Any(), "statistics modules use their available height without a scroll viewer");
    }

    private static void CheckBareReveal(HoldToReveal reveal)
    {
        Check(Descendants<Border>(reveal).All(border => border.BorderThickness == new Thickness(0)), "hold-to-reveal has no frame or focus border");
        Check(Descendants<TextBlock>(reveal).Where(text => text.IsVisible).All(text => text.Text == reveal.Text), "bare blurred meaning has no visible reveal instruction or hint overlay");
    }

    private static void CheckRoamingStatusFits(MainWindow main, LearningPage page)
    {
        var panel = LearningControl<Border>(page, "RoamingStatusPanel");
        Check(!Descendants<ScrollViewer>(panel).Any(), "status panel contains no hidden or visible scrolling surface");
        foreach (var name in new[] { "RoamingWeightSection", "RoamingCoverageSection", "RoamingActivitySection" })
        {
            var section = LearningControl<StackPanel>(page, name);
            Check(section.Visibility == Visibility.Visible, "normal and minimum-height dashboards show the complete statistics module: " + name);
            CheckReachable(section, main, "whole statistics module fits without scrolling: " + name);
        }
        var grid = LearningControl<ItemsControl>(page, "RoamingActivityGrid");
        CheckReachable(grid, main, "all 84 activity cells fit without scrolling or horizontal clipping");
        var lastWeek = page.ActivityDays[0].Day.AddDays(77);
        var rightmost = Descendants<Border>(grid).Where(cell => cell.DataContext is RoamingActivityCell day && day.Day >= lastWeek).ToArray();
        Check(rightmost.Length == 7, "rightmost heatmap column contains seven current-week dates");
        foreach (var cell in rightmost) CheckReachable(cell, main, "rightmost activity cell is fully visible: " + cell.ToolTip);
    }

    private static async Task CheckRoamingAdaptiveHeightAsync(MainWindow main, LearningPage page, string output)
    {
        var panel = LearningControl<Border>(page, "RoamingStatusPanel");
        var body = LearningControl<Grid>(page, "RoamingStatusBody");
        var header = LearningControl<FrameworkElement>(page, "RoamingStatusHeader");
        var weight = LearningControl<StackPanel>(page, "RoamingWeightSection");
        var coverage = LearningControl<StackPanel>(page, "RoamingCoverageSection");
        var activity = LearningControl<StackPanel>(page, "RoamingActivitySection");
        var previousHeight = panel.Height;
        var padding = panel.Padding.Top + panel.Padding.Bottom + panel.BorderThickness.Top + panel.BorderThickness.Bottom;
        foreach (var element in new FrameworkElement[] { header, weight, coverage, activity })
            element.Measure(new Size(Math.Max(1, body.ActualWidth), double.PositiveInfinity));
        var fixedHeight = header.DesiredSize.Height + weight.DesiredSize.Height;
        var coverageHeight = coverage.DesiredSize.Height;
        var activityHeight = activity.DesiredSize.Height;
        Check(fixedHeight > 0 && coverageHeight > 0 && activityHeight > 0, "adaptive height checks use the measured content heights of actual modules");
        try
        {
            panel.Height = padding + fixedHeight + coverageHeight + activityHeight / 2;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();
            await Until(() => activity.Visibility == Visibility.Collapsed && coverage.Visibility == Visibility.Visible);
            Check(weight.Visibility == Visibility.Visible, "reducing height first hides daily activity while retaining weight and coverage");
            CheckReachable(coverage, main, "remaining coverage is fully visible at the intermediate height");
            Snapshot(main, Path.Combine(output, "roaming-status-short.png"));

            panel.Height = padding + fixedHeight + coverageHeight / 2;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            main.UpdateLayout();
            await Until(() => activity.Visibility == Visibility.Collapsed && coverage.Visibility == Visibility.Collapsed);
            Check(weight.Visibility == Visibility.Visible && !Descendants<ScrollViewer>(panel).Any(), "further height reduction hides coverage second while keeping the weight module and no scrollbar");
            CheckReachable(weight, main, "weight remains fully visible at the shortest checked height");
            Snapshot(main, Path.Combine(output, "roaming-status-shortest.png"));
        }
        finally { panel.Height = previousHeight; }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        main.UpdateLayout();
        await Until(() => activity.Visibility == Visibility.Visible && coverage.Visibility == Visibility.Visible);
        CheckRoamingStatusFits(main, page);
        Snapshot(main, Path.Combine(output, "roaming-status-restored.png"));
    }

    private static async Task CheckRoamingPeriodSwitchAsync(LearningPage page, bool monthly)
    {
        var week = LearningControl<RadioButton>(page, "RoamingWeekButton");
        var month = LearningControl<RadioButton>(page, "RoamingMonthButton");
        var (selector, line) = PeriodUnderline(week, month);
        var before = UnderlineBounds(selector, line);
        var target = monthly ? month : week;
        await SelectTabAsync(target);
        var samples = new List<Rect>();
        var watch = Stopwatch.StartNew();
        Rect current;
        do
        {
            selector.UpdateLayout();
            current = UnderlineBounds(selector, line);
            samples.Add(current);
            var targetBounds = target.TransformToAncestor(selector).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            if (Math.Abs(current.Left + current.Width / 2 - targetBounds.Left - targetBounds.Width / 2) <= 3 && current.Width <= before.Width + 1) break;
            if (watch.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Shared period underline failed to settle beneath the selected period.");
            await Task.Delay(20);
        } while (true);
        Check(target.IsChecked == true && (monthly ? week : month).IsChecked == false, "period selector changes one real weekly/monthly selection");
        Check(current.Height <= 2.5 && current.Width > 4 && current.Width < target.ActualWidth * 0.75, "period selector settles as one thin short underline beneath its selected label");
        Check(Math.Abs(current.Left - before.Left) > 10, "shared underline moves to the newly selected period");
        if (SystemParameters.ClientAreaAnimation)
            Check(samples.Any(sample => sample.Width > Math.Max(before.Width, current.Width) + 1), "shared period underline visibly extends then shrinks during selection");
    }

    private static (FrameworkElement Selector, FrameworkElement Line) PeriodUnderline(RadioButton week, RadioButton month)
    {
        for (DependencyObject? element = week; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is not FrameworkElement parent) continue;
            var lines = Descendants<FrameworkElement>(parent).Where(child => child.Name == "PeriodUnderline").ToArray();
            if (lines.Length == 0 || !Descendants<RadioButton>(parent).Contains(month)) continue;
            Check(lines.Length == 1, "week and month share a single underline rather than independent full-width tab borders");
            return (parent, lines.Single());
        }
        throw new InvalidOperationException("Period selector must expose its real shared PeriodUnderline element.");
    }
    private static Rect UnderlineBounds(FrameworkElement selector, FrameworkElement line) =>
        line.TransformToAncestor(selector).TransformBounds(new Rect(0, 0, line.ActualWidth, line.ActualHeight));

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
