using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Reading;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Preview;
using VocabCollector.Desktop.Reading;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class ReadingCapturePaneTests
{
    [Fact]
    public void OpeningPrefillsTheDraftAndClearsOldResultsWithoutCallingCaptureOrChangingTheArticle() => Run(fixture =>
    {
        fixture.CaptureViewModel.Word = "old";
        var seed = fixture.CaptureViewModel.GenerateAsync();
        fixture.Captures.Requests[0].Complete(new(CaptureStatus.Saved, "old", "# OLD PREVIEW", [fixture.Existing], "old result"));
        Await(seed);
        var document = fixture.Reader.Document;
        var offset = fixture.Reader.VerticalOffset;
        fixture.Open(); Pump();
        Assert.True(fixture.Page.IsCapturePreviewOpen);
        Assert.Same(fixture.CaptureViewModel, fixture.Page.CapturePreviewViewModel);
        Assert.Equal("apple", Find<TextBox>(fixture.Page, "ReadingCaptureWordBox").Text);
        Assert.Equal("An apple grows in this paragraph.", Find<TextBox>(fixture.Page, "ReadingCaptureContextBox").Text);
        Assert.Empty(fixture.CaptureViewModel.Markdown);
        Assert.Empty(fixture.CaptureViewModel.Notes);
        Assert.Null(fixture.CaptureViewModel.SelectedNote);
        Assert.Empty(Find<MarkdownPreview>(fixture.Page, "ReadingCapturePreview").Markdown);
        Assert.Single(fixture.Captures.Requests);
        Assert.Equal(0, fixture.Translations.Calls);
        Assert.Same(document, fixture.Reader.Document);
        Assert.InRange(fixture.Reader.VerticalOffset, offset - 2, offset + 2);
        Assert.Equal(fixture.First, fixture.Page.CurrentArticle);
        Assert.Single(Directory.GetFiles(fixture.Vault.Root, "*.md", SearchOption.AllDirectories));
        Assert.Equal(Fixture.ExistingMarkdown, File.ReadAllText(fixture.Existing.AbsolutePath));
    });

    [Theory]
    [InlineData(CaptureStatus.Saved)]
    [InlineData(CaptureStatus.Failed)]
    [InlineData(CaptureStatus.AlreadyExists)]
    public void ExplicitGenerateShowsSavedFailedAndExistingResultsInsideThePane(CaptureStatus status) => Run(fixture =>
    {
        var document = fixture.Reader.Document;
        var offset = fixture.Reader.VerticalOffset;
        fixture.Open();
        Assert.Empty(fixture.Captures.Requests);
        Click(fixture.Page, "ReadingCaptureGenerateButton"); Pump();
        var request = Assert.Single(fixture.Captures.Requests);
        Assert.Equal(new CaptureRequest("apple", "An apple grows in this paragraph."), request.Request);
        Assert.Empty(request.Settings.Api.ApiKey);
        Assert.True(fixture.CaptureViewModel.IsBusy);
        Assert.False(Find<Button>(fixture.Page, "ReadingCaptureGenerateButton").IsEnabled);
        Click(fixture.Page, "ReadingCaptureGenerateButton");
        Assert.Single(fixture.Captures.Requests);
        var markdown = status == CaptureStatus.AlreadyExists ? "" : "# apple\n\n> n. generated apple";
        var expected = status == CaptureStatus.AlreadyExists ? Fixture.ExistingMarkdown : markdown;
        var message = status switch { CaptureStatus.Saved => "已收录 apple。", CaptureStatus.Failed => "保存失败，生成内容已保留。", _ => "apple 已经收录。" };
        request.Complete(new(status, "apple", markdown, status == CaptureStatus.Failed ? [] : [fixture.Existing], message));
        Until(() => !fixture.CaptureViewModel.IsBusy && Find<MarkdownPreview>(fixture.Page, "ReadingCapturePreview").Markdown == expected);
        Assert.Equal(message, Find<TextBlock>(fixture.Page, "ReadingCaptureStatusText").Text);
        Assert.Equal(expected, fixture.CaptureViewModel.Markdown);
        Assert.True(fixture.Page.IsCapturePreviewOpen);
        Assert.Same(document, fixture.Reader.Document);
        Assert.Equal(fixture.First, fixture.Page.CurrentArticle);
        Click(fixture.Page, "CloseReadingCaptureButton"); Pump();
        Assert.False(fixture.Page.IsCapturePreviewOpen);
        Assert.Same(document, fixture.Reader.Document);
        Assert.InRange(fixture.Reader.VerticalOffset, offset - 2, offset + 2);
        Assert.Equal(Fixture.ExistingMarkdown, File.ReadAllText(fixture.Existing.AbsolutePath));
    });

    [Fact]
    public void UnexpectedCaptureFailureKeepsInputAndShowsARetryableMessageInThePane() => Run(fixture =>
    {
        var document = fixture.Reader.Document;
        fixture.Open(); Click(fixture.Page, "ReadingCaptureGenerateButton");
        Assert.Single(fixture.Captures.Requests).Result.SetException(new IOException("private-error-canary"));
        Until(() => !fixture.CaptureViewModel.IsBusy);
        Assert.Equal("apple", fixture.CaptureViewModel.Word);
        Assert.Equal("An apple grows in this paragraph.", fixture.CaptureViewModel.Context);
        var status = Find<TextBlock>(fixture.Page, "ReadingCaptureStatusText").Text;
        Assert.Contains("重试", status);
        Assert.DoesNotContain("private-error-canary", status);
        Assert.True(Find<Button>(fixture.Page, "ReadingCaptureGenerateButton").IsEnabled);
        Assert.True(fixture.Page.IsCapturePreviewOpen);
        Assert.Same(document, fixture.Reader.Document);
    });

    [Theory]
    [InlineData("close")]
    [InlineData("directory")]
    [InlineData("article")]
    [InlineData("leave")]
    [InlineData("cancel")]
    public void DismissingOrLeavingCancelsOnlyThePaneRequestAndRejectsItsLateResult(string action) => Run(fixture =>
    {
        var document = fixture.Reader.Document;
        var offset = fixture.Reader.VerticalOffset;
        fixture.Open(); Click(fixture.Page, "ReadingCaptureGenerateButton");
        var request = Assert.Single(fixture.Captures.Requests);
        Assert.False(request.Token.IsCancellationRequested);
        switch (action)
        {
            case "close": Click(fixture.Page, "CloseReadingCaptureButton"); break;
            case "directory": fixture.Page.ReturnToDirectory(); break;
            case "article": Await(fixture.Page.OpenArticleAsync(fixture.Second)); break;
            case "leave": fixture.Page.Cancel(); break;
            case "cancel":
                var button = Find<Button>(fixture.Page, "ReadingCaptureCancelButton");
                Assert.True(button.IsVisible);
                var peer = Assert.IsType<ButtonAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(button));
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
                break;
        }
        Until(() => request.Token.IsCancellationRequested && !fixture.CaptureViewModel.IsBusy);
        request.Complete(new(CaptureStatus.Saved, "apple", "# LATE-CAPTURE-CANARY", [fixture.Existing], "late"));
        Pump();
        Assert.Equal(action == "cancel", fixture.Page.IsCapturePreviewOpen);
        Assert.DoesNotContain("LATE-CAPTURE-CANARY", fixture.CaptureViewModel.Markdown);
        Assert.DoesNotContain("LATE-CAPTURE-CANARY", Find<MarkdownPreview>(fixture.Page, "ReadingCapturePreview").Markdown);
        if (action == "article")
        {
            Assert.Equal(fixture.Second, fixture.Page.CurrentArticle);
            Assert.Contains("NEW ARTICLE BODY", Body(fixture.Reader));
        }
        else
        {
            Assert.Same(document, fixture.Reader.Document);
            Assert.InRange(fixture.Reader.VerticalOffset, offset - 2, offset + 2);
        }
        Assert.Single(fixture.Captures.Requests);
        Assert.Equal(Fixture.ExistingMarkdown, File.ReadAllText(fixture.Existing.AbsolutePath));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyMainViewModelCannotBeOverwrittenByAnotherReadingWord(bool startedInPane) => Run(fixture =>
    {
        Task? external = null;
        if (startedInPane) { fixture.Open(); Click(fixture.Page, "ReadingCaptureGenerateButton"); }
        else
        {
            fixture.CaptureViewModel.Word = "apple";
            fixture.CaptureViewModel.Context = "original pending context";
            external = fixture.CaptureViewModel.GenerateAsync();
        }
        var request = Assert.Single(fixture.Captures.Requests);
        var context = fixture.CaptureViewModel.Context;
        fixture.Page.OpenCapturePreview(fixture.CaptureViewModel, new("banana", "must not replace pending input")); Pump();
        Assert.Equal("apple", fixture.CaptureViewModel.Word);
        Assert.Equal(context, fixture.CaptureViewModel.Context);
        Assert.Equal(startedInPane, fixture.Page.IsCapturePreviewOpen);
        Assert.Single(fixture.Captures.Requests);
        Assert.Contains("等待", fixture.Page.ViewModel.Message);
        fixture.Page.CloseCapturePreview();
        Assert.Equal(startedInPane, request.Token.IsCancellationRequested);
        if (external is not null) { fixture.CaptureViewModel.CancelCommand.Execute(null); Await(external); }
        Until(() => !fixture.CaptureViewModel.IsBusy);
    });

    // Share the same test-only Application/Dispatcher as the article and pointer
    // tests. A second Application would fail when these classes run together.
    private static void Run(Action<Fixture> test) => ((Lazy<Dispatcher>)typeof(ReadingPageTests)
        .GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(() =>
        { using var fixture = new Fixture(); test(fixture); });
    private static T Find<T>(ReadingPage page, string name) where T : class => Assert.IsAssignableFrom<T>(page.FindName(name));
    private static void Click(ReadingPage page, string name) => Find<Button>(page, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Body(ArticleTextReader reader) => new TextRange(reader.Document.ContentStart, reader.Document.ContentEnd).Text;
    private static void Pump() => MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(25));
    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) Pump();
        Assert.True(condition(), "Reading capture pane did not reach its expected state.");
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); Pump(); }

    private sealed class Fixture : IDisposable
    {
        public const string ExistingMarkdown = "# apple\n\n> n. 原有苹果笔记\n\n手写补充必须保留。";
        public TempVault Vault { get; } = new();
        public NoteLocation Existing { get; }
        public ReadingArticle First { get; } = new("First article", "SUMMARY IS NOT BODY", "https://www.sciencedaily.com/releases/capture-fixture.htm", "ScienceDaily", ReadingCategory.General);
        public ReadingArticle Second { get; } = new("Second article", "SECOND SUMMARY", "https://blog.cloudflare.com/capture-fixture/", "Cloudflare", ReadingCategory.Technology);
        public ControlledCapture Captures { get; } = new();
        public NoTranslations Translations { get; } = new();
        public MainViewModel CaptureViewModel { get; }
        public ReadingPage Page { get; }
        public ArticleTextReader Reader { get; }
        private readonly Window _owner;
        public Fixture()
        {
            Existing = Vault.Note("英语单词/apple.md", ExistingMarkdown);
            CaptureViewModel = new MainViewModel(Captures, AppSettings.Default with { Vault = Vault.Options });
            var daily = new DailyReadingViewModel(new Daily([First, Second])); Await(daily.LoadAsync());
            Page = new ReadingPage(daily, _ => throw new InvalidOperationException("No external navigation in capture tests"), new Content(), Translations);
            _owner = new Window { Style = null, Icon = null, Content = Page, Width = 1000, Height = 760, ShowInTaskbar = false };
            _owner.Show(); Await(Page.OpenArticleAsync(First));
            Reader = Find<ArticleTextReader>(Page, "ArticleBodyReader");
            Reader.ScrollToVerticalOffset(280); Pump();
            Assert.True(Reader.VerticalOffset > 100);
        }
        public void Open() => Page.OpenCapturePreview(CaptureViewModel, new("apple", "An apple grows in this paragraph."));
        public void Dispose()
        {
            Page.Dispose(); CaptureViewModel.CancelCommand.Execute(null);
            foreach (var request in Captures.Requests) request.Result.TrySetCanceled();
            Until(() => !CaptureViewModel.IsBusy);
            Pump(); _owner.Close(); Pump(); Vault.Dispose();
        }
    }
    private sealed class Daily(IReadOnlyList<ReadingArticle> articles) : IDailyReadingService
    {
        public Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyReadingResult(DateOnly.FromDateTime(DateTime.Now), articles, false, false, "isolated capture test"));
    }
    private sealed class Content : IArticleContentService
    {
        public Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new ArticleContent(article,
                article.Title == "First article" ? Enumerable.Range(0, 70).Select(index => $"Paragraph {index}. An apple grows in this paragraph. Reading continues with selectable plain English text.").ToArray()
                : ["NEW ARTICLE BODY. A banana grows."], DateTimeOffset.UnixEpoch));
    }
    private sealed class NoTranslations : IWordTranslationService
    {
        public int Calls { get; private set; }
        public Task<WordTranslation> TranslateAsync(string word, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Capture must never automatically translate."); }
    }
    private sealed class ControlledCapture : ICaptureService
    {
        public List<CaptureCall> Requests { get; } = [];
        public Task<CaptureResult> CaptureAsync(CaptureRequest request, AppSettings settings, IProgress<CaptureStage>? progress, CancellationToken ct)
        {
            var call = new CaptureCall(request, settings, ct); Requests.Add(call);
            return call.Result.Task.WaitAsync(ct);
        }
    }
    private sealed record CaptureCall(CaptureRequest Request, AppSettings Settings, CancellationToken Token)
    {
        public TaskCompletionSource<CaptureResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(CaptureResult result) => Result.TrySetResult(result);
    }
}
