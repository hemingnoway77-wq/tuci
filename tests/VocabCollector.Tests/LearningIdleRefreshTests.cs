using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

public sealed class LearningIdleRefreshTests
{
    [Fact]
    public void NoRefreshLeavesIdleRoamingActionsEnabled() => Run(fixture =>
    {
        using var trace = new ButtonTrace(fixture.Page);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(900));
        Assert.Empty(trace.States);
        Assert.True(fixture.Skip.IsEnabled && fixture.Remember.IsEnabled);
    });

    [Fact]
    public void UnchangedLibraryReloadDoesNotDisableCurrentRoamingActions() => Run(fixture =>
    {
        var exposure = File.ReadAllText(fixture.ReviewPath);
        var title = fixture.Page.RoamingTitle;
        using var trace = new ButtonTrace(fixture.Page);
        Await(fixture.Page.ReloadAsync());
        Assert.Equal(title, fixture.Page.RoamingTitle);
        Assert.Equal(exposure, File.ReadAllText(fixture.ReviewPath));
        Assert.True(trace.AllEnabled, trace.Describe());
    });

    [Fact]
    public void DebouncedBackgroundReloadDoesNotDisableCurrentRoamingActions() => Run(fixture =>
    {
        var exposure = File.ReadAllText(fixture.ReviewPath);
        using var trace = new ButtonTrace(fixture.Page);
        fixture.Page.RequestReload();
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(200));
        Assert.Empty(trace.States); // The request is debounced, not an immediate card action.
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(900));
        AwaitIdle(fixture.Page);
        Assert.Equal(exposure, File.ReadAllText(fixture.ReviewPath));
        Assert.True(trace.AllEnabled, trace.Describe());
    });

    [Fact]
    public void SameContentMarkdownWriteDoesNotDisableUnchangedRoamingCard() => Run(fixture =>
    {
        var exposure = File.ReadAllText(fixture.ReviewPath);
        var original = File.ReadAllText(fixture.NotePath);
        using var trace = new ButtonTrace(fixture.Page);
        // Simulates an editor/sync tool touching a visible .md file without changing its body.
        File.WriteAllText(fixture.NotePath, original);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(1300));
        AwaitIdle(fixture.Page);
        Assert.Equal(exposure, File.ReadAllText(fixture.ReviewPath));
        Assert.Equal(original, File.ReadAllText(fixture.NotePath));
        Assert.True(trace.AllEnabled, trace.Describe());
    });

    private static void Run(Action<Fixture> test) => MarkdownPreviewTests.OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var fixture = new Fixture();
        test(fixture);
    });

    private static void Await(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(8)) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(task.IsCompleted, "Isolated learning scan did not finish.");
        task.GetAwaiter().GetResult();
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30));
    }

    private static void AwaitIdle(LearningPage page)
    {
        var watch = Stopwatch.StartNew();
        while (page.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(8)) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.False(page.IsBusy);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Window _window;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tuci-idle-refresh-" + Guid.NewGuid().ToString("N"));
        public string NotePath { get; }
        public string ReviewPath { get; }
        public LearningPage Page { get; }
        public Button Skip => (Button)Page.FindName("RoamingSkipButton");
        public Button Remember => (Button)Page.FindName("RoamingRememberButton");
        public Fixture()
        {
            var vault = new VaultOptions(Directory.CreateDirectory(Path.Combine(Root, "vault")).FullName, "notes", false);
            var wordRoot = Directory.CreateDirectory(VaultPaths.WordRoot(vault)).FullName;
            NotePath = Path.Combine(wordRoot, "apple.md");
            File.WriteAllText(NotePath, "# apple\n\n**释义**：\n\n> n. 苹果。\n\nIdle review fixture, created only in a temporary directory.\n");
            var state = Path.Combine(Root, "state");
            Page = new LearningPage(vault, state);
            Page.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
            _window = new Window { Content = Page, Width = 1100, Height = 780, ShowInTaskbar = false };
            _window.Show();
            Await(Page.ReloadAsync());
            Await(Page.ShowRoamingAsync());
            ReviewPath = Directory.GetFiles(state, "roaming.json", SearchOption.AllDirectories).Single();
            Skip.GetBindingExpression(UIElement.IsEnabledProperty)!.UpdateTarget();
            Remember.GetBindingExpression(UIElement.IsEnabledProperty)!.UpdateTarget();
            Assert.True(Page.HasExposure && Skip.IsEnabled && Remember.IsEnabled);
        }
        public void Dispose()
        {
            Page.Dispose(); _window.Close();
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Root);
            if (!target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe temporary fixture cleanup path.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }

    private sealed class ButtonTrace : IDisposable
    {
        private readonly LearningPage _page;
        private readonly Button _skip, _remember;
        public List<(bool Busy, bool CanAdvance, bool Skip, bool Remember, double Opacity)> States { get; } = [];
        public bool AllEnabled => States.All(state => state.Skip && state.Remember);
        public ButtonTrace(LearningPage page)
        {
            _page = page;
            _skip = (Button)page.FindName("RoamingSkipButton");
            _remember = (Button)page.FindName("RoamingRememberButton");
            page.PropertyChanged += Changed;
        }
        private void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(LearningPage.CanAdvance)) return;
            // Flush the same binding state a rendered frame observes during a slower background scan.
            _skip.GetBindingExpression(UIElement.IsEnabledProperty)!.UpdateTarget();
            _remember.GetBindingExpression(UIElement.IsEnabledProperty)!.UpdateTarget();
            States.Add((_page.IsBusy, _page.CanAdvance, _skip.IsEnabled, _remember.IsEnabled, _skip.Opacity));
        }
        public string Describe() => "Idle background refresh must not dim/disable review actions. " + string.Join(" -> ", States.Select(state =>
            $"busy={state.Busy},canAdvance={state.CanAdvance},skip={state.Skip},remember={state.Remember},opacity={state.Opacity}"));
        public void Dispose() => _page.PropertyChanged -= Changed;
    }
}
