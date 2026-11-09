using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

public sealed class ThemeRaceTests
{
    [Fact]
    public void AnOldAccountsDelayedSaveDoesNotPublishIntoTheNewAccount() => OnUi(() =>
    {
        using var fixture = new Fixture();
        var first = new AppearanceSettingsStore(Path.Combine(fixture.Directory, "alice"));
        var second = new AppearanceSettingsStore(Path.Combine(fixture.Directory, "bob"));
        Await(first.SaveAsync(AppTheme.WarmNeutral)); Await(second.SaveAsync(AppTheme.Mono));
        var manager = new ThemeManager(new ResourceDictionary(), first);
        var published = new List<AppTheme>(); manager.PreferenceSaved += (_, theme) => published.Add(theme);
        var saveLock = SaveLock(first); saveLock.Wait();
        Task<bool>? pending = null;
        try
        {
            pending = manager.SetThemeAsync(AppTheme.Pink);
            Assert.False(pending.IsCompleted); Assert.True(manager.HasPendingPreferenceSave);
            Await(manager.SwitchStoreAsync(second));
            Assert.Equal(AppTheme.Mono, manager.CurrentTheme);
        }
        finally { saveLock.Release(); }
        Await(pending!);
        Assert.True(pending!.Result);
        Assert.Empty(published);
        Assert.Equal(AppTheme.Mono, manager.CurrentTheme);
        Assert.False(manager.HasPendingPreferenceSave);
        var firstTheme = first.LoadAsync(); Await(firstTheme); Assert.Equal(AppTheme.Pink, firstTheme.Result);
        var secondTheme = second.LoadAsync(); Await(secondTheme); Assert.Equal(AppTheme.Mono, secondTheme.Result);
    });

    [Fact]
    public void ALocalChoiceWinsOverAnAlreadyWaitingRemoteSaveWithoutRemoteEcho() => OnUi(() =>
    {
        using var fixture = new Fixture();
        var store = new AppearanceSettingsStore(fixture.Directory);
        Await(store.SaveAsync(AppTheme.WarmNeutral));
        var manager = new ThemeManager(new ResourceDictionary(), store);
        var published = new List<AppTheme>(); manager.PreferenceSaved += (_, theme) => published.Add(theme);
        var saveLock = SaveLock(store); saveLock.Wait();
        Task? remote = null; Task<bool>? local = null;
        try
        {
            remote = manager.ApplyRemoteAsync(AppTheme.Mono);
            Assert.False(remote.IsCompleted);
            local = manager.SetThemeAsync(AppTheme.Pink);
            Assert.False(local.IsCompleted);
            Assert.Equal(AppTheme.Pink, manager.CurrentTheme);
            Assert.True(manager.HasPendingPreferenceSave);
        }
        finally { saveLock.Release(); }
        Await(Task.WhenAll(remote!, local!));
        Assert.True(local!.Result);
        Assert.Equal(AppTheme.Pink, manager.CurrentTheme);
        Assert.Equal(new[] { AppTheme.Pink }, published);
        Assert.False(manager.HasPendingPreferenceSave);
        var stored = store.LoadAsync(); Await(stored); Assert.Equal(AppTheme.Pink, stored.Result);
    });

    [Fact]
    public void ARemoteThemeDoesNotQueueBehindAnUnfinishedLocalChoice() => OnUi(() =>
    {
        using var fixture = new Fixture();
        var store = new AppearanceSettingsStore(fixture.Directory);
        Await(store.SaveAsync(AppTheme.WarmNeutral));
        var manager = new ThemeManager(new ResourceDictionary(), store);
        var published = new List<AppTheme>(); manager.PreferenceSaved += (_, theme) => published.Add(theme);
        var saveLock = SaveLock(store); saveLock.Wait();
        Task<bool>? local = null;
        try
        {
            local = manager.SetThemeAsync(AppTheme.Pink);
            Assert.False(local.IsCompleted);
            var remote = manager.ApplyRemoteAsync(AppTheme.Mono);
            Assert.True(remote.IsCompletedSuccessfully);
            Assert.Equal(AppTheme.Pink, manager.CurrentTheme);
        }
        finally { saveLock.Release(); }
        Await(local!);
        Assert.Equal(new[] { AppTheme.Pink }, published);
        Assert.Equal(AppTheme.Pink, manager.CurrentTheme);
        var stored = store.LoadAsync(); Await(stored); Assert.Equal(AppTheme.Pink, stored.Result);
    });

    private static SemaphoreSlim SaveLock(AppearanceSettingsStore store) => (SemaphoreSlim)typeof(AppearanceSettingsStore)
        .GetField("saveLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private static void OnUi(Action action) => MarkdownPreviewTests.OnSta(() =>
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private static void Await(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(5));
        Assert.True(task.IsCompleted, "The controlled theme operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "tuci-theme-race-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
