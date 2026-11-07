using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VocabCollector.Core.Reading;

namespace VocabCollector.Desktop.Reading;

public sealed class WebViewOfflineTranslationEngine : IOfflineTranslationEngine
{
    private const string LocalHost = "tuci-offline.invalid";
    private const string EntryUrl = "https://" + LocalHost + "/index.html";
    private const int MaxMessageLength = 16 * 1024;
    private readonly Panel _host;
    private readonly Dispatcher _dispatcher;
    private readonly string _assets, _userData;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private HashSet<string>? _verifiedPaths;
    private WebView2? _browser;
    private TaskCompletionSource<bool>? _ready;
    private TaskCompletionSource<string>? _pending;
    private long _generation, _browserGeneration, _nextId, _pendingId;
    private int _disposed;

    public WebViewOfflineTranslationEngine(Panel host, string? assetDirectory = null,
        string? userDataDirectory = null, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host; _dispatcher = host.Dispatcher;
        _assets = Path.GetFullPath(assetDirectory ?? Path.Combine(AppContext.BaseDirectory, "Assets", "OfflineTranslation"));
        _userData = Path.GetFullPath(userDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector", "offline-translation-browser"));
        _timeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        if (_timeout <= TimeSpan.Zero || _timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _dispatcher.ShutdownStarted += OnDispatcherShutdown;
    }

    public async Task<string> TranslateAsync(string text, CancellationToken ct)
    {
        ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) throw new ArgumentException("离线翻译文本为空或过长。", nameof(text));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(_timeout);
        var entered = false;
        try
        {
            await _requests.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true; ThrowIfDisposed();
            if (_verifiedPaths is null)
                _verifiedPaths = await Task.Run(() => VerifyAssetsAsync(deadline.Token), deadline.Token).ConfigureAwait(false);
            await OnUiAsync(() => EnsureReadyOnUiAsync(deadline.Token), deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            var id = Interlocked.Increment(ref _nextId);
            return await OnUiAsync(() => TranslateOnUiAsync(id, text, deadline.Token), deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error)
        {
            if (entered) RequestReset();
            ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
            throw new WordTranslationException("离线翻译等待超时，请重试。", error);
        }
        catch (Exception error)
        {
            if (entered) RequestReset();
            ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
            if (error is WordTranslationException) throw;
            throw FriendlyFailure(error);
        }
        finally { if (entered) _requests.Release(); }
    }

    private async Task<HashSet<string>> VerifyAssetsAsync(CancellationToken ct)
    {
        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var manifestName in new[] { "models.manifest.json", "runtime.manifest.json" })
            {
                var manifestPath = ResolveAsset(manifestName);
                await using var manifest = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (manifest.Length > 2 * 1024 * 1024) throw new InvalidDataException();
                using var document = await JsonDocument.ParseAsync(manifest, cancellationToken: ct).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formatVersion", out var version) ||
                    !version.TryGetInt32(out var format) || format != 1 || !root.TryGetProperty("files", out var files) ||
                    files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) throw new InvalidDataException();
                foreach (var file in files.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("path", out var pathValue) ||
                        pathValue.ValueKind != JsonValueKind.String || !file.TryGetProperty("bytes", out var bytesValue) ||
                        !bytesValue.TryGetInt64(out var bytes) || bytes < 0 || !file.TryGetProperty("sha256", out var hashValue) ||
                        hashValue.ValueKind != JsonValueKind.String) throw new InvalidDataException();
                    var relative = pathValue.GetString()!;
                    var filePath = ResolveAsset(relative);
                    var normalized = relative.Replace('\\', '/');
                    if (!paths.Add(normalized)) throw new InvalidDataException();
                    var hash = hashValue.GetString()!;
                    if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException();
                    await using var content = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (content.Length != bytes) throw new InvalidDataException();
                    var actual = await SHA256.HashDataAsync(content, ct).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(hash))) throw new InvalidDataException();
                }
            }
            if (!paths.Contains("index.html")) throw new InvalidDataException();
            ct.ThrowIfCancellationRequested();
            return paths;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { throw new WordTranslationException("离线翻译包缺失或损坏，请使用完整应用包重新解压。", error); }
    }

    private string ResolveAsset(string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl) ||
            parts.Any(part => part.Length == 0 || part is "." or "..")) throw new InvalidDataException();
        var absolute = Path.GetFullPath(Path.Combine(_assets, Path.Combine(parts)));
        if (!absolute.StartsWith(Path.TrimEndingDirectorySeparator(_assets) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        var current = _assets;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        }
        return absolute;
    }

    private async Task EnsureReadyOnUiAsync(CancellationToken ct)
    {
        _dispatcher.VerifyAccess(); ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
        if (_browser is not null && _browserGeneration == Volatile.Read(ref _generation) && _ready is { } existing)
        { await existing.Task.WaitAsync(ct); return; }
        ReleaseOnUi();
        var generation = Interlocked.Increment(ref _generation);
        _browserGeneration = generation;
        var ready = _ready = Completion<bool>();
        Directory.CreateDirectory(_userData);
        var options = new CoreWebView2EnvironmentOptions
        { AdditionalBrowserArguments = "--disable-background-networking --disable-component-update --disable-sync --no-first-run" };
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userData, options: options);
        EnsureCurrent(generation, ct);
        var browser = new WebView2
        {
            Width = 1, Height = 1, IsHitTestVisible = false, Focusable = false, AllowExternalDrop = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        KeyboardNavigation.SetIsTabStop(browser, false);
        _browser = browser; _host.Children.Add(browser);
        await browser.EnsureCoreWebView2Async(environment);
        EnsureCurrent(generation, ct);
        var core = browser.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsReputationCheckingRequired = false;
        core.SetVirtualHostNameToFolderMapping(LocalHost, _assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += ResourceRequested;
        core.NavigationStarting += NavigationStarting;
        core.FrameNavigationStarting += FrameNavigationStarting;
        core.NewWindowRequested += NewWindowRequested;
        core.PermissionRequested += PermissionRequested;
        core.LaunchingExternalUriScheme += LaunchingExternalUriScheme;
        core.DownloadStarting += DownloadStarting;
        core.ProcessFailed += ProcessFailed;
        core.WebMessageReceived += WebMessageReceived;
        core.Navigate(EntryUrl);
        await ready.Task.WaitAsync(ct);
        EnsureCurrent(generation, ct);
    }

    private async Task<string> TranslateOnUiAsync(long id, string text, CancellationToken ct)
    {
        _dispatcher.VerifyAccess(); ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
        EnsureCurrent(_browserGeneration, ct);
        var core = _browser?.CoreWebView2 ?? throw new WordTranslationException("离线翻译引擎尚未就绪，请重试。");
        var pending = _pending = Completion<string>(); _pendingId = id;
        try
        {
            var payload = JsonSerializer.Serialize(new { id, text });
            await core.ExecuteScriptAsync("window.tuciTranslate(" + payload + ");");
            return await pending.Task.WaitAsync(ct);
        }
        finally
        {
            if (ReferenceEquals(_pending, pending)) { _pending = null; _pendingId = 0; }
        }
    }

    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsCurrent(sender) || !IsEntry(args.Source)) return;
        try
        {
            var json = args.WebMessageAsJson;
            if (json.Length > MaxMessageLength) return;
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            var kind = type.GetString();
            if (kind == "ready") { _ready?.TrySetResult(true); return; }
            if (kind == "error" && !data.TryGetProperty("id", out _))
            {
                SignalFailure(new WordTranslationException("离线翻译引擎启动失败，请重试。"));
                return;
            }
            if (!data.TryGetProperty("id", out var request) || !request.TryGetInt64(out var id) || id != _pendingId || _pending is null) return;
            if (kind == "error") _pending.TrySetException(new WordTranslationException("离线翻译失败，请重试。"));
            else if (kind == "result" && data.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                var value = text.GetString()!;
                if (value.Length is > 0 and <= 2000) _pending.TrySetResult(value);
                else _pending.TrySetException(new WordTranslationException("离线翻译未返回有效结果，请重试。"));
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { }
    }

    private static bool IsEntry(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        IsLocal(uri) && uri.AbsolutePath == "/index.html" && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    private static bool IsLocal(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.Host == LocalHost && uri.IsDefaultPort && uri.UserInfo.Length == 0;
    private bool IsCurrent(object? sender) => Volatile.Read(ref _disposed) == 0 &&
        _browserGeneration == Volatile.Read(ref _generation) && _browser is not null && ReferenceEquals(sender, _browser.CoreWebView2);

    private void ResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (IsCurrent(sender) && Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) && IsLocal(uri) &&
            _verifiedPaths?.Contains(Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))) == true) return;
        if (sender is CoreWebView2 core)
            args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Local translation resources only", "Content-Type: text/plain");
    }
    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    { if (!IsCurrent(sender) || !IsEntry(args.Uri)) args.Cancel = true; }
    private static void FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args) => args.Cancel = true;
    private static void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;
    private static void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args) => args.State = CoreWebView2PermissionState.Deny;
    private static void LaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args) => args.Cancel = true;
    private static void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args) { args.Cancel = true; args.Handled = true; }
    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    { if (IsCurrent(sender)) SignalFailure(new WordTranslationException("离线翻译引擎意外停止，请重试。")); }

    private void SignalFailure(WordTranslationException error)
    { _ready?.TrySetException(error); _pending?.TrySetException(error); RequestReset(); }

    private void EnsureCurrent(long generation, CancellationToken ct)
    {
        ThrowIfDisposed(); ct.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _generation)) throw new WordTranslationException("离线翻译引擎已重置，请重试。");
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) throw new ObjectDisposedException(nameof(WebViewOfflineTranslationEngine));
        var task = _dispatcher.CheckAccess() ? action() : _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, ct).Task.Unwrap();
        ObserveFault(task); return task;
    }
    private Task OnUiAsync(Func<Task> action, CancellationToken ct) => OnUiAsync(async () => { await action(); return true; }, ct);

    private void RequestReset()
    {
        var generation = Interlocked.Increment(ref _generation);
        if (_dispatcher.HasShutdownFinished) return;
        if (_dispatcher.HasShutdownStarted && _dispatcher.CheckAccess()) { ReleaseOnUi(); return; }
        if (_dispatcher.HasShutdownStarted) return;
        // Always defer destruction past a WebView2 callback; the generation changes
        // immediately so an abandoned initialization can never attach a new view.
        try
        {
            var operation = _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            { if (generation == Volatile.Read(ref _generation)) ReleaseOnUi(); }));
            ObserveFault(operation.Task);
        }
        catch (InvalidOperationException) { }
    }

    private void ReleaseOnUi()
    {
        _dispatcher.VerifyAccess();
        var browser = _browser; _browser = null;
        var error = new WordTranslationException("离线翻译引擎已停止，请重试。");
        _ready?.TrySetException(error); _ready = null;
        _pending?.TrySetException(error); _pending = null; _pendingId = 0;
        if (browser is null) return;
        try
        {
            if (browser.CoreWebView2 is { } core)
            {
                core.WebResourceRequested -= ResourceRequested;
                core.NavigationStarting -= NavigationStarting;
                core.FrameNavigationStarting -= FrameNavigationStarting;
                core.NewWindowRequested -= NewWindowRequested;
                core.PermissionRequested -= PermissionRequested;
                core.LaunchingExternalUriScheme -= LaunchingExternalUriScheme;
                core.DownloadStarting -= DownloadStarting;
                core.ProcessFailed -= ProcessFailed;
                core.WebMessageReceived -= WebMessageReceived;
            }
        }
        catch (Exception) { }
        finally
        {
            _host.Children.Remove(browser);
            try { browser.Dispose(); } catch (Exception) { }
        }
    }

    private static TaskCompletionSource<T> Completion<T>()
    { var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); ObserveFault(result.Task); return result; }
    private static void ObserveFault(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private static WordTranslationException FriendlyFailure(Exception error) => error switch
    {
        WebView2RuntimeNotFoundException => new("未找到 Microsoft WebView2 Runtime，请安装或更新 Microsoft Edge WebView2 Runtime 后重试；不需要安装 Firefox。", error),
        IOException or UnauthorizedAccessException => new("无法访问离线翻译数据，请检查磁盘和目录权限后重试。", error),
        _ => new("离线翻译引擎暂时不可用，请重试。", error)
    };
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    private void OnDispatcherShutdown(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dispatcher.ShutdownStarted -= OnDispatcherShutdown;
        _lifetime.Cancel();
        if (_dispatcher.CheckAccess()) { Interlocked.Increment(ref _generation); ReleaseOnUi(); }
        else RequestReset();
        // Waiters can still unwind and release after cancellation. Do not dispose
        // their semaphore or the shared cancellation source under those operations.
    }
}
