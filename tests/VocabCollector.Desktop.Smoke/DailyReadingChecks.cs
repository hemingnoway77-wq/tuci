using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Core.Reading;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    private async Task CheckDailyReadingAsync(MainWindow main, RecordingReadingService service,
        Func<string?> openedArticle, string output, Func<int> modelRequests,
        RecordingArticleContentService content, RecordingWordTranslationService translations,
        MainViewModel capture, ThemeManager themes)
    {
        var before = modelRequests();
        await SelectPageAsync(main, "ReadingTab");
        await Until(() => Descendants<ReadingPage>(main).Any(page => page.IsVisible && !((DailyReadingViewModel)page.DataContext).IsBusy));
        var page = Descendants<ReadingPage>(main).Single(item => item.IsVisible);
        var viewModel = (DailyReadingViewModel)page.DataContext;
        await Until(() => viewModel.Articles.Count == 3);

        Check(ReferenceEquals(Window.GetWindow(page), main) && page.IsVisible, "daily reading is a page inside the main window");
        Check(app.Windows.OfType<Window>().Count(item => item.IsVisible) == 1, "daily reading opens no extra window");
        Check(modelRequests() == before, "reading works without API settings and makes zero model requests");
        Check(service.Calls == 1 && !service.LastForced, "first opening loads daily articles");

        await SelectPageAsync(main, "CaptureTab");
        Check(!page.IsVisible, "leaving the page hides the reading surface");
        await SelectPageAsync(main, "ReadingTab");
        Check(page.IsVisible && ReferenceEquals(page, Descendants<ReadingPage>(main).Single(item => item.IsVisible))
            && service.Calls == 1 && viewModel.Articles.Count == 3,
            "returning to reading reuses the loaded page and articles");

        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CheckReadingDirectory(page);
        Snapshot(main, Path.Combine(output, "daily-reading.png"));
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        main.UpdateLayout();
        var refresh = Descendants<Button>(page).Single(button => button.Name == "DailyReadingRefreshButton");
        CheckReachable(refresh, main, "refresh remains reachable at minimum reading size");
        var scroll = Descendants<ScrollViewer>(page).Single(viewer => viewer.Name == "ReadingArticlesScroll");
        Check(AutoHideScrollbars.GetIsEnabled(scroll), "reading uses shared auto-hiding scrollbars");
        scroll.ScrollToEnd();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var openButton = Descendants<Button>(page).Where(button => button.Name == "OpenArticleButton").Last();
        CheckReachable(openButton, main, "last article remains reachable by scrolling");
        Check(scroll.VerticalOffset > 0, "minimum directory fixture exercises a real reading-list scroll position");
        var directoryOffset = scroll.VerticalOffset;
        Snapshot(main, Path.Combine(output, "daily-reading-minimum.png"));
        Invoke(openButton);
        await Until(() => page.IsArticleOpen && page.CurrentArticle?.Url == service.Articles[^1].Url &&
            ReadingControl<System.Windows.Controls.RichTextBox>(page, "ArticleBodyReader").Document.Blocks.Count == content.Paragraphs.Count);
        await CheckPureReadingArticleAsync(main, page, service.Articles[^1], content, translations, capture,
            themes, openedArticle, output, modelRequests);
        Check(!page.IsArticleOpen && Math.Abs(scroll.VerticalOffset - directoryOffset) <= 3,
            "returning from full text restores the article directory's previous scroll position");
        Check(modelRequests() == before, "pure full-text reading and word actions use no model requests");

        Invoke(refresh);
        await Until(() => service.Calls == 2 && !viewModel.IsBusy);
        Check(service.LastForced, "manual refresh asks for fresh feeds");

        service.BlockRefresh = true;
        Invoke(refresh);
        await Until(() => service.Calls == 3 && viewModel.IsBusy);
        await SelectPageAsync(main, "CaptureTab");
        await Until(() => service.Cancelled && !viewModel.IsBusy);
        Check(modelRequests() == before, "refresh and cancellation never call the model");
        service.BlockRefresh = false;

        main.Width = 1140;
        main.Height = 800;
        await SelectPageAsync(main, "ReadingTab");
        await Until(() => page.IsVisible && !viewModel.IsBusy);
        Check(viewModel.Articles.Count == 3, "canceled refresh keeps the articles already loaded");
        await SelectPageAsync(main, "CaptureTab");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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
            return new(new DateOnly(2026, 10, 28), Articles, false, false, "今日文章已准备好，点击即可阅读纯净全文。");
        }
    }
}
