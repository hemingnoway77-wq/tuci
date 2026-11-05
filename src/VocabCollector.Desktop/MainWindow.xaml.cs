using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using VocabCollector.Core;
using VocabCollector.Core.Reading;
using VocabCollector.Desktop.Preview;

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
    private HttpClient? _readingClient;
    private DailyReadingViewModel? _readingViewModel;
    private ReadingPage? _readingPage;
    private bool _initializingUi = true;
    private bool _syncingThemeOption;
    private bool _showingReading;

    public MainWindow(MainViewModel viewModel, ISettingsStore settingsStore, IAppNavigator navigator,
        IDailyReadingService? readingService = null, Action<string>? openArticle = null, ThemeManager? themeManager = null, DesktopSync? sync = null)
    {
        _themes = themeManager ?? new ThemeManager(Application.Current.Resources);
        InitializeComponent();
        _sync = sync;
        SyncButton.Visibility = sync == null ? Visibility.Collapsed : Visibility.Visible;
        if (sync != null)
        {
            SyncButton.ToolTip = sync.Status;
            sync.Updated += SyncStatusChanged;
        }
        Activated += (_, _) => _sync?.Request();
        _viewModel = viewModel;
        _store = settingsStore;
        _navigator = navigator;
        DataContext = viewModel;
        _readingService = readingService;
        _openArticle = openArticle;
        SelectThemeOption(_themes.CurrentTheme);
        _themes.ThemeChanged += (_, theme) =>
        {
            _syncingThemeOption = true;
            try { SelectThemeOption(theme); }
            finally { _syncingThemeOption = false; }
            TitleBarTheme.Apply(this);
        };
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        StatusMetaText.Text = DateTime.Now.ToString("yyyy.MM.dd") + " · 兔词";
        _initializingUi = false;
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        Loaded += (_, _) => UpdateStatus();
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void SelectThemeOption(AppTheme theme)
    {
        var option = theme switch
        {
            AppTheme.Pink => ThemeRoseOption,
            AppTheme.WarmNeutral => ThemeWarmOption,
            _ => ThemeMonoOption
        };
        option.IsChecked = true;
    }

    private async void ThemeOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializingUi || _syncingThemeOption || sender is not RadioButton { Tag: string name }) return;
        var theme = name switch
        {
            "Pink" => AppTheme.Pink,
            "WarmNeutral" => AppTheme.WarmNeutral,
            _ => AppTheme.Mono
        };
        try
        {
            if (!await _themes.SetThemeAsync(theme))
                SetMessage("主题已切换，但未能保存偏好；下次启动会恢复上次的主题。");
        }
        catch (Exception) { SetMessage("暂时无法切换主题，请稍后重试。"); }
    }

    private void CaptureTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializingUi) return;
        ShowCapture();
    }

    private void ReadingTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializingUi) return;
        ShowReading();
    }

    private void ShowCapture()
    {
        _showingReading = false;
        // Leaving the page stops hidden network work; articles are kept for a quick return.
        _readingPage?.Cancel();
        if (_readingPage is not null) _readingPage.Visibility = Visibility.Collapsed;
        CapturePage.Visibility = Visibility.Visible;
        CaptureTab.IsChecked = true;
        UpdateStatus();
    }

    private async void ShowReading()
    {
        EnsureReadingPage();
        _showingReading = true;
        CapturePage.Visibility = Visibility.Collapsed;
        if (_readingPage is not null) _readingPage.Visibility = Visibility.Visible;
        ReadingTab.IsChecked = true;
        UpdateStatus();
        try
        {
            if (_readingPage is not null) await _readingPage.EnsureLoadedAsync();
        }
        catch (Exception)
        {
            if (_readingViewModel is not null) _readingViewModel.Message = "文章获取失败，请点击「刷新文章」重试。";
        }
    }

    private void EnsureReadingPage()
    {
        if (_readingPage is not null) return;
        // Keep article networking separate from the model client and its credentials.
        var service = _readingService;
        if (service is null)
        {
            _readingClient ??= new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector", "reading");
            service = new DailyReadingService(_readingClient, cache);
        }
        _readingViewModel = new DailyReadingViewModel(service);
        _readingViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _readingPage = new ReadingPage(_readingViewModel, _openArticle);
        PageHost.Children.Add(_readingPage);
    }

    private void UpdateStatus()
    {
        StatusMessageText.Text = _showingReading
            ? _readingViewModel?.Message ?? ""
            : _viewModel.Message;
        var busy = _showingReading ? _readingViewModel?.IsBusy == true : _viewModel.IsBusy;
        StatusProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Message" or "IsBusy")
            UpdateStatus();
    }

    private void SetMessage(string message)
    {
        if (_showingReading && _readingViewModel is not null) _readingViewModel.Message = message;
        else _viewModel.Message = message;
    }

    private void SyncStatusChanged(object? sender, EventArgs args) => SyncButton.ToolTip = _sync?.Status;

    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_sync != null) new SyncWindow(_sync, _viewModel.Settings.Vault) { Owner = this }.ShowDialog();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        var dialog = new SettingsWindow(_viewModel.Settings, _store, _themes) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SavedSettings != null) _viewModel.ApplySettings(dialog.SavedSettings);
    }

    private void OpenNote_Click(object sender, RoutedEventArgs e) => Open(true);
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(false);

    private void Preview_LinkRequested(object? sender, PreviewLinkEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        try
        {
            if (!e.IsWikiLink && Uri.TryCreate(e.Target, UriKind.Absolute, out var web) && web.Scheme is "https" or "http")
            {
                Process.Start(new ProcessStartInfo(web.AbsoluteUri) { UseShellExecute = true });
                return;
            }
            var note = PreviewLinkResolver.Resolve(e.Target, _viewModel.Settings.Vault, _viewModel.SelectedNote);
            if (note is null) { _viewModel.Message = "这张关联词卡暂时无法打开，可以在 Obsidian 中查看。"; return; }
            _navigator.OpenNote(note);
        }
        catch (Exception) { _viewModel.Message = "无法打开这个链接，请确认目标笔记仍在词库中。"; }
    }

    private void Open(bool note)
    {
        if (_viewModel.SelectedNote == null) return;
        try { if (note) _navigator.OpenNote(_viewModel.SelectedNote); else _navigator.OpenFolder(_viewModel.SelectedNote); }
        catch (Exception) { _viewModel.Message = note ? "无法唤起 Obsidian。请确认已安装，并在 Obsidian 中打开此仓库；笔记已经保存在显示的位置。" : "无法打开文件夹，请检查它是否仍然存在。"; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.IsBusy) return;
        e.Cancel = true;
        _viewModel.CancelCommand.Execute(null);
        _viewModel.Message = "正在取消本次操作，完成后可以关闭窗口。";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_sync != null) _sync.Updated -= SyncStatusChanged;
        _readingPage?.Cancel();
        _readingClient?.Dispose();
    }
}
