using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Tests;

public sealed class RoamingPanelTests
{
    [Fact]
    public void ShortStatusPanelHidesActivityThenCoverageAndRestoresTheSelectedPeriod() => Run((page, root) =>
    {
        var panel = Find<Border>(page, "RoamingStatusPanel");
        var body = Find<Grid>(page, "RoamingStatusBody");
        var header = Find<TextBlock>(page, "RoamingStatusHeader");
        var weight = Find<StackPanel>(page, "RoamingWeightSection");
        var coverage = Find<StackPanel>(page, "RoamingCoverageSection");
        var activity = Find<StackPanel>(page, "RoamingActivitySection");
        var stateFile = Directory.GetFiles(root, "roaming.json", SearchOption.AllDirectories).Single();
        var before = File.ReadAllText(stateFile);
        Assert.Empty(Descendants<ScrollViewer>(panel));
        Assert.True(activity.IsVisible && coverage.IsVisible);
        page.SetStatisticsPeriod(true);
        var chrome = panel.ActualHeight - body.ActualHeight;
        var fixedHeight = header.DesiredSize.Height + weight.DesiredSize.Height;
        var coverageHeight = coverage.DesiredSize.Height;
        var activityHeight = activity.DesiredSize.Height;
        panel.Height = chrome + fixedHeight + coverageHeight + activityHeight / 2;
        Pump();
        Assert.False(activity.IsVisible);
        Assert.True(coverage.IsVisible && weight.IsVisible);
        panel.Height = chrome + fixedHeight + coverageHeight / 2;
        Pump();
        Assert.False(activity.IsVisible || coverage.IsVisible);
        Assert.True(weight.IsVisible);
        panel.Height = double.NaN;
        Pump();
        Assert.True(activity.IsVisible && coverage.IsVisible && weight.IsVisible);
        Assert.True(Find<PeriodSelector>(page, "RoamingPeriodSelector").IsMonthly);
        Assert.Equal(84, page.ActivityDays.Count);
        Assert.Equal(before, File.ReadAllText(stateFile));
    });

    [Fact]
    public void PreviewIsAnIndependentThirdPaneAndNavigationDoesNotCompleteReview() => Run((page, root) =>
    {
        var stateFile = Directory.GetFiles(root, "roaming.json", SearchOption.AllDirectories).Single();
        var before = File.ReadAllText(stateFile);
        var word = page.RoamingTitle;
        page.OpenRoamingPreview(); Pump();
        Assert.True(page.IsRoamingPreviewOpen);
        Assert.Equal(word, page.RoamingReaderControl.Note?.Title);
        Assert.True(Find<Grid>(page, "RoamingSurface").IsVisible);
        Assert.False(Find<Grid>(page, "LibrarySurface").IsVisible);
        var preview = Descendants<MarkdownPreview>(page.RoamingReaderControl).Single();
        var link = preview.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()).First();
        link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent)); Pump();
        Assert.NotEqual(word, page.RoamingReaderControl.Note?.Title);
        Assert.Equal(word, page.RoamingTitle);
        Assert.Equal(before, File.ReadAllText(stateFile));
        page.CloseRoamingPreview(); Pump();
        Assert.False(page.IsRoamingPreviewOpen);
        Assert.False(Find<Border>(page, "RoamingPreviewPane").IsVisible);
        Assert.True(Find<Border>(page, "RoamingStatusPanel").IsVisible);
        Assert.Equal(before, File.ReadAllText(stateFile));
        page.OpenRoamingPreview(); Pump();
        Assert.Equal(word, page.RoamingReaderControl.Note?.Title);
    });

    [Fact]
    public void AdvanceFollowsInOpenPreviewAndOnlyCountsTheActualNewExposure() => Run((page, root) =>
    {
        page.OpenRoamingPreview();
        var stateFile = Directory.GetFiles(root, "roaming.json", SearchOption.AllDirectories).Single();
        var before = File.ReadAllText(stateFile);
        Find<Button>(page, "RoamingSkipButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => !page.IsBusy && File.ReadAllText(stateFile) != before);
        Assert.Equal(page.RoamingTitle, page.RoamingReaderControl.Note?.Title);
        Assert.Equal(2, page.ActivityDays.Sum(day => day.Count));
        Assert.InRange(page.Coverage, .5, 1);
        var after = File.ReadAllText(stateFile);
        Await(page.ShowRoamingAsync());
        Assert.Equal(after, File.ReadAllText(stateFile));
        Assert.Equal(2, page.ActivityDays.Sum(day => day.Count));
    });

    [Fact]
    public void DashboardUsesBarsWithoutNumericWeightAndAWeeklyColumnHeatmap() => Run((page, root) =>
    {
        Assert.InRange(page.WeightLevel, 0, 1);
        Assert.DoesNotContain(page.WeightDescription, char.IsDigit);
        Assert.Equal(84, page.ActivityDays.Count);
        Assert.Equal(DayOfWeek.Monday, page.ActivityDays[0].Day.DayOfWeek);
        Assert.Equal(page.ActivityDays[0].Day.AddDays(7), page.ActivityDays[1].Day);
        Assert.Equal(page.ActivityDays[0].Day.AddDays(1), page.ActivityDays[12].Day);
        Assert.Contains("2 词", page.CoverageLabel);
        Assert.Equal(.5, page.Coverage);
        var state = Directory.GetFiles(root, "roaming.json", SearchOption.AllDirectories).Single();
        var before = File.ReadAllText(state);
        page.SetStatisticsPeriod(true);
        Assert.Contains("本月", page.CoverageDescription);
        page.SetStatisticsPeriod(false);
        Assert.Contains("本周", page.CoverageDescription);
        Assert.Equal(before, File.ReadAllText(state));
        Assert.Contains("漫游 1 次", page.ActivityDays.Single(day => day.Count > 0).Tooltip);
        Assert.Contains("1 个不同单词", page.ActivityDays.Single(day => day.Count > 0).Tooltip);
    });

    private static void Run(Action<LearningPage, string> action) => MarkdownPreviewTests.OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var root = Path.Combine(Path.GetTempPath(), "roaming-panel-" + Guid.NewGuid().ToString("N"));
        var vault = new VaultOptions(Path.Combine(root, "vault"), "words", false);
        Directory.CreateDirectory(vault.RootPath);
        Directory.CreateDirectory(VaultPaths.WordRoot(vault));
        File.WriteAllText(Path.Combine(VaultPaths.WordRoot(vault), "apple.md"), "# apple\n\n> n. 苹果\n\n[[banana]]");
        File.WriteAllText(Path.Combine(VaultPaths.WordRoot(vault), "banana.md"), "# banana\n\n> n. 香蕉\n\n[[apple]]");
        var originals = Directory.GetFiles(VaultPaths.WordRoot(vault)).ToDictionary(path => path, File.ReadAllText);
        using var page = new LearningPage(vault, Path.Combine(root, "state"));
        var window = new Window { Content = page, Width = 920, Height = 700, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try
        {
            window.Show(); Await(page.ReloadAsync()); Await(page.ShowRoamingAsync()); Pump();
            action(page, root);
            Assert.All(originals, pair => Assert.Equal(pair.Value, File.ReadAllText(pair.Key)));
        }
        finally
        {
            page.Dispose(); window.Close();
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected temporary fixture path.");
            Directory.Delete(root, true);
        }
    });
    private static T Find<T>(LearningPage page, string name) where T : class => Assert.IsType<T>(page.FindName(name));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Pump() => MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(25));
    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) Pump();
        Assert.True(condition());
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
}
