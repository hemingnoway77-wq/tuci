using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using VocabCollector.Core.Reading;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Reading;
using Xunit.Abstractions;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class ReadingWordPointerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ReadingTranslateButton")]
    [InlineData("ReadingCaptureButton")]
    public void PopupActionPointerDownPreservesTheSelectedWordUntilRelease(string name) => OnUi(() =>
    {
        using var fixture = new Fixture();
        fixture.SelectWord("apple");
        var button = Find<Button>(fixture.Page, name);
        fixture.Trace.Clear();
        Pointer(button, down: true);
        output.WriteLine(string.Join(Environment.NewLine, fixture.Trace));
        Assert.True(fixture.OwnerRouted > 0, "The test must exercise the actual popup-to-owner WPF preview route.");
        Assert.True(fixture.Page.WordActionsPopup.IsOpen,
            "Pressing an action inside the popup must preserve that selected word until release. " + string.Join(" | ", fixture.Trace));
        Pointer(button, down: false);
        Assert.True(fixture.Page.WordActionsPopup.IsOpen);
        Assert.Equal("apple", Find<TextBlock>(fixture.Page, "ReadingSelectedWord").Text);
        Assert.Equal(0, fixture.Deactivated);
    });

    [Fact]
    public void OutsidePointerDownDismissesActionsWithoutConsumingTheArticleSelection() => OnUi(() =>
    {
        using var fixture = new Fixture();
        fixture.SelectWord("apple");
        var run = fixture.Run("banana");
        fixture.Reader.Selection.Select(run.ContentStart, run.ContentEnd);
        Pointer(fixture.Reader, down: true);
        Assert.False(fixture.Page.WordActionsPopup.IsOpen);
        Assert.Equal("banana", fixture.Reader.Selection.Text);
        Assert.True(ApplicationCommands.Copy.CanExecute(null, fixture.Reader));
    });

    [Fact]
    public void DismissingTheFirstPopupDoesNotConsumeTheSecondClicksNativeSelection() => OnUi(() =>
    {
        using var fixture = new Fixture();
        fixture.SelectWord("apple");
        var banana = fixture.Run("banana");
        Pointer(banana, down: true, clickCount: 2);
        // Native RichTextBox word selection happens between the second down and up.
        fixture.Reader.Selection.Select(banana.ContentStart, banana.ContentEnd);
        Pointer(banana, down: false, clickCount: 2);
        Assert.False(fixture.Page.WordActionsPopup.IsOpen);
        Assert.Equal("banana", fixture.Reader.Selection.Text);
        Assert.True(ApplicationCommands.Copy.CanExecute(null, fixture.Reader));
        Assert.Equal(0, fixture.Translations.Requests);
    });

    // Reuse the existing isolated Application rather than creating a second WPF
    // Application when these tests run with ReadingPageTests in the same process.
    private static void OnUi(Action test) => ((Lazy<Dispatcher>)typeof(ReadingPageTests)
        .GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(test);

    private static T Find<T>(ReadingPage page, string name) where T : class => Assert.IsType<T>(page.FindName(name));
    private static void Pump() => MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(25));
    private static void Pointer(IInputElement target, bool down, int clickCount = 1)
    {
        foreach (var routedEvent in down
            ? new[] { Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent }
            : new[] { Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent })
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(args, clickCount);
            if (target is UIElement element) element.RaiseEvent(args);
            else ((ContentElement)target).RaiseEvent(args);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public ReadingPage Page { get; }
        public Window Owner { get; }
        public ArticleTextReader Reader { get; }
        public Translations Translations { get; } = new();
        public List<string> Trace { get; } = [];
        public int OwnerRouted { get; private set; }
        public int Deactivated { get; private set; }

        public Fixture()
        {
            var article = new ReadingArticle("Pointer fixture", "Not full text", "https://www.sciencedaily.com/releases/fixture.htm", "ScienceDaily", ReadingCategory.General);
            var viewModel = new DailyReadingViewModel(new Daily(article));
            Page = new ReadingPage(viewModel, _ => throw new InvalidOperationException("No remote sources in pointer tests"), new Content(), Translations);
            Owner = new Window { Content = Page, Width = 900, Height = 680, ShowInTaskbar = false };
            // Registered before ReadingPage's listener, so this snapshot records
            // the selection state at the very beginning of the owner route.
            Owner.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, args) =>
            {
                OwnerRouted++;
                Trace.Add($"ownerRouted={OwnerRouted}; source={(args.OriginalSource as FrameworkElement)?.Name ?? args.OriginalSource.GetType().Name}; popup={Page.WordActionsPopup.IsOpen}; handled={args.Handled}");
            }), true);
            Owner.Deactivated += (_, _) => { Deactivated++; Trace.Add("owner.Deactivated"); };
            Owner.Show(); Pump();
            var task = Page.OpenArticleAsync(article);
            Wait(task);
            Reader = Find<ArticleTextReader>(Page, "ArticleBodyReader");
            foreach (var name in new[] { "ReadingTranslateButton", "ReadingCaptureButton" })
                Find<Button>(Page, name).AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) =>
                    Trace.Add($"buttonPreviewDown={name}; popup={Page.WordActionsPopup.IsOpen}")), true);
            Page.WordActionsPopup.Closed += (_, _) => Trace.Add("popup.Closed");
        }

        public Run Run(string word) => Assert.Single(Reader.Document.Blocks.OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.OfType<Span>())
            .Single(span => span.Tag is ReadingWordSelection selection && selection.Word == word).Inlines.OfType<Run>());

        public void SelectWord(string word)
        {
            var run = Run(word);
            Reader.Focus(); Reader.Selection.Select(run.ContentStart, run.ContentEnd);
            Reader.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Reader)!, Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump();
            Assert.True(Page.WordActionsPopup.IsOpen);
        }

        public void Dispose() { Page.Dispose(); Pump(); Owner.Close(); Pump(); }
        private static void Wait(Task task)
        {
            var end = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < end) Pump();
            Assert.True(task.IsCompleted); task.GetAwaiter().GetResult(); Pump();
        }
    }

    private sealed class Daily(ReadingArticle article) : IDailyReadingService
    {
        public Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyReadingResult(DateOnly.FromDateTime(DateTime.Now), [article], false, false, "isolated pointer test"));
    }
    private sealed class Content : IArticleContentService
    {
        public Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new ArticleContent(article, ["An apple grows. A banana ripens."], DateTimeOffset.UnixEpoch));
    }
    private sealed class Translations : IWordTranslationService
    {
        public int Requests { get; private set; }
        public Task<WordTranslation> TranslateAsync(string word, CancellationToken ct)
        { Requests++; return Task.FromResult(new WordTranslation(word, "fixture translation")); }
    }
}
