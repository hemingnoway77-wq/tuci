using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core.Reading;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Reading;

namespace VocabCollector.Tests;

[CollectionDefinition("ReadingPage STA", DisableParallelization = true)]
public sealed class ReadingPageTestCollection;

[Collection("ReadingPage STA")]
public sealed class ReadingPageTests
{
    [Fact]
    public void CancelledModuleNavigationCannotRestartAnArticleAfterPendingFeedLoading() => Run(fixture =>
    {
        var feeds = new PendingDailyFixture();
        var content = new ControlledContent();
        using var page = new ReadingPage(new DailyReadingViewModel(feeds), articleContentService: content,
            wordTranslationService: new ControlledTranslations());
        var opening = page.OpenArticleAsync(fixture.First);
        page.Cancel();
        content.Requests[0].Complete("A cancelled article response must be ignored.");
        Await(opening);
        using var navigation = new CancellationTokenSource();
        var returning = page.EnsureLoadedAsync(navigation.Token);
        navigation.Cancel(); page.Cancel();
        feeds.Result.SetResult(new(DateOnly.FromDateTime(DateTime.Now), [fixture.First], false, false, "Local fixture"));
        Until(() => returning.IsCompleted || content.Requests.Count > 1);
        Assert.Single(content.Requests);
        Await(returning);
    });

    [Fact]
    public void ArticleAppearsImmediatelyWithoutASecondEntranceAndPreservesSelectionAndScrollPosition() => Run(fixture =>
    {
        fixture.Load(fixture.First, Enumerable.Repeat("An apple remains selectable while this article opens.", 60).ToArray());
        var surface = Find<Grid>(fixture.Page, "ArticleSurface");
        Assert.False(surface.HasAnimatedProperties); Assert.True(surface.RenderTransform.Value.IsIdentity); Assert.Equal(1, surface.Opacity);
        var reader = Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader");
        var document = reader.Document; var word = WordRun(reader, "apple");
        reader.Selection.Select(word.ContentStart, word.ContentEnd); reader.ScrollToVerticalOffset(180);
        Until(() => reader.VerticalOffset > 100);
        Assert.Same(document, reader.Document); Assert.Equal("apple", reader.Selection.Text);
        Assert.False(surface.HasAnimatedProperties); Assert.True(surface.RenderTransform.Value.IsIdentity); Assert.Equal(1, surface.Opacity);
    });

    [Fact]
    public void ArticleUsesAFullWidthViewportAndReturnsWithoutPendingVisualMotion() => Run(fixture =>
    {
        fixture.Load(fixture.First, Enumerable.Repeat("An apple remains selectable while this article opens.", 40).ToArray());
        var reader = Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader");
        Assert.True(double.IsPositiveInfinity(reader.MaxWidth), "The text column must not constrain the scrollbar viewport.");
        var surface = Find<Grid>(fixture.Page, "ArticleSurface");
        var document = reader.Document;
        fixture.Page.ReturnToDirectory();
        Assert.False(surface.HasAnimatedProperties);
        Assert.True(surface.RenderTransform.Value.IsIdentity); Assert.Equal(1, surface.Opacity);
        Assert.Same(document, reader.Document);
    });

    [Fact]
    public void ALongArticleBuildsPlainParagraphsAndLazilyActivatesTheSelectedOccurrence() => Run(fixture =>
    {
        var paragraph = "An apple fell. Another apple grew nearby!";
        fixture.Load(fixture.First, Enumerable.Repeat(paragraph, 180).ToArray());
        var reader = Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader");
        var document = reader.Document;
        Assert.Equal(180, document.Blocks.Count);
        Assert.All(document.Blocks.OfType<Paragraph>(), p => Assert.IsType<Run>(Assert.Single(p.Inlines.Cast<Inline>())));
        var run = Assert.IsType<Run>(Assert.IsType<Paragraph>(document.Blocks.FirstBlock).Inlines.FirstInline);
        var offset = paragraph.LastIndexOf("apple", StringComparison.Ordinal);
        reader.Selection.Select(run.ContentStart.GetPositionAtOffset(offset), run.ContentStart.GetPositionAtOffset(offset + 5));
        ReadingWordSelection? captured = null; fixture.Page.WordCaptureRequested += (_, selection) => captured = selection;
        reader.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(reader)!, Environment.TickCount, Key.Enter)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Pump();
        Assert.True(fixture.Page.WordActionsPopup.IsOpen); Assert.Equal("apple", reader.Selection.Text);
        Click(fixture.Page, "ReadingCaptureButton");
        Assert.Equal(new ReadingWordSelection("apple", "Another apple grew nearby!"), captured);
        Assert.Same(document, reader.Document);
        Assert.True(document.Blocks.OfType<Paragraph>().First().Inlines.Count <= 3,
            "Only the used word may split its paragraph's text run; the other words stay plain text.");
    });

    [Fact]
    public void ReturningToTheSameArticleReusesItsDocumentWithoutRefetching() => Run(fixture =>
    {
        fixture.Load(fixture.First, Enumerable.Repeat("An apple remains in a retained document.", 80).ToArray());
        var reader = Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader"); var document = reader.Document;
        reader.ScrollToVerticalOffset(180); Until(() => reader.VerticalOffset > 100);
        fixture.Page.ReturnToDirectory();
        Await(fixture.Page.OpenArticleAsync(fixture.First)); Pump();
        Assert.Single(fixture.Content.Requests); Assert.Same(document, reader.Document);
        Assert.True(reader.VerticalOffset > 100);
    });

    [Fact]
    public void LongArticleWordHitTestingUsesGlyphBoundsAndRepeatedUseRetainsItsDocument() => Run(fixture =>
    {
        const string text = "An apple fell. Another apple grew nearby!";
        fixture.Load(fixture.First, Enumerable.Repeat(text, 180).ToArray());
        var reader = Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader");
        var document = reader.Document; var paragraph = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
        var first = Assert.IsType<Run>(paragraph.Inlines.FirstInline);
        var start = text.LastIndexOf("apple", StringComparison.Ordinal);
        var bounds = first.ContentStart.GetPositionAtOffset(start + 1)!.GetCharacterRect(LogicalDirection.Forward);
        var next = first.ContentStart.GetPositionAtOffset(start + 2)!.GetCharacterRect(LogicalDirection.Forward);
        var point = new Point((bounds.Left + next.Left) / 2, bounds.Top + bounds.Height / 2);
        var find = typeof(ArticleTextReader).GetMethod("FindPlainWord", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var activate = typeof(ArticleTextReader).GetMethod("ActivatePlainWord", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        for (var i = 0; i < 12; i++)
        {
            var hit = find.Invoke(reader, [point]);
            Assert.NotNull(hit); activate.Invoke(reader, [hit]); Pump();
            Assert.True(fixture.Page.WordActionsPopup.IsOpen);
            Assert.Equal("apple", Find<TextBlock>(fixture.Page, "ReadingSelectedWord").Text);
            fixture.Page.WordActionsPopup.IsOpen = false; Pump();
        }
        Assert.Same(document, reader.Document);
        Assert.Equal(180, document.Blocks.Count);
        Assert.True(paragraph.Inlines.Count <= 3, "Repeatedly highlighting one word cannot grow the document tree.");
        var whitespace = first.ContentStart.GetPositionAtOffset(2)!.GetCharacterRect(LogicalDirection.Forward);
        var whitespaceEnd = first.ContentStart.GetPositionAtOffset(3)!.GetCharacterRect(LogicalDirection.Forward);
        Assert.Null(find.Invoke(reader, [new Point((whitespace.Left + whitespaceEnd.Left) / 2, whitespace.Top + whitespace.Height / 2)]));
    });

    [Fact]
    public void DefaultTranslationIsOfflineAndDoesNotStartBrowserBeforeARequest() => Run(fixture =>
    {
        using var page = new ReadingPage(fixture.Page.ViewModel, articleContentService: fixture.Content);
        var field = typeof(ReadingPage).GetField("_translationService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal("OfflineDictionaryWordTranslationService", field.GetValue(page)!.GetType().Name);
        Assert.Empty(Find<Grid>(page, "TranslationRuntimeHost").Children);
    });

    [Fact]
    public void DictionaryDetailsUseTheNativePopupAndClearWhenTheSelectedWordChanges() => Run(fixture =>
    {
        fixture.Load(fixture.First, "A book is on the table. A banana is nearby.");
        SelectWord(fixture.Page, "book");
        Click(fixture.Page, "ReadingTranslateButton");
        fixture.Translations.Requests[0].Result.SetResult(new WordTranslation("book", "n. 书；书籍\nvt. 预订；登记")
        {
            Phonetic = "bʊk", Meanings = [new("n.", "书；书籍"), new("vt.", "预订；登记")]
        });
        Until(() => Find<Button>(fixture.Page, "ReadingTranslateButton").IsEnabled);
        var meanings = Find<ItemsControl>(fixture.Page, "WordMeaningsList");
        Assert.Equal(2, meanings.Items.Count);
        Assert.Equal("n.", Assert.IsType<WordMeaning>(meanings.Items[0]).PartOfSpeech);
        Assert.Equal("预订；登记", Assert.IsType<WordMeaning>(meanings.Items[1]).Definition);
        Assert.Contains("bʊk", Find<TextBlock>(fixture.Page, "WordPhoneticText").Text);
        Assert.Equal(Visibility.Visible, Find<ScrollViewer>(fixture.Page, "WordDetailsScroll").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<TextBlock>(fixture.Page, "WordTranslationText").Visibility);
        Assert.True(fixture.Page.WordActionsPopup.IsOpen);

        SelectWord(fixture.Page, "banana");
        Assert.Empty(meanings.Items);
        Assert.Equal(Visibility.Collapsed, Find<TextBlock>(fixture.Page, "WordPhoneticText").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<ScrollViewer>(fixture.Page, "WordDetailsScroll").Visibility);
        Click(fixture.Page, "ReadingTranslateButton");
        fixture.Translations.Requests[1].Result.SetResult(new("banana", "香蕉"));
        Until(() => Find<TextBlock>(fixture.Page, "WordTranslationText").Text == "香蕉");
        Assert.Equal(Visibility.Visible, Find<TextBlock>(fixture.Page, "WordTranslationText").Visibility);
        Assert.Empty(meanings.Items);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateArticleCannotReopenDirectoryOrReplaceTheNewArticle(bool openAnother) => Run(fixture =>
    {
        var old = fixture.Page.OpenArticleAsync(fixture.First);
        var request = Assert.Single(fixture.Content.Requests);
        fixture.Page.ReturnToDirectory();
        Assert.True(request.Token.IsCancellationRequested);
        Task? newer = null;
        if (openAnother)
        {
            newer = fixture.Page.OpenArticleAsync(fixture.Second);
            fixture.Content.Requests[1].Complete("The newer article must remain visible.");
            Await(newer);
        }
        request.Complete("The late old article must never reappear.");
        Await(old);
        if (openAnother)
        {
            Assert.Equal(fixture.Second, fixture.Page.CurrentArticle);
            Assert.Contains("newer article", Body(fixture.Page));
            Assert.DoesNotContain("late old", Body(fixture.Page));
            Assert.Equal(fixture.Second.Title, Find<TextBlock>(fixture.Page, "ArticleTitleText").Text);
        }
        else
        {
            Assert.False(fixture.Page.IsArticleOpen);
            Assert.Equal(Visibility.Visible, Find<Grid>(fixture.Page, "ReadingDirectory").Visibility);
            Assert.Equal(Visibility.Collapsed, Find<Grid>(fixture.Page, "ArticleSurface").Visibility);
            Assert.DoesNotContain("late old", Body(fixture.Page));
        }
    });

    [Fact]
    public void LeavingAndReturningKeepsLoadedDocumentAndReadingPosition() => Run(fixture =>
    {
        fixture.Load(fixture.First, Enumerable.Repeat("An apple is worth reading about in this long, selectable paragraph.", 70).ToArray());
        var reader = Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader");
        var document = reader.Document;
        reader.ScrollToVerticalOffset(280); Pump();
        var offset = reader.VerticalOffset;
        Assert.True(offset > 100);
        fixture.Page.Cancel();
        fixture.Page.Visibility = Visibility.Collapsed; Pump();
        fixture.Page.Visibility = Visibility.Visible; Pump();
        Await(fixture.Page.EnsureLoadedAsync());
        Assert.Equal(fixture.First, fixture.Page.CurrentArticle);
        Assert.Same(document, reader.Document);
        Assert.InRange(reader.VerticalOffset, offset - 2, offset + 2);
        Assert.Single(fixture.Content.Requests);
    });

    [Fact]
    public void FailedFullTextIsExplicitAndRetryNeverPresentsTheSummaryAsBody() => Run(fixture =>
    {
        var load = fixture.Page.OpenArticleAsync(fixture.First);
        fixture.Content.Requests[0].Result.SetException(new ArticleContentException("fixture failure"));
        Await(load);
        Assert.Equal("全文暂未载入", Find<TextBlock>(fixture.Page, "ArticleStatusText").Text);
        Assert.Equal(Visibility.Visible, Find<Button>(fixture.Page, "RetryArticleButton").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader").Visibility);
        Assert.DoesNotContain(fixture.First.Summary, Body(fixture.Page));
        Assert.Contains("没有用摘要替代全文", fixture.Page.ViewModel.Message);
        Click(fixture.Page, "RetryArticleButton");
        Until(() => fixture.Content.Requests.Count == 2);
        Assert.True(fixture.Content.Requests[1].ForceRefresh);
        fixture.Content.Requests[1].Complete("The actual full text is visible after retry.");
        Until(() => Body(fixture.Page).Contains("actual full text", StringComparison.Ordinal));
        Assert.Equal(Visibility.Visible, Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader").Visibility);
        Assert.Equal(Visibility.Collapsed, Find<StackPanel>(fixture.Page, "ArticleLoadState").Visibility);
        Assert.DoesNotContain(fixture.First.Summary, Body(fixture.Page));
    });

    [Fact]
    public void LateTranslationCannotReplaceTheNewSelectedWordsTranslation() => Run(fixture =>
    {
        fixture.Load(fixture.First, "An apple grows. A banana ripens.");
        SelectWord(fixture.Page, "apple");
        Click(fixture.Page, "ReadingTranslateButton");
        var old = Assert.Single(fixture.Translations.Requests);
        SelectWord(fixture.Page, "banana");
        Assert.True(old.Token.IsCancellationRequested);
        Click(fixture.Page, "ReadingTranslateButton");
        Assert.Equal("banana", fixture.Translations.Requests[1].Word);
        fixture.Translations.Requests[1].Result.SetResult(new("banana", "香蕉"));
        Until(() => Find<TextBlock>(fixture.Page, "WordTranslationText").Text == "香蕉");
        old.Result.SetResult(new("apple", "过期的苹果译文")); Pump();
        Assert.True(fixture.Page.WordActionsPopup.IsOpen);
        Assert.Equal("banana", Find<TextBlock>(fixture.Page, "ReadingSelectedWord").Text);
        Assert.Equal("香蕉", Find<TextBlock>(fixture.Page, "WordTranslationText").Text);
    });

    [Fact]
    public void ClosingWordPopupRejectsLateTranslationWithoutReopeningIt() => Run(fixture =>
    {
        fixture.Load(fixture.First, "An apple grows.");
        SelectWord(fixture.Page, "apple");
        Click(fixture.Page, "ReadingTranslateButton");
        var request = Assert.Single(fixture.Translations.Requests);
        var closed = false;
        fixture.Page.WordActionsPopup.Closed += (_, _) => closed = true;
        fixture.Page.WordActionsPopup.IsOpen = false;
        // PopupAnimation=Fade delays Closed. A response can arrive after dismissal
        // was requested but before Closed increments the selection revision.
        request.Result.SetResult(new("apple", "迟到结果"));
        Until(() => closed);
        Assert.False(fixture.Page.WordActionsPopup.IsOpen);
        Assert.DoesNotContain("迟到结果", Find<TextBlock>(fixture.Page, "WordTranslationText").Text);
    });

    [Fact]
    public void EnterOnARealWordRunOpensActionsAndCollectsThatOccurrenceWithoutTranslation() => Run(fixture =>
    {
        fixture.Load(fixture.First, "An apple fell. Another apple grew nearby!");
        ReadingWordSelection? captured = null;
        fixture.Page.WordCaptureRequested += (_, word) => captured = word;
        SelectWord(fixture.Page, "apple", occurrence: 1);
        Assert.True(fixture.Page.WordActionsPopup.IsOpen);
        Assert.Equal("apple", Find<TextBlock>(fixture.Page, "ReadingSelectedWord").Text);
        Assert.Empty(fixture.Translations.Requests);
        Click(fixture.Page, "ReadingCaptureButton");
        Assert.Equal(new ReadingWordSelection("apple", "Another apple grew nearby!"), captured);
        Assert.False(fixture.Page.WordActionsPopup.IsOpen);
        Assert.Empty(fixture.Translations.Requests);
        Assert.Empty(fixture.OpenedSources);
    });

    [Fact]
    public void StationarySingleClickOnWordRunOpensOnlyActionsWithoutCapturingOrTranslating() => Run(fixture =>
    {
        fixture.Load(fixture.First, "An apple grows.");
        var captures = 0;
        fixture.Page.WordCaptureRequested += (_, _) => captures++;
        var reader = Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader");
        reader.Selection.Select(reader.Document.ContentStart, reader.Document.ContentStart);
        MouseWord(WordRun(reader, "apple"), clickCount: 1);
        Pump();
        Assert.True(fixture.Page.WordActionsPopup.IsOpen);
        Assert.Equal("apple", Find<TextBlock>(fixture.Page, "ReadingSelectedWord").Text);
        Assert.Equal(0, captures);
        Assert.Empty(fixture.Translations.Requests);
    });

    [Fact]
    public void ExistingTextSelectionRemainsCopyableAndDoesNotOpenWordActions() => Run(fixture =>
    {
        fixture.Load(fixture.First, "An apple grows.");
        var reader = Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader");
        var run = WordRun(reader, "apple");
        reader.Focus(); reader.Selection.Select(run.ContentStart, run.ContentEnd);
        Assert.Equal("apple", reader.Selection.Text);
        MouseWord(run, clickCount: 1); Pump();
        Assert.Equal("apple", reader.Selection.Text);
        Assert.True(ApplicationCommands.Copy.CanExecute(null, reader));
        Assert.False(fixture.Page.WordActionsPopup.IsOpen);
        Assert.Empty(fixture.Translations.Requests);
    });

    [Fact]
    public void DoubleClickDoesNotTurnNativeWordSelectionIntoAnActionPopup() => Run(fixture =>
    {
        fixture.Load(fixture.First, "An apple grows.");
        var reader = Find<ArticleTextReader>(fixture.Page, "ArticleBodyReader");
        var document = reader.Document;
        reader.Selection.Select(document.ContentStart, document.ContentStart);
        MouseWord(WordRun(reader, "apple"), clickCount: 2); Pump();
        Assert.False(fixture.Page.WordActionsPopup.IsOpen);
        Assert.Same(document, reader.Document);
        Assert.Contains("An apple grows.", Body(fixture.Page));
        Assert.Empty(fixture.Translations.Requests);
    });

    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // ReadingPage has StaticResource references during InitializeComponent.
                // Use an empty test Application, never the production App startup/configuration.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    });

    private static void Run(Action<Fixture> test) => Ui.Value.Invoke(() => { using var fixture = new Fixture(); test(fixture); });
    private static T Find<T>(ReadingPage page, string name) where T : class => Assert.IsType<T>(page.FindName(name));
    private static void Click(ReadingPage page, string name) => Find<Button>(page, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string Body(ReadingPage page)
    {
        var document = Find<ArticleTextReader>(page, "ArticleBodyReader").Document;
        return new TextRange(document.ContentStart, document.ContentEnd).Text;
    }
    private static void SelectWord(ReadingPage page, string word, int occurrence = 0)
    {
        page.WordActionsPopup.IsOpen = false; Pump();
        var reader = Find<ArticleTextReader>(page, "ArticleBodyReader");
        var run = WordRun(reader, word, occurrence);
        reader.Focus(); reader.Selection.Select(run.ContentStart, run.ContentEnd);
        reader.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(reader)!, Environment.TickCount, Key.Enter)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Pump();
        Assert.True(page.WordActionsPopup.IsOpen);
    }
    private static Run WordRun(ArticleTextReader reader, string word, int occurrence = 0)
    {
        var span = reader.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Span>())
            .Where(span => span.Tag is ReadingWordSelection selection && selection.Word == word).Skip(occurrence).First();
        return Assert.Single(span.Inlines.OfType<Run>());
    }
    private static void MouseWord(Run run, int clickCount)
    {
        foreach (var routedEvent in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, clickCount);
            run.RaiseEvent(args);
        }
    }
    private static void Pump() => MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(25));
    private static void Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) Pump();
        Assert.True(condition(), "Reading page did not reach the expected state.");
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    private sealed class Fixture : IDisposable
    {
        public ReadingArticle First { get; } = new("First article", "SUMMARY MUST NEVER BE FULL TEXT", "https://www.sciencedaily.com/releases/first.htm", "ScienceDaily", ReadingCategory.General);
        public ReadingArticle Second { get; } = new("Second article", "SECOND SUMMARY", "https://blog.cloudflare.com/second/", "Cloudflare", ReadingCategory.Technology);
        public ControlledContent Content { get; } = new();
        public ControlledTranslations Translations { get; } = new();
        public List<string> OpenedSources { get; } = [];
        public ReadingPage Page { get; }
        private readonly Window _owner;
        public Fixture()
        {
            var viewModel = new DailyReadingViewModel(new DailyFixture([First, Second]));
            Await(viewModel.LoadAsync());
            Page = new ReadingPage(viewModel, OpenedSources.Add, Content, Translations);
            _owner = new Window { Content = Page, Width = 900, Height = 680, ShowInTaskbar = false };
            _owner.Show(); Pump();
        }
        public void Load(ReadingArticle article, params string[] paragraphs)
        {
            var task = Page.OpenArticleAsync(article);
            Content.Requests[^1].Complete(paragraphs); Await(task); Pump();
        }
        public void Dispose()
        {
            Page.Dispose();
            foreach (var request in Content.Requests) request.Result.TrySetCanceled();
            foreach (var request in Translations.Requests) request.Result.TrySetCanceled();
            Pump(); _owner.Close(); Pump();
        }
    }
    private sealed class DailyFixture(IReadOnlyList<ReadingArticle> articles) : IDailyReadingService
    {
        public Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult(new DailyReadingResult(DateOnly.FromDateTime(DateTime.Now), articles, false, false, "模拟阅读来源，无网络或 API 配置。"));
    }
    private sealed class PendingDailyFixture : IDailyReadingService
    {
        public TaskCompletionSource<DailyReadingResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken) => Result.Task;
    }
    private sealed class ControlledContent : IArticleContentService
    {
        public List<ContentRequest> Requests { get; } = [];
        public Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken)
        { var request = new ContentRequest(article, forceRefresh, cancellationToken); Requests.Add(request); return request.Result.Task; }
    }
    private sealed record ContentRequest(ReadingArticle Article, bool ForceRefresh, CancellationToken Token)
    {
        public TaskCompletionSource<ArticleContent> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(params string[] paragraphs) => Result.SetResult(new(Article, paragraphs, DateTimeOffset.UnixEpoch));
    }
    private sealed class ControlledTranslations : IWordTranslationService
    {
        public List<TranslationRequest> Requests { get; } = [];
        public Task<WordTranslation> TranslateAsync(string word, CancellationToken ct)
        { var request = new TranslationRequest(word, ct); Requests.Add(request); return request.Result.Task; }
    }
    private sealed record TranslationRequest(string Word, CancellationToken Token)
    { public TaskCompletionSource<WordTranslation> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
}
