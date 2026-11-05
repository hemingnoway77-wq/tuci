using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VocabCollector.Desktop.Reading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;

namespace VocabCollector.Desktop.Smoke;

public sealed partial class SmokeRunner
{
    public async Task RunArticleChecksOnlyAsync()
    {
        var output = Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "article-smoke");
        var run = Path.Combine(output, Guid.NewGuid().ToString("N"));
        var config = Path.Combine(run, "config");
        var vault = Path.Combine(run, "vault");
        Directory.CreateDirectory(vault);
        using var client = new System.Net.Http.HttpClient();
        var store = new VaultStore();
        var vm = new MainViewModel(new CaptureService(new OpenAiCompatibleAnalyzer(client), store, TimeProvider.System),
            new AppSettings(new ApiOptions("", "", ""), new VaultOptions(vault, "words", true)), store);
        var main = new MainWindow(vm, new WindowsSettingsStore(config), new RecordingNavigator(), new RecordingReadingService(),
            _ => throw new InvalidOperationException("Article-only smoke cannot open remote articles"),
            new ThemeManager(app.Resources, new AppearanceSettingsStore(config)), learningStateDirectory: Path.Combine(config, "learning"));
        app.MainWindow = main;
        main.Show();
        try { await CheckArticleAsync(main, run, output); }
        finally { main.Close(); }
        Console.WriteLine("Article artifacts: " + output);
    }

    private static async Task CheckArticleAsync(MainWindow main, string run, string output)
    {
        await using var server = new LocalArticleServer();
        var profile = Path.Combine(run, "browser");
        var article = new ArticleWindow(server.BaseUrl + "/first", userDataDirectory: profile) { Owner = main };
        try
        {
            article.Show();
            var host = Control<Grid>(article, "BrowserHost");
            await UntilArticleAsync(() =>
            {
                if (Control<Button>(article, "RetryButton").IsVisible)
                    throw new InvalidOperationException("Article runtime initialization failed: " + Control<TextBlock>(article, "MessageText").Text);
                return Task.FromResult(host.Children.OfType<WebView2>().Any(view => view.CoreWebView2 is not null));
            });
            var browser = host.Children.OfType<WebView2>().Single();
            var core = browser.CoreWebView2;
            core.NavigationStarting += (_, args) => Console.WriteLine($"ARTICLE starting {args.NavigationId} cancel={args.Cancel}: {args.Uri}");
            core.NavigationCompleted += (_, args) => Console.WriteLine($"ARTICLE complete {args.NavigationId} success={args.IsSuccess} HTTP={args.HttpStatusCode} error={args.WebErrorStatus}; source={core.Source}; status={Control<TextBlock>(article, "StatusText").Text}; message={Control<TextBlock>(article, "MessageText").Text}");
            core.NewWindowRequested += (_, args) => Console.WriteLine($"ARTICLE popup handled={args.Handled} user={args.IsUserInitiated}: {args.Uri}");
            Check(Path.GetFullPath(core.Environment.UserDataFolder).Equals(Path.GetFullPath(profile), StringComparison.OrdinalIgnoreCase), "real WebView2 uses the isolated smoke browser profile");
            var nonLocalResources = 0;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port == server.Port) return;
                nonLocalResources++;
                args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Smoke blocked nonlocal resource", "Content-Type: text/plain");
            };

            await WaitForArticleAsync(article, browser, "/first", "Local article one", "LOCAL_FIRST_BODY");
            Check(Control<TextBox>(article, "AddressBox").Text == server.BaseUrl + "/first" && article.Title.Contains("Local article one", StringComparison.Ordinal), "real article window exposes the loaded source address and title");
            Check(!Control<Button>(article, "BackButton").IsEnabled, "the first source article has no back history");
            await CaptureArticleAsync(article, browser, Path.Combine(output, "article.png"));

            await core.ExecuteScriptAsync("document.getElementById('next').click()");
            await WaitForArticleAsync(article, browser, "/second", "Local article two", "LOCAL_SECOND_BODY");
            Check(Control<Button>(article, "BackButton").IsEnabled, "in-page local article navigation enables back");
            Invoke(Control<Button>(article, "BackButton"));
            await WaitForArticleAsync(article, browser, "/first", "Local article one", "LOCAL_FIRST_BODY");
            Check(Control<Button>(article, "ForwardButton").IsEnabled, "back navigation enables forward");
            Invoke(Control<Button>(article, "ForwardButton"));
            await WaitForArticleAsync(article, browser, "/second", "Local article two", "LOCAL_SECOND_BODY");
            var secondRequests = server.SecondRequests;
            Invoke(Control<Button>(article, "ReloadButton"));
            await UntilArticleAsync(() => Task.FromResult(server.SecondRequests > secondRequests));
            await WaitForArticleAsync(article, browser, "/second", "Local article two", "LOCAL_SECOND_BODY");

            foreach (var prohibited in new[] { "file:///C:/tuci-smoke-never-opened.html", "data:text/html,PROHIBITED_BODY", "tuci-smoke-unregistered://blocked" })
            {
                var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void ObserveBlocked(object? sender, CoreWebView2NavigationStartingEventArgs args)
                { if (args.Uri.StartsWith(prohibited.Split(':')[0] + ":", StringComparison.OrdinalIgnoreCase)) blocked.TrySetResult(args.Cancel); }
                core.NavigationStarting += ObserveBlocked;
                try
                {
                    core.Navigate(prohibited);
                    Check(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(15)), "real browser navigation cancels non-HTTP protocol: " + prohibited.Split(':')[0]);
                    Check(core.Source == server.BaseUrl + "/second", "blocked protocol retains the current local article");
                }
                finally { core.NavigationStarting -= ObserveBlocked; }
            }
            var popup = new TaskCompletionSource<(bool Handled, bool UserInitiated)>(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObservePopup(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => popup.TrySetResult((args.Handled, args.IsUserInitiated));
            core.NewWindowRequested += ObservePopup;
            try
            {
                await core.ExecuteScriptAsync("window.open('/first','_blank')");
                var requested = await popup.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Check(requested.Handled, "browser popup is handled without opening an external window");
                if (requested.UserInitiated)
                    await WaitForArticleAsync(article, browser, "/first", "Local article one", "LOCAL_FIRST_BODY");
            }
            finally { core.NewWindowRequested -= ObservePopup; }

            core.Navigate(server.BaseUrl + "/failure");
            await UntilArticleAsync(() => Task.FromResult(Control<Button>(article, "RetryButton").IsVisible && Control<TextBlock>(article, "MessageText").Text.Contains("HTTP 503", StringComparison.Ordinal)));
            Check(Control<Button>(article, "RetryButton").IsEnabled && !Control<ProgressBar>(article, "LoadingProgress").IsVisible, "HTTP failure shows an enabled retry and stops the loading indicator");
            Snapshot(article, Path.Combine(output, "article-http-error.png"));
            server.Recover = true;
            Invoke(Control<Button>(article, "RetryButton"));
            await WaitForArticleAsync(article, browser, "/failure", "Recovered local article", "LOCAL_RECOVERED_BODY");
            Check(!Control<Button>(article, "RetryButton").IsVisible, "retry recovers the same source address after the local server becomes available");

            var download = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveDownload(object? sender, CoreWebView2DownloadStartingEventArgs args) => download.TrySetResult(args.Cancel && args.Handled);
            core.DownloadStarting += ObserveDownload;
            try
            {
                core.Navigate(server.BaseUrl + "/download");
                Check(await download.Task.WaitAsync(TimeSpan.FromSeconds(15)), "real attachment response is canceled without a download dialog or external file execution");
            }
            finally { core.DownloadStarting -= ObserveDownload; }
            Check(nonLocalResources == 0, "mock source articles request no external resources");
            Check(Directory.Exists(profile), "browser data is created only in the isolated run directory");
        }
        finally { article.Close(); }
        Check(Control<Grid>(article, "BrowserHost").Children.Count == 0, "closing the real article window removes and disposes its WebView2 control");
    }

    private static async Task WaitForArticleAsync(ArticleWindow article, WebView2 browser, string path, string title, string marker)
    {
        await UntilArticleAsync(async () =>
        {
            if (!browser.CoreWebView2.Source.EndsWith(path, StringComparison.Ordinal) ||
                Control<ProgressBar>(article, "LoadingProgress").IsVisible || Control<Border>(article, "MessagePanel").IsVisible) return false;
            var documentTitle = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("document.title"));
            var text = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("document.body.innerText"));
            return documentTitle == title && text?.Contains(marker, StringComparison.Ordinal) == true && article.Title.Contains(title, StringComparison.Ordinal);
        });
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task UntilArticleAsync(Func<Task<bool>> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Timed out waiting for real local article browser state");
            await Task.Delay(25);
        }
    }

    private static async Task CaptureArticleAsync(ArticleWindow window, WebView2 browser, string path)
    {
        // WPF's VisualBrush omits child HWNDs. Capture the actual WebView2 surface and
        // place it at its measured client position in the native WPF shell snapshot.
        window.UpdateLayout();
        using var native = new MemoryStream();
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, native);
        native.Position = 0;
        var pageImage = new BitmapImage();
        pageImage.BeginInit(); pageImage.CacheOption = BitmapCacheOption.OnLoad; pageImage.StreamSource = native; pageImage.EndInit();
        var content = (FrameworkElement)window.Content;
        var margin = content.Margin;
        var width = content.ActualWidth + margin.Left + margin.Right;
        var height = content.ActualHeight + margin.Top + margin.Bottom;
        var origin = browser.TransformToAncestor(content).Transform(new Point());
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
            context.DrawImage(pageImage, new Rect(margin.Left + origin.X, margin.Top + origin.Y, browser.ActualWidth, browser.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class LocalArticleServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _connections = [];
        private readonly Task _loop;
        private int _secondRequests;
        public int Port { get; }
        public string BaseUrl => "http://127.0.0.1:" + Port;
        public int SecondRequests => Volatile.Read(ref _secondRequests);
        public bool Recover { get; set; }

        public LocalArticleServer()
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _connections.Add(RespondAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line == null) return;
                    var path = line.Split(' ')[1];
                    while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 }) { }
                    if (path == "/second") Interlocked.Increment(ref _secondRequests);
                    var status = path == "/failure" && !Recover ? "503 Service Unavailable" : "200 OK";
                    Console.WriteLine($"ARTICLE server {path}: {status}");
                    var title = path switch { "/second" => "Local article two", "/failure" when Recover => "Recovered local article", _ => "Local article one" };
                    var body = path switch { "/second" => "LOCAL_SECOND_BODY", "/failure" when Recover => "LOCAL_RECOVERED_BODY", _ => "LOCAL_FIRST_BODY" };
                    var html = "<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'\"><title>" + title +
                        "</title><style>body{max-width:740px;margin:70px auto;padding:0 32px;background:#fffdf8;color:#282620;font:19px/1.8 Georgia,serif}h1{font-size:36px}a{color:#705a42}</style></head><body><h1>" + title +
                        "</h1><p>这是来自本机 HTTP 服务的文章。正文与导航都在兔词窗口中显示。</p><p>" + body + "</p><p>Real WebView2 runtime, isolated browser data, and local article navigation.</p><a id='next' href='/second'>Continue to the second article →</a></body></html>";
                    var payload = Encoding.UTF8.GetBytes(path == "/download" ? "SMOKE_ATTACHMENT_MUST_BE_CANCELED" : html);
                    var type = path == "/download" ? "application/octet-stream\r\nContent-Disposition: attachment; filename=blocked-smoke.txt" : "text/html; charset=utf-8";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nCache-Control: no-store\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(payload, _stop.Token);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            await _loop;
            await Task.WhenAll(_connections);
            _stop.Dispose();
        }
    }
}
