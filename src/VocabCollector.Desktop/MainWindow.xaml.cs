using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using VocabCollector.Core;
using VocabCollector.Core.Reading;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;
using VocabCollector.Desktop.Reading;

namespace VocabCollector.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ISettingsStore _store;
    private readonly IAppNavigator _navigator;
    private readonly IDailyReadingService? _readingService;
    private readonly Action<string>? _openArticle;
    private readonly ThemeManager _themes;
    private readonly DesktopSync? _sync;
    private readonly string? _learningStateDirectory;
    private HttpClient? _readingClient;
    private DailyReadingViewModel? _readingViewModel;
    private ReadingPage? _readingPage;
    private LearningPage? _learningPage;
    private bool _initializingUi = true, _syncingThemeOption, _changingPage, _closed;
    private string _page = "capture";

    public MainWindow(MainViewModel viewModel, ISettingsStore settingsStore, IAppNavigator navigator,
        IDailyReadingService? readingService = null, Action<string>? openArticle = null, ThemeManager? themeManager = null,
        DesktopSync? sync = null, string? learningStateDirectory = null)
    {
        _themes = themeManager ?? new ThemeManager(Application.Current.Resources);
        _viewModel = viewModel; _store = settingsStore; _navigator = navigator;
        _readingService = readingService; _openArticle = openArticle; _sync = sync;
        _learningStateDirectory = learningStateDirectory;
        InitializeComponent();
        DataContext = viewModel;
        SyncButton.Visibility = sync == null ? Visibility.Collapsed : Visibility.Visible;
        if (sync != null) { SyncButton.ToolTip = sync.Status; sync.Updated += SyncStatusChanged; }
        Activated += (_, _) => { _sync?.Request(); _learningPage?.RequestReload(); };
        SelectThemeOption(_themes.CurrentTheme);
        _themes.ThemeChanged += ThemeChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        StatusMetaText.Text = DateTime.Now.ToString("yyyy.MM.dd") + " · 兔词";
        _initializingUi = false;
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        Loaded += (_, _) => UpdateStatus();
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
        if (_page == "reading" && page != "reading") _readingPage?.Cancel();
        _page = page;
        CapturePage.Visibility = page == "capture" ? Visibility.Visible : Visibility.Collapsed;
        if (_readingPage != null) _readingPage.Visibility = page == "reading" ? Visibility.Visible : Visibility.Collapsed;
        if (_learningPage != null) _learningPage.Visibility = page is "library" or "roaming" ? Visibility.Visible : Visibility.Collapsed;
        _changingPage = true;
        try { (page switch { "library" => LibraryTab, "roaming" => RoamingTab, "reading" => ReadingTab, _ => CaptureTab }).IsChecked = true; }
        finally { _changingPage = false; }
        UpdateStatus();
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
        await learning.ReloadAsync();
        if (_closed || !ReferenceEquals(learning, _learningPage)) return;
        if (roaming && _page == "roaming") await learning.ShowRoamingAsync();
    }
    private async Task OpenInLibraryAsync(NoteLocation target)
    {
        var learning = EnsureLearningPage();
        await learning.ReloadAsync();
        if (_closed || !ReferenceEquals(learning, _learningPage)) return;
        var note = learning.AllNotes.FirstOrDefault(n => n.AbsolutePath.Equals(target.AbsolutePath, StringComparison.OrdinalIgnoreCase));
        if (note == null) { SetMessage("这篇笔记暂时无法读取，请刷新词库或检查目录。"); return; }
        learning.OpenNote(note);
        SelectPage("library");
    }
    private async Task ShowReadingAsync()
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
            _readingPage = new ReadingPage(_readingViewModel, OpenArticle);
            PageHost.Children.Add(_readingPage);
        }
        SelectPage("reading");
        try { await _readingPage.EnsureLoadedAsync(); }
        catch (Exception) { if (_readingViewModel != null) _readingViewModel.Message = "文章获取失败，请点击「刷新文章」重试。"; }
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
        StatusMessageText.Text = _page switch { "reading" => _readingViewModel?.Message ?? "", "library" or "roaming" => _learningPage?.Message ?? "", _ => _viewModel.Message };
        var busy = _page switch { "reading" => _readingViewModel?.IsBusy == true, "library" or "roaming" => _learningPage?.IsBusy == true, _ => _viewModel.IsBusy };
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
        await learning.ReloadAsync();
        if (!_closed && ReferenceEquals(learning, _learningPage) && source == _viewModel.SelectedNote && page == _page)
            learning.HandleLink(e, source, NotePreview);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        e.Cancel = true; _viewModel.CancelCommand.Execute(null);
        _viewModel.Message = "正在取消本次操作，完成后可以关闭窗口。";
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _themes.ThemeChanged -= ThemeChanged;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_readingViewModel != null) _readingViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_sync != null) _sync.Updated -= SyncStatusChanged;
        _learningPage?.Dispose(); NotePreview.Dispose();
        _readingPage?.Cancel(); _readingClient?.Dispose();
    }
}
