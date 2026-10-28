using VocabCollector.Core.Reading;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

public class DailyReadingViewModelTests
{
    [Fact]
    public async Task OpensWithoutAiOrVaultSettingsAndDisplaysCachedDailyArticles()
    {
        var requests = new List<bool>();
        var vm = new DailyReadingViewModel(new ReadingService((refresh, _) =>
        {
            requests.Add(refresh);
            return Task.FromResult(new DailyReadingResult(new(2026, 10, 28),
                [GeneralArticle, TechnologyArticle], true, false, "已读取今日缓存。"));
        }));

        await vm.LoadAsync();

        Assert.Equal([false], requests);
        Assert.Equal([GeneralArticle, TechnologyArticle], vm.Articles);
        Assert.Contains("2026", vm.DayLabel);
        Assert.Contains("10", vm.DayLabel);
        Assert.Contains("28", vm.DayLabel);
        Assert.Contains("缓存", vm.Message);
        Assert.True(vm.IsFromCache);
        Assert.False(vm.IsOffline);
        Assert.False(vm.IsBusy);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshRequestsNewArticlesAndReplacesThePreviousDay()
    {
        var requests = new List<bool>();
        var vm = new DailyReadingViewModel(new ReadingService((refresh, _) =>
        {
            requests.Add(refresh);
            return Task.FromResult(refresh
                ? new DailyReadingResult(new(2026, 10, 29), [TechnologyArticle], false, false, "已更新。")
                : new DailyReadingResult(new(2026, 10, 28), [GeneralArticle], true, false, "今日缓存。"));
        }));

        await vm.LoadAsync();
        await vm.LoadAsync(forceRefresh: true);

        Assert.Equal([false, true], requests);
        Assert.Equal(TechnologyArticle, Assert.Single(vm.Articles));
        Assert.Contains("29", vm.DayLabel);
        Assert.False(vm.IsFromCache);
        Assert.Equal("已更新。", vm.Message);
    }

    [Fact]
    public async Task OverlappingLoadsDoNotStartAnotherRequestAndCommandsReflectBusyState()
    {
        var pending = new TaskCompletionSource<DailyReadingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var vm = new DailyReadingViewModel(new ReadingService(async (_, ct) =>
        {
            requests++;
            return await pending.Task.WaitAsync(ct);
        }));

        var running = vm.LoadAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));
        await vm.LoadAsync();
        await vm.LoadAsync(forceRefresh: true);
        pending.SetResult(new(new(2026, 10, 28), [GeneralArticle], false, false, "已获取。"));
        await running;

        Assert.Equal(1, requests);
        Assert.Equal(GeneralArticle, Assert.Single(vm.Articles));
        Assert.False(vm.IsBusy);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task NetworkFailureKeepsPreviousArticlesRedactsDetailsAndAllowsRetry()
    {
        var requests = 0;
        var vm = new DailyReadingViewModel(new ReadingService((_, _) =>
        {
            requests++;
            if (requests == 2) throw new HttpRequestException("private-network-canary");
            return Task.FromResult(new DailyReadingResult(new(2026, 10, 28),
                [requests == 1 ? GeneralArticle : TechnologyArticle], false, false, "已获取。"));
        }));
        await vm.LoadAsync();

        await vm.LoadAsync(forceRefresh: true);

        Assert.Equal(GeneralArticle, Assert.Single(vm.Articles));
        Assert.Contains("网络", vm.Message);
        Assert.Contains("重试", vm.Message);
        Assert.DoesNotContain("private-network-canary", vm.Message);
        Assert.False(vm.IsBusy);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        await vm.LoadAsync(forceRefresh: true);
        Assert.Equal(TechnologyArticle, Assert.Single(vm.Articles));
    }

    [Fact]
    public async Task CancellationStopsTheRequestPreservesArticlesAndAllowsRetry()
    {
        var requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new DailyReadingViewModel(new ReadingService(async (_, ct) =>
        {
            requests++;
            if (requests == 2)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return new(new(2026, 10, 28), [GeneralArticle], false, false, "已获取。");
        }));
        await vm.LoadAsync();
        var running = vm.LoadAsync(forceRefresh: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        vm.CancelCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(GeneralArticle, Assert.Single(vm.Articles));
        Assert.Contains("取消", vm.Message);
        Assert.False(vm.IsBusy);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        await vm.LoadAsync(forceRefresh: true);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task CancellationWinsWhenAServiceReturnsAfterItsTokenWasCancelled()
    {
        var pending = new TaskCompletionSource<DailyReadingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new DailyReadingViewModel(new ReadingService((_, _) => pending.Task));
        var running = vm.LoadAsync();
        vm.Cancel();
        pending.SetResult(new(new(2026, 10, 28), [GeneralArticle], false, false, "已获取。"));

        await running;

        Assert.Empty(vm.Articles);
        Assert.Contains("取消", vm.Message);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OfflineCachedResultShowsItsExplanationAndRetryRemainsAvailable()
    {
        var vm = new DailyReadingViewModel(new ReadingService((_, _) =>
            Task.FromResult(new DailyReadingResult(new(2026, 10, 27), [GeneralArticle], true, true,
                "网络暂时不可用，正在显示上次缓存，可刷新重试。"))));

        await vm.LoadAsync();

        Assert.True(vm.IsFromCache);
        Assert.True(vm.IsOffline);
        Assert.Contains("27", vm.DayLabel);
        Assert.Contains("上次缓存", vm.Message);
        Assert.Equal(GeneralArticle, Assert.Single(vm.Articles));
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task ActivatingAgainOnTheSameLocalDayKeepsExistingArticlesWithoutAnotherRequest()
    {
        var clock = new ReadingClock(new(2026, 10, 28, 23, 55, 0, TimeSpan.FromHours(8)));
        var requests = 0;
        var vm = new DailyReadingViewModel(new ReadingService((_, _) =>
        {
            requests++;
            return Task.FromResult(new DailyReadingResult(new(2026, 10, 28), [GeneralArticle], true, false, "今日缓存。"));
        }), clock);
        await vm.LoadAsync();
        clock.Advance(TimeSpan.FromMinutes(2));

        await vm.RefreshIfNewDayAsync();
        await vm.RefreshIfNewDayAsync();

        Assert.Equal(1, requests);
        Assert.Equal(GeneralArticle, Assert.Single(vm.Articles));
    }

    [Fact]
    public async Task ActivatingAfterLocalMidnightRequestsTheNewDayWithoutForcingRefresh()
    {
        var clock = new ReadingClock(new(2026, 10, 28, 23, 55, 0, TimeSpan.FromHours(8)));
        var requests = new List<bool>();
        var vm = new DailyReadingViewModel(new ReadingService((refresh, _) =>
        {
            requests.Add(refresh);
            return Task.FromResult(requests.Count == 1
                ? new DailyReadingResult(new(2026, 10, 28), [GeneralArticle], true, false, "今日缓存。")
                : new DailyReadingResult(new(2026, 10, 29), [TechnologyArticle], false, false, "新一天的阅读。"));
        }), clock);
        await vm.LoadAsync();
        clock.Advance(TimeSpan.FromMinutes(10));

        await vm.RefreshIfNewDayAsync();

        Assert.Equal([false, false], requests);
        Assert.Equal(TechnologyArticle, Assert.Single(vm.Articles));
        Assert.Contains("29", vm.DayLabel);
    }

    [Fact]
    public async Task OfflineOldCacheDoesNotRepeatRequestsOnEveryActivationOfTheAttemptedDay()
    {
        var clock = new ReadingClock(new(2026, 10, 28, 23, 55, 0, TimeSpan.FromHours(8)));
        var requests = 0;
        var vm = new DailyReadingViewModel(new ReadingService((_, _) =>
        {
            requests++;
            return Task.FromResult(new DailyReadingResult(new(2026, 10, 27), [GeneralArticle], true, true, "网络不可用，显示上次缓存。"));
        }), clock);
        await vm.LoadAsync();
        await vm.RefreshIfNewDayAsync();
        Assert.Equal(1, requests);
        clock.Advance(TimeSpan.FromMinutes(10));

        await vm.RefreshIfNewDayAsync();
        await vm.RefreshIfNewDayAsync();
        await vm.RefreshIfNewDayAsync();

        Assert.Equal(2, requests);
        Assert.Contains("27", vm.DayLabel);
        Assert.True(vm.IsOffline);
        Assert.Equal(GeneralArticle, Assert.Single(vm.Articles));
    }

    private static ReadingArticle GeneralArticle => new("How communities grow greener",
        "Researchers explore how neighbourhoods support greener habits.", "https://example.com/environment",
        "ScienceDaily", ReadingCategory.General, new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));
    private static ReadingArticle TechnologyArticle => new("Building a faster network",
        "Engineers explain a new approach to improving Internet performance.", "https://blog.cloudflare.com/example/",
        "Cloudflare Blog", ReadingCategory.Technology);

    private sealed class ReadingService(Func<bool, CancellationToken, Task<DailyReadingResult>> get) : IDailyReadingService
    {
        public Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            get(forceRefresh, cancellationToken);
    }

    private sealed class ReadingClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Reading test time", TimeSpan.FromHours(8), "Reading test time", "Reading test time");
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
