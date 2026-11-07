using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;
using Xunit.Abstractions;

namespace VocabCollector.Tests;

[Collection("Learning loading WPF")]
public sealed class LearningLoadingTests(ITestOutputHelper output)
{
    [Fact]
    public void ConcurrentPreparationSharesOneTaskAndDoesNotDrawAReviewCard() => Run(fixture =>
    {
        var first = fixture.Page.EnsureLoadedAsync();
        var second = fixture.Page.EnsureLoadedAsync();
        var shared = ReferenceEquals(first, second);
        Await(Task.WhenAll(first, second));
        Assert.True(shared, "Concurrent callers must await the same library scan.");
        Assert.Single(fixture.Page.AllNotes);
        Assert.False(fixture.Page.HasExposure);
        Assert.Empty(Directory.GetFiles(fixture.State, "roaming.json", SearchOption.AllDirectories));
        Await(fixture.Page.ShowRoamingAsync());
        var review = Directory.GetFiles(fixture.State, "roaming.json", SearchOption.AllDirectories).Single();
        var before = File.ReadAllText(review);
        Await(fixture.Page.EnsureLoadedAsync()); Await(fixture.Page.ShowRoamingAsync());
        Assert.Equal(before, File.ReadAllText(review));
    });

    [Fact]
    public void FreshPreparationReusesTheSnapshotWithoutReadingLockedMarkdown() => Run(fixture =>
    {
        Await(fixture.Page.EnsureLoadedAsync());
        var notes = fixture.Page.AllNotes;
        var busy = new List<bool>();
        fixture.Page.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(LearningPage.IsBusy)) busy.Add(fixture.Page.IsBusy); };
        using var locked = new FileStream(fixture.NotePath, FileMode.Open, FileAccess.Read, FileShare.None);
        Await(fixture.Page.EnsureLoadedAsync());
        Assert.True(ReferenceEquals(notes, fixture.Page.AllNotes), "Repeated preparation unexpectedly replaced the library snapshot.");
        Assert.Single(fixture.Page.AllNotes);
        Assert.Empty(busy);
    });

    [Fact]
    public void ActivationBurstWithinTheFreshnessWindowDoesNotStartAnotherScan() => Run(fixture =>
    {
        Await(fixture.Page.EnsureLoadedAsync());
        var notes = fixture.Page.AllNotes;
        var updates = 0;
        fixture.Page.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(LearningPage.AllNotes)) updates++; };
        for (var index = 0; index < 200; index++) fixture.Page.RequestReload(forceRefresh: false);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(850));
        Assert.Equal(0, updates);
        Assert.Same(notes, fixture.Page.AllNotes);
    });

    [Fact]
    public void ExpiredPreparationChecksTheLibraryAgainButFreshPreparationDoesNot() => Run(fixture =>
    {
        Await(fixture.Page.EnsureLoadedAsync());
        var busy = new List<bool>();
        fixture.Page.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(LearningPage.IsBusy)) busy.Add(fixture.Page.IsBusy); };
        fixture.Clock.Advance(TimeSpan.FromSeconds(29));
        Await(fixture.Page.EnsureLoadedAsync());
        Assert.Empty(busy);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Await(fixture.Page.EnsureLoadedAsync());
        Assert.Contains(true, busy);
        Assert.False(fixture.Page.IsBusy);
    });

    [Fact]
    public void AForceRequestDuringSnapshotApplicationIsIncludedInTheSharedTask() => Run(fixture =>
    {
        Task? first = null, followup = null;
        var updates = 0;
        fixture.Page.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(LearningPage.AllNotes) || updates++ != 0) return;
            File.WriteAllText(Path.Combine(fixture.WordRoot, "banana.md"), "# banana\n\nA new note created after the first snapshot was read.");
            followup = fixture.Page.ReloadAsync();
        };
        first = fixture.Page.EnsureLoadedAsync();
        Await(first);
        Assert.NotNull(followup);
        var shared = ReferenceEquals(first, followup);
        Await(followup!);
        Assert.True(shared, "A forced refresh during a scan must join its completion task.");
        Assert.Contains(fixture.Page.AllNotes, note => note.Title == "banana");
        Assert.False(fixture.Page.HasExposure);
    });

    [Fact]
    public void WatcherChangesAndManualReloadBypassTheFreshnessWindow() => Run(fixture =>
    {
        Await(fixture.Page.EnsureLoadedAsync());
        File.WriteAllText(fixture.NotePath, "# apple\n\nWatcher update must be visible within the TTL.");
        Until(() => fixture.Page.AllNotes.Single().Markdown.Contains("Watcher update", StringComparison.Ordinal));
        File.WriteAllText(fixture.NotePath, "# apple\n\nManual refresh must apply immediately.");
        Await(fixture.Page.ReloadAsync());
        Assert.Contains("Manual refresh", fixture.Page.AllNotes.Single().Markdown);
    });

    [Fact]
    public void AnIncompleteScanCanRetryWithoutWaitingForTheTtl() => Run(fixture =>
    {
        using (var locked = new FileStream(fixture.NotePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Await(fixture.Page.EnsureLoadedAsync());
            Assert.Empty(fixture.Page.AllNotes);
        }
        Await(fixture.Page.EnsureLoadedAsync());
        Assert.Single(fixture.Page.AllNotes);
    });

    [Fact]
    public void DisposedPreparationCannotPublishDataOrNotifications() => Run(fixture =>
    {
        var running = fixture.Page.EnsureLoadedAsync();
        var changes = 0;
        fixture.Page.PropertyChanged += (_, _) => changes++;
        fixture.Page.Dispose();
        Await(running);
        Assert.Empty(fixture.Page.AllNotes);
        Assert.Equal(0, changes);
    });

    [Fact]
    public void RepeatedModulePreparationKeepsTheSnapshotAndReportsItsCost() => Run(fixture =>
    {
        var watch = Stopwatch.StartNew(); Await(fixture.Page.EnsureLoadedAsync()); var cold = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        for (var index = 0; index < 3; index++) Await(fixture.Page.ReloadAsync());
        var forced = watch.Elapsed.TotalMilliseconds;
        var notes = fixture.Page.AllNotes;
        watch.Restart();
        for (var index = 0; index < 10; index++) Await(fixture.Page.EnsureLoadedAsync());
        var prepared = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"400-note fixture: control/window construction {fixture.ConstructionMilliseconds:F1} ms; first preparation {cold:F1} ms; 3 forced scans {forced:F1} ms; 10 normal preparations {prepared:F2} ms.");
        Assert.Same(notes, fixture.Page.AllNotes);
    }, noteCount: 400);

    private static void Run(Action<Fixture> test, int noteCount = 1) => MarkdownPreviewTests.OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var fixture = new Fixture(noteCount); test(fixture);
    });
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(8)) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(5));
        Assert.True(condition(), "Library loading did not complete.");
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tuci-load-cache-" + Guid.NewGuid().ToString("N"));
        public string State { get; }
        public string WordRoot { get; }
        public string NotePath { get; }
        public ManualClock Clock { get; } = new();
        public LearningPage Page { get; }
        public double ConstructionMilliseconds { get; }
        private readonly Window _window;
        public Fixture(int count)
        {
            var vault = new VaultOptions(Directory.CreateDirectory(Path.Combine(Root, "vault")).FullName, "notes", false);
            WordRoot = Directory.CreateDirectory(VaultPaths.WordRoot(vault)).FullName;
            NotePath = Path.Combine(WordRoot, "apple.md"); State = Path.Combine(Root, "state");
            File.WriteAllText(NotePath, "# apple\n\n**释义**：\n\n> n. 苹果。\n");
            for (var index = 1; index < count; index++)
            {
                var title = "word" + (char)('a' + index / 26) + (char)('a' + index % 26);
                var folder = Directory.CreateDirectory(Path.Combine(WordRoot, "folder" + index % 12)).FullName;
                File.WriteAllText(Path.Combine(folder, title + ".md"), "# " + title + "\n\n" + string.Concat(Enumerable.Repeat("A local Markdown sentence for the repeated preparation measurement.\n", 30)));
            }
            var watch = Stopwatch.StartNew();
            Page = new LearningPage(vault, State, Clock);
            Page.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
            _window = new Window { Style = null, Icon = null, Content = Page, Width = 1000, Height = 760, ShowInTaskbar = false };
            _window.Show(); ConstructionMilliseconds = watch.Elapsed.TotalMilliseconds;
        }
        public void Dispose()
        {
            Page.Dispose(); _window.Close();
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe isolated fixture path.");
            Directory.Delete(Root, true);
        }
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan amount) { _now += amount; _timestamp += amount.Ticks; }
    }
}

[CollectionDefinition("Learning loading WPF", DisableParallelization = true)]
public sealed class LearningLoadingCollection;
