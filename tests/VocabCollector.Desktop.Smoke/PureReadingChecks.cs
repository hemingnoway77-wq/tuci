using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Reading;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Reading;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    public async Task RunPureReadingChecksOnlyAsync()
    {
        var output = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "pure-reading-smoke");
        var run = Path.Combine(output, Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(run, "isolated-vault");
        var config = Path.Combine(run, "config");
        Directory.CreateDirectory(vault);
        using var modelHandler = new PureReadingModelHandler();
        using var client = new HttpClient(modelHandler);
        var vaultStore = new VaultStore();
        var settings = new AppSettings(new ApiOptions("", "", ""), new VaultOptions(vault, "words", true));
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), vaultStore, TimeProvider.System), settings, vaultStore);
        var feeds = new RecordingReadingService();
        var content = new RecordingArticleContentService();
        var translations = new RecordingWordTranslationService();
        var themes = new ThemeManager(app.Resources, new AppearanceSettingsStore(config));
        await themes.LoadAsync();
        string? sourceUrl = null;
        var main = new MainWindow(vm, new WindowsSettingsStore(config), new RecordingNavigator(), feeds, url => sourceUrl = url,
            themes, learningStateDirectory: Path.Combine(config, "learning"), articleContentService: content, wordTranslationService: translations);
        var bindings = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        app.MainWindow = main;
        main.Show();
        try
        {
            await CheckDailyReadingAsync(main, feeds, () => sourceUrl, output, () => modelHandler.Requests, content, translations, vm, themes);
            Check(modelHandler.Requests == 0 && !Directory.GetFiles(vault, "*.md", SearchOption.AllDirectories).Any(),
                "pure reading and capture prefill create no note and send no model request");
            Check(!File.Exists(Path.Combine(config, "settings.json")), "reading never creates API configuration");
            Check(bindings.Errors.Count == 0, "pure reading has no WPF binding errors: " + string.Join("; ", bindings.Errors));
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindings);
            main.Close();
        }
        Console.WriteLine("Pure-reading artifacts: " + output);
    }

    private static T ReadingControl<T>(ReadingPage page, string name) where T : FrameworkElement =>
        (T)(page.FindName(name) ?? throw new InvalidOperationException("Missing reading control " + name));

    private static void CheckReadingDirectory(ReadingPage page)
    {
        Check(!page.IsArticleOpen && ReadingControl<FrameworkElement>(page, "ReadingDirectory").IsVisible,
            "daily reading initially shows its shared article directory");
        Check(page.FocusVisualStyle is null && ReadingControl<ScrollViewer>(page, "ReadingArticlesScroll").FocusVisualStyle is null &&
            ReadingControl<ItemsControl>(page, "ReadingArticlesList").FocusVisualStyle is null,
            "directory surfaces have no dotted focus border");
        var texts = Descendants<TextBlock>(page).Where(text => text.IsVisible).Select(text => text.Text).ToArray();
        Check(!texts.Contains("TODAY") && !texts.Contains("今日配比"), "directory omits the removed right-hand daily summary card");
        var directory = ReadingControl<FrameworkElement>(page, "ReadingDirectory");
        Check(ReadingControl<ScrollViewer>(page, "ReadingArticlesScroll").ActualWidth >= directory.ActualWidth - 25,
            "article directory uses the available width without a separate right card");
    }

    private async Task CheckPureReadingArticleAsync(MainWindow main, ReadingPage page, ReadingArticle article,
        RecordingArticleContentService content, RecordingWordTranslationService translations, MainViewModel capture,
        ThemeManager themes, Func<string?> openedArticle, string output, Func<int> modelRequests)
    {
        var beforeRequests = modelRequests();
        var initialTheme = themes.CurrentTheme;
        var initialWidth = main.Width;
        var initialHeight = main.Height;
        var body = ReadingControl<RichTextBox>(page, "ArticleBodyReader");
        var document = body.Document;
        var articleText = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Check(content.Calls == 1 && content.LastArticle?.Url == article.Url && !content.LastForced,
            "full-text action uses the injected content service without requesting a feed refresh");
        Check(articleText.Contains(RecordingArticleContentService.FullTextMarker, StringComparison.Ordinal) &&
            articleText.Contains("final paragraph", StringComparison.Ordinal) && document.Blocks.Count == content.Paragraphs.Count,
            "native article reader displays every full-text paragraph beyond the directory summary");
        Check(body.IsReadOnly && body.FocusVisualStyle is null && app.Windows.OfType<ArticleWindow>().All(window => !window.IsVisible),
            "pure full text is selectable native text inside the current window");
        Check(openedArticle() is null && capture.Settings.Api.ApiKey.Length == 0,
            "opening pure full text requires no API key and leaves the original-page callback unused");
        CheckReachable(ReadingControl<Button>(page, "BackToReadingListButton"), main, "return to the reading directory is reachable at minimum size");
        CheckReachable(ReadingControl<Button>(page, "ArticleSourceButton"), main, "original source remains a reachable secondary action");
        Check(body.ActualWidth >= 300 && body.ActualHeight >= 150, "minimum article window retains a usable text area");
        Snapshot(main, Path.Combine(output, "pure-reading-minimum.png"));

        body.ScrollToVerticalOffset(270);
        await Until(() => body.VerticalOffset > 100);
        var noteCount = capture.Notes.Count;
        var savedFiles = ReadingMarkdownFiles(capture.Settings.Vault);
        var selected = await OpenPureWordActionsAsync(page, body, "resilient");
        var popup = (FrameworkElement)page.WordActionsPopup.Child;
        var translate = Descendants<Button>(popup).Single(button => button.Name == "ReadingTranslateButton");
        Check(translations.Calls == 0, "opening word actions does not run a translation before the explicit action");
        Invoke(translate);
        await Until(() => translations.Calls == 1 && Descendants<TextBlock>(popup).Any(text => text.Name == "WordTranslationText" &&
            text.IsVisible && text.Text.Contains(RecordingWordTranslationService.Meaning, StringComparison.Ordinal)));
        Check(translations.LastWord == selected.Word && modelRequests() == beforeRequests,
            "word translation uses the free injected service without the model");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SnapshotWordPopup(popup, Path.Combine(output, "pure-reading-word.png"));
        var readingOffset = body.VerticalOffset;
        Invoke(Descendants<Button>(popup).Single(button => button.Name == "ReadingCaptureButton"));
        await Until(() => Control<RadioButton>(main, "CaptureTab").IsChecked == true && capture.Word == selected.Word && capture.Context == selected.Context);
        Check(Control<TextBox>(main, "WordBox").Text == selected.Word && Control<TextBox>(main, "ContextBox").Text == selected.Context,
            "capture navigation prefills the selected word and its actual article sentence");
        Check(selected.Context.Contains(selected.Word, StringComparison.OrdinalIgnoreCase) && selected.Context.Length > selected.Word.Length,
            "capture receives sentence context rather than only the selected word");
        Check(!capture.IsBusy && capture.Notes.Count == noteCount && modelRequests() == beforeRequests &&
            ReadingMarkdownFiles(capture.Settings.Vault).SequenceEqual(savedFiles), "capture action only prefills input without saving a note or starting generation");
        await SelectPageAsync(main, "ReadingTab");
        await Until(() => page.IsVisible && page.IsArticleOpen && page.CurrentArticle?.Url == article.Url);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(ReferenceEquals(body.Document, document) && Math.Abs(body.VerticalOffset - readingOffset) <= 3 && content.Calls == 1,
            "returning from capture retains the same article, document and reading position without refetching");

        main.Width = 1140;
        main.Height = 800;
        foreach (var (option, target, slug) in new[]
        {
            ("ThemeWarmOption", AppTheme.WarmNeutral, "warm"),
            ("ThemeRoseOption", AppTheme.Pink, "pink"),
            ("ThemeMonoOption", AppTheme.Mono, "mono")
        })
        {
            await SwitchThemeAsync(Control<RadioButton>(main, option), themes, target);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(ReferenceEquals(body.Document, document) && new TextRange(document.ContentStart, document.ContentEnd).Text == articleText,
                "theme switching preserves the complete native article document");
            Check(((SolidColorBrush)document.Foreground).Color == ((SolidColorBrush)app.Resources["Ink"]).Color,
                "native article ink follows the shared application palette");
            CheckReachable(Control<FrameworkElement>(main, "MainHeader"), main, "pure article retains the shared navigation header");
            Snapshot(main, Path.Combine(output, "pure-reading-" + slug + ".png"));
        }
        await themes.SetThemeAsync(initialTheme);
        Invoke(ReadingControl<Button>(page, "ArticleSourceButton"));
        await Until(() => openedArticle() == article.Url);
        Check(modelRequests() == beforeRequests && content.Calls == 1, "secondary source action keeps the original URL and makes no model or content request");

        var retryArticle = ((DailyReadingViewModel)page.DataContext).Articles.First();
        content.FailNext = true;
        await page.OpenArticleAsync(retryArticle, forceRefresh: true);
        await Until(() => ReadingControl<Button>(page, "RetryArticleButton").IsVisible);
        Check(page.IsArticleOpen && page.CurrentArticle?.Url == retryArticle.Url && content.Calls == 2,
            "full-text failure keeps the requested article open with an in-page retry");
        Invoke(ReadingControl<Button>(page, "RetryArticleButton"));
        await Until(() => content.Calls == 3 && body.Document.Blocks.Count == content.Paragraphs.Count &&
            new TextRange(body.Document.ContentStart, body.Document.ContentEnd).Text.Contains(RecordingArticleContentService.FullTextMarker, StringComparison.Ordinal));
        Check(content.LastForced && content.LastArticle?.Url == retryArticle.Url && modelRequests() == beforeRequests,
            "retry refreshes only the selected article through the content service");
        main.Width = initialWidth;
        main.Height = initialHeight;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Invoke(ReadingControl<Button>(page, "BackToReadingListButton"));
        await Until(() => !page.IsArticleOpen && ReadingControl<FrameworkElement>(page, "ReadingDirectory").IsVisible);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CheckReadingDirectory(page);
        Console.WriteLine("PURE READING: native full text, word translation, capture prefill, retained position, themes, source and retry passed");
    }

    private static async Task<ReadingWordSelection> OpenPureWordActionsAsync(ReadingPage page, RichTextBox body, string word)
    {
        var spans = body.Document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Span>())
            .Where(span => span.Tag is ReadingWordSelection selection && selection.Word.Equals(word, StringComparison.OrdinalIgnoreCase)).ToArray();
        var span = spans.First(candidate =>
        {
            var bounds = candidate.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            return bounds.Top >= 5 && bounds.Bottom < body.ActualHeight - 20;
        });
        var selection = (ReadingWordSelection)span.Tag;
        body.Selection.Select(span.ContentStart, span.ContentEnd);
        var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(body), Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        body.RaiseEvent(enter);
        await Until(() => page.WordActionsPopup.IsOpen);
        Check(enter.Handled, "native reader handles the selected word's Enter action through the real WPF route");
        return selection;
    }

    private static string[] ReadingMarkdownFiles(VaultOptions vault) => string.IsNullOrWhiteSpace(vault.RootPath) || !Directory.Exists(vault.RootPath)
        ? [] : Directory.GetFiles(vault.RootPath, "*.md", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void SnapshotWordPopup(FrameworkElement popup, string path)
    {
        popup.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(popup.ActualWidth)), Math.Max(1, (int)Math.Ceiling(popup.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(popup);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class RecordingArticleContentService : IArticleContentService
    {
        public const string FullTextMarker = "PURE_FULL_ARTICLE_CONTENT";
        public int Calls { get; private set; }
        public bool LastForced { get; private set; }
        public bool FailNext { get; set; }
        public ReadingArticle? LastArticle { get; private set; }
        public IReadOnlyList<string> Paragraphs { get; } = Enumerable.Range(1, 45).Select(index =>
            $"Small habits help learners stay resilient when their plans change. Paragraph {index} adds the full article's actual context, beyond its summary, so readers can continue learning in this application." +
            (index == 1 ? " " + FullTextMarker + "." : index == 45 ? " This is the final paragraph." : "")).ToArray();
        public Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++; LastArticle = article; LastForced = forceRefresh;
            if (FailNext) { FailNext = false; throw new ArticleContentException("Smoke fixture: temporary content failure."); }
            return Task.FromResult(new ArticleContent(article, Paragraphs, DateTimeOffset.UtcNow));
        }
    }

    private sealed class RecordingWordTranslationService : IWordTranslationService
    {
        public const string Meaning = "有韧性的；能恢复的";
        public int Calls { get; private set; }
        public string? LastWord { get; private set; }
        public Task<WordTranslation> TranslateAsync(string word, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++; LastWord = word;
            return Task.FromResult(new WordTranslation(word, Meaning));
        }
    }

    private sealed class PureReadingModelHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Pure-reading smoke must never contact a model endpoint.");
        }
    }
}
