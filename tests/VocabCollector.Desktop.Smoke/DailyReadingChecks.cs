using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Core.Reading;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    private async Task CheckDailyReadingAsync(MainWindow main, RecordingReadingService service,
        Func<string?> openedArticle, string output, Func<int> modelRequests)
    {
        var before = modelRequests();
        Invoke(Control<Button>(main, "DailyReadingButton"));
        await Until(() => app.Windows.OfType<DailyReadingWindow>().Any(window => window.IsVisible));
        var window = app.Windows.OfType<DailyReadingWindow>().Single(window => window.IsVisible);
        var vm = (DailyReadingViewModel)window.DataContext;
        await Until(() => !vm.IsBusy && vm.Articles.Count == 3);
        Check(modelRequests() == before, "reading works without API settings and makes zero model requests");
        Check(window.Icon != null, "reading window shares the application icon");
        Check(service.Calls == 1 && !service.LastForced, "first opening loads daily articles");
        Invoke(Control<Button>(main, "DailyReadingButton"));
        Check(app.Windows.OfType<DailyReadingWindow>().Count(item => item.IsVisible) == 1 && service.Calls == 1,
            "repeated entry reuses the open reading window");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var openButton = Descendants<Button>(window).First(button => button.Name == "OpenArticleButton");
        Invoke(openButton);
        await Until(() => openedArticle() is not null);
        Check(openedArticle() == service.Articles[0].Url, "article button opens the original URL");
        Check(modelRequests() == before, "opening original article uses no model requests");
        Snapshot(window, Path.Combine(output, "daily-reading.png"));
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(Control<Button>(window, "DailyReadingRefreshButton").IsVisible, "refresh remains reachable at minimum reading size");
        var scroll = Descendants<ScrollViewer>(window).First();
        Check(AutoHideScrollbars.GetIsEnabled(scroll), "reading uses shared auto-hiding scrollbars");
        scroll.ScrollToEnd();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(Descendants<Button>(window).Where(button => button.Name == "OpenArticleButton").Last().IsVisible,
            "last article remains reachable by scrolling");
        Snapshot(window, Path.Combine(output, "daily-reading-minimum.png"));
        Invoke(Control<Button>(window, "DailyReadingRefreshButton"));
        await Until(() => service.Calls == 2 && !vm.IsBusy);
        Check(service.LastForced, "manual refresh asks for fresh feeds");
        service.BlockRefresh = true;
        Invoke(Control<Button>(window, "DailyReadingRefreshButton"));
        await Until(() => service.Calls == 3 && vm.IsBusy);
        window.Close();
        await Until(() => service.Cancelled && !vm.IsBusy);
        Check(modelRequests() == before, "refresh and cancellation never call the model");
        service.BlockRefresh = false;
        Invoke(Control<Button>(main, "DailyReadingButton"));
        await Until(() => app.Windows.OfType<DailyReadingWindow>().Any(item => item.IsVisible));
        window = app.Windows.OfType<DailyReadingWindow>().Single(item => item.IsVisible);
        await Until(() => !((DailyReadingViewModel)window.DataContext).IsBusy);
        window.Close();
    }

    private sealed class RecordingReadingService : IDailyReadingService
    {
        public int Calls { get; private set; }
        public bool LastForced { get; private set; }
        public bool BlockRefresh { get; set; }
        public bool Cancelled { get; private set; }
        public IReadOnlyList<ReadingArticle> Articles { get; } =
        [
            new("How small habits shape the way we learn", "Researchers are exploring how small, regular activities help people build lasting knowledge. The findings offer a useful way to think about learning and the choices we make each day.", "https://www.sciencedaily.com/releases/2026/10/example-learning.htm", "ScienceDaily · Society", ReadingCategory.General),
            new("A new perspective on our changing environment", "Scientists are looking at the connections between people, cities and the natural world. Their work shows why understanding these relationships matters for the future.", "https://www.sciencedaily.com/releases/2026/10/example-environment.htm", "ScienceDaily · Environment", ReadingCategory.General),
            new("Building a more reliable web, one request at a time", "A practical look at how engineers improve the reliability of online services, from handling unexpected failures to making everyday requests faster and easier to understand.", "https://blog.cloudflare.com/example-reliability/", "Cloudflare Blog", ReadingCategory.Technology)
        ];
        public async Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            Calls++; LastForced = forceRefresh;
            if (forceRefresh && BlockRefresh)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return new(new DateOnly(2026, 10, 28), Articles, false, false, "今日文章已准备好，点击即可阅读原文。");
        }
    }
}
