using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Reading;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;
using VocabCollector.Desktop.Reading;
using VocabCollector.Desktop.Controls;

namespace VocabCollector.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsStore _store;
    private readonly IAppNavigator _navigator;
    private readonly IDailyReadingService? _readingService;
    private readonly Action<string>? _openArticle;
    private readonly IArticleContentService? _articleContentService;
    private readonly IWordTranslationService? _wordTranslationService;
    private readonly ThemeManager _themes;
    private readonly DesktopSync? _sync;
    private readonly string? _learningStateDirectory;
    private HttpClient? _readingClient;
    private DailyReadingViewModel? _readingViewModel;
    private ReadingPage? _readingPage;
    private LearningPage? _learningPage;
    private bool _initializingUi = true, _syncingThemeOption, _changingPage, _closed;
    private string _page = "capture";
    private readonly Func<Task>? _prepareLocalData;
    private Task? _preparation;

    public MainWindow(MainViewModel viewModel, ISettingsStore settingsStore, IAppNavigator navigator,
        IDailyReadingService? readingService = null, Action<string>? openArticle = null, ThemeManager? themeManager = null,
        DesktopSync? sync = null, string? learningStateDirectory = null,
        IArticleContentService? articleContentService = null, IWordTranslationService? wordTranslationService = null,
        Func<Task>? prepareLocalData = null)
    {
        _themes = themeManager ?? new ThemeManager(Application.Current.Resources);
        _viewModel = viewModel; _store = settingsStore; _navigator = navigator;
        _readingService = readingService; _openArticle = openArticle; _sync = sync;
        _learningStateDirectory = learningStateDirectory;
        _articleContentService = articleContentService; _wordTranslationService = wordTranslationService;
        _prepareLocalData = prepareLocalData;
        InitializeComponent();
        DataContext = viewModel;
        SyncButton.Visibility = sync == null ? Visibility.Collapsed : Visibility.Visible;
        if (sync != null) { SyncButton.ToolTip = sync.Status; sync.Updated += SyncStatusChanged; }
        Activated += (_, _) => { _sync?.Request(); _learningPage?.RequestReload(false); };
        SelectThemeOption(_themes.CurrentTheme);
        _themes.ThemeChanged += ThemeChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        StatusMetaText.Text = DateTime.Now.ToString("yyyy.MM.dd") + " · 兔词";
        _initializingUi = false;
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        Loaded += async (_, _) => { UpdateStatus(); AlignNavigation(false); if (_prepareLocalData is not null) await PrepareForUseAsync(); };
        Closing += OnClosing; Closed += OnClosed;
    }

    private void SelectThemeOption(AppTheme theme)
    {
        var option = theme switch { AppTheme.Pink => ThemeRoseOption, AppTheme.WarmNeutral => ThemeWarmOption, _ => ThemeMonoOption };
        option.IsChecked = true;
    }
    private void ThemeChanged(object? sender, AppTheme theme)
    {
        _syncingThemeOption = true;
        try { SelectThemeOption(theme); } finally { _syncingThemeOption = false; }
        TitleBarTheme.Apply(this);
    }
    private async void ThemeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializingUi || _syncingThemeOption || sender is not RadioButton { Tag: string name }) return;
        var theme = name switch { "Pink" => AppTheme.Pink, "WarmNeutral" => AppTheme.WarmNeutral, _ => AppTheme.Mono };
        try { if (!await _themes.SetThemeAsync(theme)) SetMessage("主题已切换，但未能保存偏好；下次启动会恢复上次的主题。"); }
        catch (Exception) { SetMessage("暂时无法切换主题，请稍后重试。"); }
    }
    private void CaptureTab_Checked(object sender, RoutedEventArgs e)
    { if (!_initializingUi && !_changingPage) SelectPage("capture"); }
    private async void LibraryTab_Checked(object sender, RoutedEventArgs e)
    { if (!_initializingUi && !_changingPage) await ShowLearningAsync(false); }
    private async void RoamingTab_Checked(object sender, RoutedEventArgs e)
    { if (!_initializingUi && !_changingPage) await ShowLearningAsync(true); }
    private async void ReadingTab_Checked(object sender, RoutedEventArgs e)
    { if (!_initializingUi && !_changingPage) await ShowReadingAsync(); }

    private void SelectPage(string page)
    {
        if (_closed) return;
        var previous = _page;
        foreach (var surface in PageHost.Children.OfType<FrameworkElement>()) PageMotion.Reset(surface);
        if (_page == "reading" && page != "reading") _readingPage?.Cancel();
        _page = page;
        CapturePage.Visibility = page == "capture" ? Visibility.Visible : Visibility.Collapsed;
        if (_readingPage != null) _readingPage.Visibility = page == "reading" ? Visibility.Visible : Visibility.Collapsed;
        if (_learningPage != null) _learningPage.Visibility = page is "library" or "roaming" ? Visibility.Visible : Visibility.Collapsed;
        _changingPage = true;
        try { (page switch { "library" => LibraryTab, "roaming" => RoamingTab, "reading" => ReadingTab, _ => CaptureTab }).IsChecked = true; }
        finally { _changingPage = false; }
        AlignNavigation(previous != page);
        if (previous != page)
        {
            var surface = page switch { "capture" => (FrameworkElement)CapturePage, "reading" => _readingPage, _ => _learningPage };
            if (surface is not null) PageMotion.Enter(surface, PageOrder(page) >= PageOrder(previous));
        }
        UpdateStatus();
    }
    private static int PageOrder(string page) => page switch { "library" => 1, "roaming" => 2, "reading" => 3, _ => 0 };
    private void Navigation_SizeChanged(object sender, SizeChangedEventArgs e) { if (!_initializingUi) AlignNavigation(false); }
    private void AlignNavigation(bool animate)
    {
        if (_closed || PageUnderline is null) return;
        var selected = _page switch { "library" => LibraryTab, "roaming" => RoamingTab, "reading" => ReadingTab, _ => CaptureTab };
        if (selected is null || selected.ActualWidth <= 0) return;
        var origin = selected.TranslatePoint(new Point(), MainNavigation);
        PageUnderline.MoveTo(origin.X, selected.ActualWidth, animate);
    }
    public Task PrepareForUseAsync() => _preparation ??= PrepareModulesCoreAsync();
    private async Task PrepareModulesCoreAsync()
    {
        if (_closed) return;
        PreparationOverlay.Visibility = Visibility.Visible;
        PreparationProgress.IsIndeterminate = true; MainNavigation.IsEnabled = false;
        try
        {
            // Let the shell and its real loading surface paint before local work.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_closed) return;
            if (_prepareLocalData is not null) await _prepareLocalData();
            if (_closed) return;
            var learning = EnsureLearningPage();
            var reading = EnsureReadingPage(); // Native UI only; no feed or translation request.
            await learning.EnsureLoadedAsync();
            if (_closed) return;
            // Collapsed controls have data but no generated rows/templates.
            // Realize their first layout behind the opaque preparation surface,
            // so the first navigation click does not pay that synchronous cost.
            await PreparePageLayoutAsync(learning);
            if (!_closed) await PreparePageLayoutAsync(reading);
        }
        catch (Exception) { if (!_closed) SetMessage("本地内容暂未准备好，可以进入词库后重试。"); }
        finally
        {
            PreparationProgress.IsIndeterminate = false; PreparationOverlay.Visibility = Visibility.Collapsed;
            MainNavigation.IsEnabled = true;
            if (!_closed) { AlignNavigation(false); PageMotion.Enter(CapturePage); }
        }
    }
    private async Task PreparePageLayoutAsync(FrameworkElement page)
    {
        var visibility = page.Visibility;
        var hitTesting = page.IsHitTestVisible;
        try
        {
            page.IsHitTestVisible = false;
            page.Visibility = Visibility.Visible;
            page.UpdateLayout();
            // Loaded container generation, binding and the first render pass
            // must finish while the shell still shows its loading surface.
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        }
        finally
        {
            page.Visibility = visibility;
            page.IsHitTestVisible = hitTesting;
        }
    }
    private LearningPage EnsureLearningPage()
    {
        if (_learningPage != null) return _learningPage;
        _learningPage = new LearningPage(_viewModel.Settings.Vault, _learningStateDirectory) { Visibility = Visibility.Collapsed };
        _learningPage.StatusChanged += (_, _) => UpdateStatus();
        _learningPage.ReaderRequested += (_, _) => SelectPage("library");
        _learningPage.WebLinkRequested += (_, url) => OpenArticle(url);
        PageHost.Children.Add(_learningPage);
        return _learningPage;
    }
    private async Task ShowLearningAsync(bool roaming)
    {
        var learning = EnsureLearningPage();
        SelectPage(roaming ? "roaming" : "library");
        if (!roaming) learning.ShowLibrary();
        await learning.EnsureLoadedAsync();
        if (_closed || !ReferenceEquals(learning, _learningPage)) return;
        if (roaming && _page == "roaming") await learning.ShowRoamingAsync();
    }
    private async Task OpenInLibraryAsync(NoteLocation target)
    {
        var learning = EnsureLearningPage();
        await learning.EnsureLoadedAsync();
        if (_closed || !ReferenceEquals(learning, _learningPage)) return;
        var note = learning.AllNotes.FirstOrDefault(n => n.AbsolutePath.Equals(target.AbsolutePath, StringComparison.OrdinalIgnoreCase));
        if (note == null) { SetMessage("这篇笔记暂时无法读取，请刷新词库或检查目录。"); return; }
        learning.OpenNote(note);
        SelectPage("library");
    }
    private ReadingPage EnsureReadingPage()
    {
        if (_readingPage == null)
        {
            var service = _readingService;
            if (service == null)
            {
                _readingClient ??= new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector", "reading");
                service = new DailyReadingService(_readingClient, cache);
            }
            _readingViewModel = new DailyReadingViewModel(service);
            _readingViewModel.PropertyChanged += ViewModel_PropertyChanged;
            _readingPage = new ReadingPage(_readingViewModel, OpenArticle, _articleContentService, _wordTranslationService) { Visibility = Visibility.Collapsed };
            _readingPage.WordCaptureRequested += ReadingWordCaptureRequested;
            _readingPage.CaptureLinkRequested += Preview_LinkRequested;
            PageHost.Children.Add(_readingPage);
        }
        return _readingPage;
    }
    private async Task ShowReadingAsync()
    {
        var reading = EnsureReadingPage();
        SelectPage("reading");
        try { await reading.EnsureLoadedAsync(); }
        catch (Exception) { if (_readingViewModel != null) _readingViewModel.Message = "文章获取失败，请点击「刷新文章」重试。"; }
    }
    private void ReadingWordCaptureRequested(object? sender, ReadingWordSelection word)
    {
        if (_closed) return;
        if (_viewModel.IsBusy)
        {
            if (_readingViewModel != null) _readingViewModel.Message = "请等待当前收录完成，再收录这个单词。";
            return;
        }
        _readingPage?.OpenCapturePreview(_viewModel, word);
    }
    private void OpenArticle(string url)
    {
        if (_closed || !ArticleWindow.IsAllowed(url)) { SetMessage("文章链接不可用。"); return; }
        try { if (_openArticle != null) _openArticle(url); else new ArticleWindow(url) { Owner = this }.Show(); }
        catch (Exception) { SetMessage("应用内原文暂时无法打开，请稍后重试。"); }
    }
    private void UpdateStatus()
    {
        if (_initializingUi || _closed) return;
        StatusMessageText.Text = _page switch { "reading" => _readingPage?.CapturePreviewViewModel is { IsBusy: true } capture ? capture.Message : _readingViewModel?.Message ?? "", "library" or "roaming" => _learningPage?.Message ?? "", _ => _viewModel.Message };
        var busy = _page switch { "reading" => _readingViewModel?.IsBusy == true || _readingPage?.CapturePreviewViewModel?.IsBusy == true, "library" or "roaming" => _learningPage?.IsBusy == true, _ => _viewModel.IsBusy };
        StatusProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Message" or "IsBusy") UpdateStatus();
        if (ReferenceEquals(sender, _viewModel) && e.PropertyName == nameof(MainViewModel.IsBusy) && !_viewModel.IsBusy)
        { _learningPage?.RequestReload(); _sync?.Request(); }
    }
    private void SetMessage(string message)
    {
        if (_page == "reading" && _readingViewModel != null) _readingViewModel.Message = message;
        else if (_page == "library" && _learningPage != null) _learningPage.BrowserControl.SetMessage(message);
        else { _viewModel.Message = message; StatusMessageText.Text = message; }
    }
    private void SyncStatusChanged(object? sender, EventArgs args)
    {
        SyncButton.ToolTip = _sync?.Status;
        if (_sync?.Status.StartsWith("上次同步", StringComparison.Ordinal) == true) _learningPage?.RequestReload();
    }
    private void Sync_Click(object sender, RoutedEventArgs e)
    { if (_sync != null) new SyncWindow(_sync, _viewModel.Settings.Vault) { Owner = this }.ShowDialog(); }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        var dialog = new SettingsWindow(_viewModel.Settings, _store, _themes) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SavedSettings is not { } saved) return;
        var previous = _viewModel.Settings.Vault;
        _viewModel.ApplySettings(saved);
        if (previous != saved.Vault && _learningPage != null)
        {
            _learningPage.Dispose(); PageHost.Children.Remove(_learningPage); _learningPage = null;
            if (_page is "library" or "roaming") _ = ShowLearningAsync(_page == "roaming");
        }
        if (_sync?.Connected == true && !Path.GetFullPath(_sync.Folder).Equals(VaultPaths.WordRoot(saved.Vault), StringComparison.OrdinalIgnoreCase))
            SetMessage("词库位置已改变；云端仍绑定原目录，请在「词库同步」中重新核对绑定。");
    }
    private async void OpenNote_Click(object sender, RoutedEventArgs e)
    { if (_viewModel.SelectedNote is { } note) await OpenInLibraryAsync(note); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedNote is not { } note) return;
        try { _navigator.OpenFolder(note); }
        catch (Exception) { SetMessage("无法打开文件夹，请检查它是否仍然存在。"); }
    }
    private async void Preview_LinkRequested(object? sender, PreviewLinkEventArgs e)
    {
        if (_viewModel.IsBusy || _closed) return;
        if (!e.IsWikiLink && Uri.TryCreate(e.Target, UriKind.Absolute, out var web) && web.Scheme is "https" or "http")
        { OpenArticle(e.Target); return; }
        var source = _viewModel.SelectedNote;
        var page = _page;
        var learning = EnsureLearningPage();
        await learning.EnsureLoadedAsync();
        if (!_closed && ReferenceEquals(learning, _learningPage) && source == _viewModel.SelectedNote && page == _page)
            learning.HandleLink(e, source, sender as FrameworkElement ?? NotePreview);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            e.Cancel = true; _viewModel.CancelCommand.Execute(null);
            _viewModel.Message = "正在取消本次操作，完成后可以关闭窗口。"; return;
        }
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        PageUnderline.Dispose();
        foreach (var surface in PageHost.Children.OfType<FrameworkElement>()) PageMotion.Reset(surface);
        _themes.ThemeChanged -= ThemeChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_readingViewModel != null) _readingViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_sync != null) _sync.Updated -= SyncStatusChanged;
        _learningPage?.Dispose(); NotePreview.Dispose();
        if (_readingPage != null) { _readingPage.WordCaptureRequested -= ReadingWordCaptureRequested; _readingPage.CaptureLinkRequested -= Preview_LinkRequested; _readingPage.Dispose(); }
        _readingClient?.Dispose();
    }
}
