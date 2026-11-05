using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace VocabCollector.Desktop.Reading;

public partial class ArticleWindow : Window
{
    private string _requestedUrl;
    private readonly string _userDataDirectory;
    private WebView2? _browser;
    private bool _initializing;
    private bool _closed;
    private ulong? _activeNavigation;
    private readonly HashSet<ulong> _blockedNavigations = [];

    public ArticleWindow(string url, string? userDataDirectory = null)
    {
        InitializeComponent();
        _requestedUrl = IsAllowed(url) ? new Uri(url).AbsoluteUri : "";
        _userDataDirectory = userDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector", "browser");
        AddressBox.Text = _requestedUrl;
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        if (_requestedUrl.Length == 0)
            ShowFailure("来源地址无效。仅支持不含用户名和密码的 HTTP(S) 网页地址。", canRetry: false);
    }

    public static bool IsAllowed(string url) =>
        !string.IsNullOrWhiteSpace(url) && url == url.Trim() &&
        !url.Any(c => char.IsControl(c) || c == '\\') &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsWellFormedOriginalString() &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        !string.IsNullOrWhiteSpace(uri.Host) && uri.UserInfo.Length == 0 &&
        !uri.GetLeftPart(UriPartial.Authority).Contains('@');

    private async void Window_Loaded(object sender, RoutedEventArgs args) => await OpenAsync();

    private async Task OpenAsync()
    {
        if (_closed || _initializing || !IsAllowed(_requestedUrl)) return;
        if (_browser?.CoreWebView2 is { } existing)
        {
            Navigate(existing, _requestedUrl);
            return;
        }

        _initializing = true;
        SetLoading("正在启动内嵌浏览器…");
        MessageText.Text = "正在准备来源文章阅读…";
        try
        {
            Directory.CreateDirectory(_userDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataDirectory);
            if (_closed) return;
            _browser = new WebView2 { AllowExternalDrop = false };
            BrowserHost.Visibility = Visibility.Visible;
            BrowserHost.Children.Add(_browser);
            await _browser.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            var core = _browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.NavigationStarting += NavigationStarting;
            core.FrameNavigationStarting += FrameNavigationStarting;
            core.NavigationCompleted += NavigationCompleted;
            core.SourceChanged += SourceChanged;
            core.DocumentTitleChanged += DocumentTitleChanged;
            core.HistoryChanged += HistoryChanged;
            core.NewWindowRequested += NewWindowRequested;
            core.DownloadStarting += DownloadStarting;
            core.LaunchingExternalUriScheme += LaunchingExternalUriScheme;
            core.PermissionRequested += PermissionRequested;
            core.ProcessFailed += ProcessFailed;
            Navigate(core, _requestedUrl);
        }
        catch (Exception error)
        {
            ReleaseBrowser();
            if (!_closed) ShowFailure(InitializationFailure(error));
        }
        finally
        {
            _initializing = false;
            if (!_closed) RefreshNavigation();
        }
    }

    private void Navigate(CoreWebView2 core, string url)
    {
        if (_closed || !IsAllowed(url)) return;
        try { core.Navigate(url); }
        catch (Exception error) { ShowFailure("无法打开来源文章：" + error.Message); }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_closed || !IsAllowed(args.Uri))
        {
            args.Cancel = true;
            _blockedNavigations.Add(args.NavigationId);
            if (!_closed)
            {
                if (_activeNavigation == args.NavigationId)
                {
                    _activeNavigation = null;
                    ShowFailure("来源网站跳转到了不支持的地址。仅支持不含用户名和密码的 HTTP(S) 网页地址。");
                }
                else StatusText.Text = "已阻止非 HTTP(S) 地址或含登录凭据的地址。";
            }
            return;
        }
        _activeNavigation = args.NavigationId;
        _requestedUrl = args.Uri;
        AddressBox.Text = args.Uri;
        BrowserHost.Visibility = Visibility.Visible;
        MessagePanel.Visibility = Visibility.Collapsed;
        SetLoading("正在加载来源文章…");
    }

    private static void FrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsAllowed(args.Uri)) args.Cancel = true;
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_closed || _blockedNavigations.Remove(args.NavigationId) || args.NavigationId != _activeNavigation) return;
        _activeNavigation = null;
        LoadingProgress.Visibility = Visibility.Collapsed;
        if (args.HttpStatusCode >= 400)
            ShowFailure($"来源网站返回 HTTP {args.HttpStatusCode}。文章可能已移除或暂时无法访问，可稍后重试。");
        else if (!args.IsSuccess)
            ShowFailure("来源文章加载失败：" + NavigationFailure(args.WebErrorStatus) + " 请检查网络后重试。");
        else
        {
            StatusText.Text = "来源：" + new Uri(_requestedUrl).Host;
            RetryButton.Visibility = Visibility.Collapsed;
            UpdatePageDetails();
        }
        RefreshNavigation();
    }

    private void SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args) => UpdatePageDetails();
    private void DocumentTitleChanged(object? sender, object args) => UpdatePageDetails();
    private void HistoryChanged(object? sender, object args) => RefreshNavigation();

    private void UpdatePageDetails()
    {
        if (_closed || _browser?.CoreWebView2 is not { } core) return;
        if (IsAllowed(core.Source)) AddressBox.Text = core.Source;
        var title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "来源文章" : core.DocumentTitle.Trim();
        ArticleTitle.Text = title;
        Title = title + " · 兔词";
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // All popups are handled here; a clicked article link stays in this reading window.
        args.Handled = true;
        if (_closed) return;
        if (args.IsUserInitiated && IsAllowed(args.Uri))
        {
            var url = args.Uri;
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (!_closed && _browser?.CoreWebView2 is { } core) Navigate(core, url);
            }));
        }
        else StatusText.Text = "已阻止网页弹窗或不支持的地址。";
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
        if (!_closed)
        {
            _activeNavigation = null;
            LoadingProgress.Visibility = Visibility.Collapsed;
            StatusText.Text = "此阅读窗口不下载文件。请返回文章正文继续阅读。";
        }
    }

    private void LaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
    {
        args.Cancel = true;
        if (!_closed) StatusText.Text = "已阻止网页启动外部应用。";
    }

    private static void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args) =>
        args.State = CoreWebView2PermissionState.Deny;

    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (_closed) return;
        // Defer disposal until the browser event has returned.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (_closed) return;
            ReleaseBrowser();
            ShowFailure("内嵌浏览器进程已停止（" + args.ProcessFailedKind + "）。点击重试重新打开文章。");
        }));
    }

    private void SetLoading(string message)
    {
        StatusText.Text = message;
        LoadingProgress.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Collapsed;
        RefreshNavigation();
    }

    private void ShowFailure(string message, bool canRetry = true)
    {
        StatusText.Text = "无法显示来源文章";
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
        BrowserHost.Visibility = Visibility.Collapsed;
        LoadingProgress.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Visible;
        RetryButton.IsEnabled = canRetry && IsAllowed(_requestedUrl);
        RefreshNavigation();
    }

    private void RefreshNavigation()
    {
        var core = _closed ? null : _browser?.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        ReloadButton.IsEnabled = core is not null && !_initializing;
        RetryButton.IsEnabled = !_closed && !_initializing && IsAllowed(_requestedUrl);
    }

    private void Back_Click(object sender, RoutedEventArgs args)
    {
        try { if (!_closed && _browser?.CoreWebView2 is { CanGoBack: true } core) core.GoBack(); }
        catch (Exception error) { ShowFailure("无法后退：" + error.Message); }
    }

    private void Forward_Click(object sender, RoutedEventArgs args)
    {
        try { if (!_closed && _browser?.CoreWebView2 is { CanGoForward: true } core) core.GoForward(); }
        catch (Exception error) { ShowFailure("无法前进：" + error.Message); }
    }

    private void Reload_Click(object sender, RoutedEventArgs args)
    {
        try { if (!_closed && _browser?.CoreWebView2 is { } core) core.Reload(); }
        catch (Exception error) { ShowFailure("无法重新加载：" + error.Message); }
    }

    private async void Retry_Click(object sender, RoutedEventArgs args) => await OpenAsync();

    private void Window_Closed(object? sender, EventArgs args)
    {
        _closed = true;
        ReleaseBrowser();
    }

    private void ReleaseBrowser()
    {
        var browser = _browser;
        _browser = null;
        _activeNavigation = null;
        _blockedNavigations.Clear();
        if (browser is null) return;
        if (browser.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= NavigationStarting;
            core.FrameNavigationStarting -= FrameNavigationStarting;
            core.NavigationCompleted -= NavigationCompleted;
            core.SourceChanged -= SourceChanged;
            core.DocumentTitleChanged -= DocumentTitleChanged;
            core.HistoryChanged -= HistoryChanged;
            core.NewWindowRequested -= NewWindowRequested;
            core.DownloadStarting -= DownloadStarting;
            core.LaunchingExternalUriScheme -= LaunchingExternalUriScheme;
            core.PermissionRequested -= PermissionRequested;
            core.ProcessFailed -= ProcessFailed;
        }
        BrowserHost.Children.Remove(browser);
        browser.Dispose();
    }

    private static string InitializationFailure(Exception error) => error switch
    {
        WebView2RuntimeNotFoundException => "未找到 Microsoft Edge WebView2 Runtime。请手动安装或更新该运行时，然后点击重试。",
        UnauthorizedAccessException => "无法访问浏览器数据目录（本机 AppData\\Local\\VocabCollector\\browser）。请检查目录权限后重试。",
        IOException => "无法读写浏览器数据目录。请检查磁盘空间和目录是否被占用后重试。详情：" + error.Message,
        _ => $"无法初始化内嵌浏览器（0x{error.HResult:X8}）。请检查 Microsoft Edge WebView2 Runtime 是否可用后重试。详情：{error.Message}"
    };

    private static string NavigationFailure(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "找不到来源网站",
        CoreWebView2WebErrorStatus.CannotConnect => "无法连接来源网站",
        CoreWebView2WebErrorStatus.Disconnected => "网络连接已断开",
        CoreWebView2WebErrorStatus.Timeout => "连接超时",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or
        CoreWebView2WebErrorStatus.CertificateExpired or
        CoreWebView2WebErrorStatus.CertificateIsInvalid => "网站安全证书无效",
        _ => status.ToString()
    };
}
