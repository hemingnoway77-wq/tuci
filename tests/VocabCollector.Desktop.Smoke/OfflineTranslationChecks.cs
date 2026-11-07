using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using VocabCollector.Core.Reading;
using VocabCollector.Desktop.Reading;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    public async Task RunOfflineTranslationChecksOnlyAsync(string? bundleDirectory = null)
    {
        var run = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "offline-translation-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var dictionaryDirectory = bundleDirectory is null ? Path.Combine(AppContext.BaseDirectory, "Assets", "OfflineDictionary")
            : Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleDirectory)))!, "OfflineDictionary");
        var host = new Grid { Width = 1, Height = 1, IsHitTestVisible = false };
        var window = new Window { Content = host, Width = 120, Height = 80, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.ToolWindow, Title = "TUCI isolated offline translation" };
        window.Show();
        var timings = new Dictionary<string, object>();
        using var engine = new WebViewOfflineTranslationEngine(host, bundleDirectory, Path.Combine(run, "profile"));
        using var service = new OfflineWordTranslationService(engine);
        try
        {
            Check(host.Children.Count == 0, "offline runtime is lazy and has no browser before translation");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await ExpectOfflineCancellationAsync(engine.TranslateAsync("hello", canceled.Token));
                Check(host.Children.Count == 0, "pre-canceled request does not create a browser or load assets");
            }

            var clock = Stopwatch.StartNew();
            var greeting = await engine.TranslateAsync("hello", default);
            timings["coldMilliseconds"] = clock.ElapsedMilliseconds;
            Check(greeting.Contains("你好", StringComparison.Ordinal), "real bundled model translates hello to Chinese");
            var browser = host.Children.OfType<WebView2>().Single();
            var core = browser.CoreWebView2;
            Check(core.Source == "https://tuci-offline.invalid/index.html", "engine loads only its local virtual-host entrypoint");
            Check(Path.GetFullPath(core.Environment.UserDataFolder).Equals(Path.GetFullPath(Path.Combine(run, "profile")), StringComparison.OrdinalIgnoreCase),
                "offline engine uses a fresh isolated profile without an existing model cache");

            clock.Restart();
            var sentence = await engine.TranslateAsync("A resilient learner can stay steady when plans change.", default);
            timings["warmSentenceMilliseconds"] = clock.ElapsedMilliseconds;
            Check(HasChinese(sentence) && sentence.Contains("学习", StringComparison.Ordinal), "real engine translates an English sentence from bundled files");
            timings["sentence"] = sentence;
            var word = await service.TranslateAsync("Apple", default);
            Check(HasChinese(word.TranslatedText) && !word.IsFromCache && word.Word == "apple", "offline word service returns a real Chinese translation");
            var cached = await service.TranslateAsync("APPLE", default);
            Check(cached.IsFromCache && cached.TranslatedText == word.TranslatedText, "repeat normalized word uses the successful local cache");
            var apostrophe = await service.TranslateAsync("don't", default);
            Check(HasChinese(apostrophe.TranslatedText), "local model supports a word containing an apostrophe");

            // Prove the target is reachable, then verify the engine origin cannot
            // contact it. A DNS failure alone would not prove network blocking.
            await using var networkTarget = new LocalArticleServer();
            using (var probeClient = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false }))
            using (var reachable = await probeClient.GetAsync(networkTarget.BaseUrl + "/second"))
                Check(reachable.IsSuccessStatusCode, "network-blocking probe endpoint is actually reachable");
            var beforeProbe = networkTarget.SecondRequests;
            var remoteProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void NetworkProbe(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
            {
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                if (message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "offline-smoke-network")
                    remoteProbe.TrySetResult(message.RootElement.GetProperty("allowed").GetBoolean());
            }
            core.WebMessageReceived += NetworkProbe;
            try
            {
                await core.ExecuteScriptAsync("fetch(" + JsonSerializer.Serialize(networkTarget.BaseUrl + "/second") + ").then(() => chrome.webview.postMessage({type:'offline-smoke-network',allowed:true}), () => chrome.webview.postMessage({type:'offline-smoke-network',allowed:false}));");
                Check(!await remoteProbe.Task.WaitAsync(TimeSpan.FromSeconds(5)), "external network resource is blocked in the translation document");
                Check(networkTarget.SecondRequests == beforeProbe, "blocked engine fetch never reaches the reachable nonlocal server");
            }
            finally { core.WebMessageReceived -= NetworkProbe; }

            // Unsolicited or stale IDs are ignored rather than completing a future request.
            await core.ExecuteScriptAsync("chrome.webview.postMessage({type:'result',id:-1,text:'STALE_RESULT'});");
            var afterStale = await engine.TranslateAsync("book", default);
            Check(HasChinese(afterStale) && !afterStale.Contains("STALE_RESULT", StringComparison.Ordinal), "stale worker response cannot replace a later result");

            using (var cancel = new CancellationTokenSource())
            {
                var pending = engine.TranslateAsync(string.Join(" ", Enumerable.Repeat("A book helps a learner.", 50)), cancel.Token);
                cancel.Cancel();
                await ExpectOfflineCancellationAsync(pending);
            }
            Check((await engine.TranslateAsync("hello", default)).Contains("你好", StringComparison.Ordinal), "translation works after an in-flight caller cancels");
            var beforeCrash = host.Children.OfType<WebView2>().Single();
            var crashed = beforeCrash.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash", "{}");
            _ = crashed.ContinueWith(completed => _ = completed.Exception, TaskContinuationOptions.OnlyOnFaulted);
            await Until(() => host.Children.Count == 0);
            Check((await engine.TranslateAsync("hello", default)).Contains("你好", StringComparison.Ordinal) &&
                !ReferenceEquals(beforeCrash, host.Children.OfType<WebView2>().Single()),
                "a failed renderer is released and the next translation rebuilds the local engine");
            service.Dispose();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(host.Children.Count == 0, "disposing owned offline service releases its browser host");

            using var missing = new WebViewOfflineTranslationEngine(host, Path.Combine(run, "missing-bundle"), Path.Combine(run, "missing-profile"));
            try { await missing.TranslateAsync("hello", default); throw new InvalidOperationException("Missing language bundle unexpectedly succeeded."); }
            catch (WordTranslationException error) { Check(error.Message.Contains("离线", StringComparison.Ordinal), "missing local assets show a friendly offline-bundle error"); }
            Check(host.Children.Count == 0, "missing bundled assets fail before creating a browser");
            Console.WriteLine("Real offline engine checks: " + JsonSerializer.Serialize(timings));
        }
        finally { window.Close(); }

        // Exercise the production default through the real native popup, not an injected fake translator.
        var article = new ReadingArticle("Offline reading fixture", "Summary", "https://www.sciencedaily.com/releases/offline.htm", "ScienceDaily", ReadingCategory.General);
        using (var dictionary = new JsonLinesWordDictionary(dictionaryDirectory))
        {
            var bookEntry = await dictionary.LookupAsync("book", default);
            var runEntry = await dictionary.LookupAsync("run", default);
            var inflected = await dictionary.LookupAsync("introduced", default);
            Check(bookEntry?.Meanings is { Count: >= 2 } && bookEntry.Meanings.Select(meaning => meaning.PartOfSpeech).Distinct().Count() >= 2,
                "bundled book entry contains real meanings for multiple parts of speech");
            Check(runEntry?.Meanings is { Count: >= 2 } && !string.IsNullOrWhiteSpace(runEntry.Phonetic),
                "bundled run entry includes phonetics and multiple source-labelled meanings");
            Check(inflected?.Meanings is { Count: > 0 } && inflected.Word == "introduced", "dictionary preserves an inflected entry's selected word and meanings");
            Check(await dictionary.LookupAsync("tuciwordnotindictionary", default) is null, "unknown spelling is a real dictionary miss");
            timings["bookMeanings"] = bookEntry!.Meanings!;
            timings["runMeanings"] = runEntry!.Meanings!;
        }
        var viewModel = new DailyReadingViewModel(new OfflineFeeds(article));
        using var page = new ReadingPage(viewModel, _ => throw new InvalidOperationException("Offline popup must not open a browser page."), new OfflineContent());
        var owner = new Window { Content = page, Width = 900, Height = 680, ShowInTaskbar = false, ShowActivated = false, Title = "TUCI isolated native offline popup" };
        owner.Show();
        try
        {
            // Published-layout checks point the real engine at the supplied release bundle too.
            var field = typeof(ReadingPage).GetField("_translationService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Check(field.GetValue(page) is OfflineDictionaryWordTranslationService, "production reading page selects the offline dictionary-first service by default");
            // Replace only the unopened engine's profile to avoid touching the user's browser data.
            ((IDisposable)field.GetValue(page)!).Dispose();
            var popupHost = ReadingControl<Grid>(page, "TranslationRuntimeHost");
            var popupService = new OfflineDictionaryWordTranslationService(new JsonLinesWordDictionary(dictionaryDirectory),
                new OfflineWordTranslationService(new WebViewOfflineTranslationEngine(popupHost, bundleDirectory, Path.Combine(run, "popup-profile"))));
            field.SetValue(page, popupService);
            await page.OpenArticleAsync(article);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var body = ReadingControl<RichTextBox>(page, "ArticleBodyReader");
            await OpenPureWordActionsAsync(page, body, "apple");
            InvokePopupAfterPointerRoute(page, ReadingControl<Button>(page, "ReadingTranslateButton"));
            var text = ReadingControl<TextBlock>(page, "WordTranslationText");
            await Until(() => text.Text.Length > 0 || !ReadingControl<Button>(page, "ReadingTranslateButton").IsEnabled);
            var deadline = Stopwatch.StartNew();
            while (!ReadingControl<Button>(page, "ReadingTranslateButton").IsEnabled && deadline.Elapsed < TimeSpan.FromSeconds(75)) await Task.Delay(25);
            Console.WriteLine("Native offline popup state: " + JsonSerializer.Serialize(new { text = text.Text, open = page.WordActionsPopup.IsOpen,
                enabled = ReadingControl<Button>(page, "ReadingTranslateButton").IsEnabled, views = ReadingControl<Grid>(page, "TranslationRuntimeHost").Children.Count, elapsedMs = deadline.ElapsedMilliseconds }));
            Check(text.Text.Contains("苹果", StringComparison.Ordinal),
                "actual default reading popup displays the real local model's Chinese translation");
            Check(page.WordActionsPopup.IsOpen, "offline result stays in the original native word popup");
            Check(ReadingControl<ItemsControl>(page, "WordMeaningsList").Items.Count > 0 && popupHost.Children.Count == 0,
                "dictionary details are shown without starting the machine-translation browser");
            Check(ReadingControl<TextBlock>(page, "WordPhoneticText").Visibility == Visibility.Visible,
                "native mini entry displays the real dictionary phonetic");
            timings["nativePopup"] = text.Text;
            SnapshotWordPopup((FrameworkElement)page.WordActionsPopup.Child, Path.Combine(run, "offline-word-popup.png"));
            page.WordActionsPopup.IsOpen = false;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await OpenPureWordActionsAsync(page, body, "book");
            InvokePopupAfterPointerRoute(page, ReadingControl<Button>(page, "ReadingTranslateButton"));
            await Until(() => ReadingControl<ItemsControl>(page, "WordMeaningsList").Items.Count >= 2);
            Check(popupHost.Children.Count == 0, "multi-part-of-speech dictionary lookup remains independent of the WASM runtime");
            SnapshotWordPopup((FrameworkElement)page.WordActionsPopup.Child, Path.Combine(run, "offline-book-details.png"));
            var fallback = await popupService.TranslateAsync("tuciwordnotindictionary", default);
            Check(!string.IsNullOrWhiteSpace(fallback.TranslatedText) && fallback.Meanings is null && popupHost.Children.Count == 1,
                "unknown word uses the actual local model without fabricated dictionary details");
        }
        finally { page.Dispose(); owner.Close(); }
        await File.WriteAllTextAsync(Path.Combine(run, "results.json"), JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Offline translation artifacts: " + run);
        Console.WriteLine(JsonSerializer.Serialize(timings));
    }

    private static bool HasChinese(string value) => value.Any(character => character is >= '\u4e00' and <= '\u9fff');
    private static async Task ExpectOfflineCancellationAsync(Task task)
    {
        try { await task; throw new InvalidOperationException("Expected caller cancellation."); }
        catch (OperationCanceledException) { }
    }
    private sealed class OfflineFeeds(ReadingArticle article) : IDailyReadingService
    {
        public Task<DailyReadingResult> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new DailyReadingResult(DateOnly.FromDateTime(DateTime.Today), [article], false, true, "offline fixture"));
    }
    private sealed class OfflineContent : IArticleContentService
    {
        public Task<ArticleContent> GetAsync(ReadingArticle article, bool forceRefresh, CancellationToken cancellationToken) =>
            Task.FromResult(new ArticleContent(article, ["Read a short article in this application.", "An apple is on the table. A book helps a learner."], DateTimeOffset.UtcNow, IsFromCache: true, IsOffline: true));
    }
}
