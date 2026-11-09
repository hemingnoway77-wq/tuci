using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Reading;
using VocabCollector.Core.Storage;
using VocabCollector.Core.Sync;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;
using VocabCollector.Desktop.Reading;
using VocabCollector.Desktop.Controls;
using VocabCollector.Desktop.Updates;

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
    private CancellationTokenSource _navigationCancellation = new();
    private readonly Func<Task>? _prepareLocalData;
    private Task? _preparation;
    private readonly UpdateController? _updates;
    private UpdateWindow? _updateWindow;
    private VaultOptions _guestVault;
    private ApiOptions _guestApi;
    private bool _guestSettingsDirty;
    private readonly string _settingsDirectory;
    private long _accountGeneration;
    private IReadOnlyList<SyncStateEntry> _accountEntries = [];
    private readonly Dictionary<string, (long Revision, SyncStateEntry Entry)> _localStateChanges = new(StringComparer.Ordinal);
    private long _localStateRevision, _exportedLocalRevision, _exportedThemeRevision, _importedSyncGeneration;
    private string? _restoredPositionNoteId;
    private CancellationTokenSource _accountCancellation = new();
    private Task _accountTransition = Task.CompletedTask;
    private readonly bool _requireAccount;
    private bool _accountLocked;
    public Task AccountTransition => _accountTransition;

    public MainWindow(MainViewModel viewModel, ISettingsStore settingsStore, IAppNavigator navigator,
        IDailyReadingService? readingService = null, Action<string>? openArticle = null, ThemeManager? themeManager = null,
        DesktopSync? sync = null, string? learningStateDirectory = null,
        IArticleContentService? articleContentService = null, IWordTranslationService? wordTranslationService = null,
        Func<Task>? prepareLocalData = null, UpdateController? updateController = null,
        VaultOptions? guestVault = null, string? settingsDirectory = null, AppSettings? guestSettings = null, bool requireAccount = false)
    {
        if (requireAccount && sync?.CanUseAccount != true) throw new InvalidOperationException("请先登录账号，再打开兔词。");
        _requireAccount = requireAccount;
        _themes = themeManager ?? new ThemeManager(Application.Current.Resources);
        _viewModel = viewModel; _store = settingsStore; _navigator = navigator;
        _readingService = readingService; _openArticle = openArticle; _sync = sync;
        _learningStateDirectory = learningStateDirectory;
        _articleContentService = articleContentService; _wordTranslationService = wordTranslationService;
        _prepareLocalData = prepareLocalData;
        _updates = updateController;
        _guestVault = guestVault ?? viewModel.Settings.Vault;
        _guestApi = guestSettings?.Api ?? viewModel.Settings.Api;
        _settingsDirectory = settingsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector");
        InitializeComponent();
        DataContext = viewModel;
        UpdateButton.Visibility = _updates is null ? Visibility.Collapsed : Visibility.Visible;
        if (_updates is not null) _updates.Changed += UpdateStatusChanged;
        SyncButton.Visibility = sync == null ? Visibility.Collapsed : Visibility.Visible;
        if (sync != null)
        {
            SyncButton.ToolTip = sync.Status; sync.Updated += SyncStatusChanged;
            sync.BindingChanged += AccountBindingChanged;
            sync.ExportStateAsync = ExportAccountStateAsync;
            sync.GetNoteIdentitiesAsync = GetNoteIdentitiesAsync;
            sync.ImportStateAsync = ImportAccountStateAsync;
        }
        Activated += (_, _) => { _sync?.Request(); _learningPage?.RequestReload(false); };
        SelectThemeOption(_themes.CurrentTheme);
        _themes.ThemeChanged += ThemeChanged;
        _themes.PreferenceSaved += ThemePreferenceSaved;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        StatusMetaText.Text = DateTime.Now.ToString("yyyy.MM.dd") + " · 兔词";
        _initializingUi = false;
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
        Loaded += async (_, _) =>
        {
            UpdateStatus(); AlignNavigation(false);
            if (_prepareLocalData is not null) await PrepareForUseAsync();
            if (!_closed && _sync?.SignedIn == true)
            {
                try { await _sync.RestoreLocalStateAsync(); }
                catch (Exception) { SetMessage("本机同步状态暂不可用，原记录已保留。"); }
                _sync.Request();
            }
            // Update networking starts after the local startup surface has settled.
            if (!_closed && _updates is not null) await _updates.CheckAsync();
        };
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
        if (previous != page)
        {
            _navigationCancellation.Cancel(); _navigationCancellation.Dispose();
            _navigationCancellation = new();
        }
        if (_page == "reading" && page != "reading") _readingPage?.Cancel();
        _page = page;
        // These pages share one grid cell. Hidden retains their measured layout,
        // document and scroll position without drawing or accepting input.
        CapturePage.Visibility = page == "capture" ? Visibility.Visible : Visibility.Hidden;
        if (_readingPage != null) _readingPage.Visibility = page == "reading" ? Visibility.Visible : Visibility.Hidden;
        if (_learningPage != null) _learningPage.Visibility = page is "library" or "roaming" ? Visibility.Visible : Visibility.Hidden;
        _changingPage = true;
        try { (page switch { "library" => LibraryTab, "roaming" => RoamingTab, "reading" => ReadingTab, _ => CaptureTab }).IsChecked = true; }
        finally { _changingPage = false; }
        AlignNavigation(previous != page);
        UpdateStatus();
    }
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
            if (!_closed) AlignNavigation(false);
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
        var stateDirectory = _sync?.AccountStateDirectory is { } account ? Path.Combine(account, "library") : _learningStateDirectory;
        _learningPage = new LearningPage(_viewModel.Settings.Vault, stateDirectory) { Visibility = Visibility.Hidden };
        _learningPage.StatusChanged += (_, _) => UpdateStatus();
        _learningPage.ReaderRequested += (_, _) => SelectPage("library");
        _learningPage.WebLinkRequested += (_, url) => OpenArticle(url);
        _learningPage.LearningStateChanged += (_, _) => _sync?.Request();
        _learningPage.ReaderControl.ReadingPositionChanged += NoteReadingPositionChanged;
        _learningPage.ReaderControl.NoteChanged += (_, note) => RestoreNotePosition(note.Id);
        PageHost.Children.Add(_learningPage);
        return _learningPage;
    }
    private async Task ShowLearningAsync(bool roaming)
    {
        var learning = EnsureLearningPage();
        SelectPage(roaming ? "roaming" : "library");
        if (roaming) await learning.ShowRoamingAsync(_navigationCancellation.Token);
        else { learning.ShowLibrary(); await learning.EnsureLoadedAsync(); }
    }
    private async Task OpenInLibraryAsync(NoteLocation target)
    {
        var learning = EnsureLearningPage();
        SelectPage("library");
        learning.ShowLibrary();
        var navigation = _navigationCancellation.Token;
        await learning.EnsureLoadedAsync();
        if (_closed || navigation.IsCancellationRequested || !ReferenceEquals(learning, _learningPage)) return;
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
            _readingPage = new ReadingPage(_readingViewModel, OpenArticle, _articleContentService, _wordTranslationService) { Visibility = Visibility.Hidden };
            _readingPage.WordCaptureRequested += ReadingWordCaptureRequested;
            _readingPage.CaptureLinkRequested += Preview_LinkRequested;
            _readingPage.ReadingPositionChanged += ArticleReadingPositionChanged;
            _readingPage.ApplyReadingPositions(_accountEntries);
            PageHost.Children.Add(_readingPage);
        }
        return _readingPage;
    }
    private async Task ShowReadingAsync()
    {
        var reading = EnsureReadingPage();
        SelectPage("reading");
        var navigation = _navigationCancellation.Token;
        try { await reading.EnsureLoadedAsync(navigation); }
        catch (Exception) { if (!_closed && !navigation.IsCancellationRequested && _readingViewModel != null) _readingViewModel.Message = "文章获取失败，请点击「刷新文章」重试。"; }
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
    {
        if (_viewModel.IsBusy || _learningPage?.IsBusy == true || _readingPage?.CapturePreviewViewModel?.IsBusy == true) return;
        if (_sync != null) new SyncWindow(_sync, _viewModel.Settings.Vault) { Owner = this }.ShowDialog();
    }

    public void LockForSignIn()
    {
        if (_accountLocked || _closed) return;
        _accountLocked = true;
        _viewModel.CancelCommand.Execute(null);
        _readingPage?.Cancel();
        _navigationCancellation.Cancel(); _accountCancellation.Cancel();
        IsEnabled = false; Hide();
    }

    private void AccountBindingChanged(object? sender, EventArgs args)
    {
        if (_closed) return;
        if (_requireAccount)
        {
            if (_sync?.CanUseAccount != true) LockForSignIn();
            return;
        }
        _accountTransition = ChangeAccountAsync(_accountTransition);
    }
    private async Task ChangeAccountAsync(Task previousTransition)
    {
        var generation = ++_accountGeneration;
        var previousSettings = _viewModel.Settings;
        var accountDirectory = _sync?.AccountStateDirectory;
        var activeVault = _sync?.ActiveVault;
        var claimGuestSettings = _sync?.LastBindingWasRegistration == true && activeVault is not null &&
            VaultPaths.WordRoot(activeVault).Equals(VaultPaths.WordRoot(previousSettings.Vault), StringComparison.OrdinalIgnoreCase);
        _accountCancellation.Cancel(); _accountCancellation.Dispose(); _accountCancellation = new();
        _restoredPositionNoteId = null;
        if (_sync?.LastBindingWasRegistration == true) MigrateLocalLearningForRegistration();
        var guestChanged = _sync?.SignedIn == true && _sync.ActiveVault is { } bound &&
            VaultPaths.WordRoot(bound).Equals(VaultPaths.WordRoot(_guestVault), StringComparison.OrdinalIgnoreCase);
        if (guestChanged)
        {
            _guestVault = new VaultOptions(Path.Combine(_settingsDirectory, "guest-libraries", Guid.NewGuid().ToString("N")), "英语单词", true);
            _guestApi = new ApiOptions("", "", "");
            _guestSettingsDirty = true;
            Directory.CreateDirectory(_guestVault.RootPath);
        }
        _accountEntries = [];
        _localStateChanges.Clear();
        _localStateRevision = _exportedLocalRevision = _importedSyncGeneration = 0;
        _navigationCancellation.Cancel(); _navigationCancellation.Dispose(); _navigationCancellation = new();
        if (_learningPage is { } learning) { learning.Dispose(); PageHost.Children.Remove(learning); _learningPage = null; }
        if (_readingPage is { } reading) { reading.Dispose(); PageHost.Children.Remove(reading); _readingPage = null; }
        if (_readingViewModel is not null) _readingViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _readingViewModel = null;
        _viewModel.PrepareDraft("", "");
        // Remove the previous account's secret before any asynchronous profile load.
        _viewModel.ApplySettings(new AppSettings(new("", "", ""), activeVault ?? _guestVault));
        SelectPage("capture");
        try
        {
            await previousTransition;
            // A successful registration has claimed this guest scope. Finish
            // preserving its settings even if the user immediately signs out.
            if (claimGuestSettings && accountDirectory is not null && !File.Exists(Path.Combine(accountDirectory, "settings.json")))
                await new WindowsSettingsStore(accountDirectory).SaveAsync(previousSettings with { Vault = activeVault! }, CancellationToken.None);
            if (_closed || generation != _accountGeneration) return;
            if (_guestSettingsDirty)
            {
                await _store.SaveAsync(new AppSettings(_guestApi, _guestVault), CancellationToken.None);
                if (_closed || generation != _accountGeneration) return;
                _guestSettingsDirty = false;
            }
            if (accountDirectory is not null)
            {
                var accountStore = new WindowsSettingsStore(accountDirectory);
                var loaded = await accountStore.LoadAsync(_accountCancellation.Token);
                if (_closed || generation != _accountGeneration) return;
                _viewModel.ApplySettings(loaded.Settings with { Vault = activeVault! });
            }
            else
            {
                if (_closed || generation != _accountGeneration) return;
                _viewModel.ApplySettings(new AppSettings(_guestApi, activeVault ?? _guestVault));
            }
            await _themes.SwitchStoreAsync(new AppearanceSettingsStore(accountDirectory ?? _settingsDirectory));
            if (_closed || generation != _accountGeneration) return;
            if (_sync?.SignedIn == true) await _sync.RestoreLocalStateAsync(_accountCancellation.Token);
        }
        catch (Exception) { if (!_closed && generation == _accountGeneration) SetMessage("账号已切换，本机状态暂时无法恢复，请刷新后重试。"); }
    }

    private void MigrateLocalLearningForRegistration()
    {
        if (_sync?.AccountStateDirectory is not { } account || _sync.ActiveVault is not { } active ||
            !VaultPaths.WordRoot(active).Equals(VaultPaths.WordRoot(_viewModel.Settings.Vault), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var rootKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(VaultPaths.WordRoot(active).ToUpperInvariant())));
            var source = _learningPage?.LocalStateDirectory ?? new VocabCollector.Core.Library.LibraryCatalog(active,
                _learningStateDirectory is null ? null : Path.Combine(_learningStateDirectory, rootKey)).LocalStateDirectory;
            var target = Path.Combine(account, "library", rootKey);
            VaultPaths.EnsureInside(account, target);
            Directory.CreateDirectory(target);
            foreach (var name in new[] { "catalog.json", "roaming.json", "workspace-tabs.json" })
                if (File.Exists(Path.Combine(source, name)) && !File.Exists(Path.Combine(target, name)))
                {
                    VaultPaths.EnsureInside(source, Path.Combine(source, name));
                    File.Copy(Path.Combine(source, name), Path.Combine(target, name));
                }
            var appearance = Path.Combine(_settingsDirectory, "appearance.json");
            if (File.Exists(appearance) && !File.Exists(Path.Combine(account, "appearance.json")))
                File.Copy(appearance, Path.Combine(account, "appearance.json"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { SetMessage("旧学习记录暂未迁移，原文件已保留；可恢复目录权限后重试。"); }
    }

    private async Task<IEnumerable<SyncStateEntry>> ExportAccountStateAsync(IReadOnlyList<SyncedNote> notes, CancellationToken ct)
    {
        if (_closed) return [];
        var generation = _accountGeneration;
        var learning = EnsureLearningPage();
        await learning.SetCloudNotesAsync(notes);
        if (_closed || generation != _accountGeneration || !ReferenceEquals(learning, _learningPage)) return [];
        var learningEntries = await learning.ExportLearningStateAsync(_sync?.DeviceId ?? "", ct);
        if (_closed || generation != _accountGeneration) return [];
        // Take the local snapshot after all awaited preparation. A newer queued
        // choice must never be followed by an older snapshot of the same key.
        _exportedLocalRevision = _localStateRevision;
        _exportedThemeRevision = _themes.PreferenceRevision;
        return learningEntries.Concat(_localStateChanges.Values.Select(change => change.Entry)).ToArray();
    }
    private async Task<IReadOnlyDictionary<string, string>> GetNoteIdentitiesAsync(CancellationToken ct)
    {
        if (_closed || _sync?.ActiveVault is not { } active || !VaultPaths.WordRoot(active).Equals(VaultPaths.WordRoot(_viewModel.Settings.Vault), StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, string>();
        var learning = EnsureLearningPage();
        await learning.EnsureLoadedAsync();
        return learning.AllNotes.ToDictionary(note => note.RelativePath,
            note => learning.CloudNoteIds.GetValueOrDefault(note.Id) ?? note.Id, StringComparer.OrdinalIgnoreCase);
    }

    private async Task ImportAccountStateAsync(IReadOnlyList<SyncStateEntry> entries, CancellationToken ct)
    {
        if (_closed || _sync?.SignedIn != true) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _accountCancellation.Token);
        ct = linked.Token;
        var generation = _accountGeneration;
        var syncGeneration = _sync.StateSyncGeneration;
        var exchanged = syncGeneration != _importedSyncGeneration;
        var acknowledgedRevision = exchanged ? _exportedLocalRevision : 0;
        var themeRevision = exchanged ? _exportedThemeRevision : _themes.PreferenceRevision;
        var learning = EnsureLearningPage();
        await learning.SetCloudNotesAsync(_sync.Notes);
        if (_closed || generation != _accountGeneration || !ReferenceEquals(learning, _learningPage)) return;
        await learning.MergeLearningStateAsync(entries, ct);
        if (_closed || generation != _accountGeneration) return;
        foreach (var key in _localStateChanges.Where(pair => pair.Value.Revision <= acknowledgedRevision).Select(pair => pair.Key).ToArray())
            _localStateChanges.Remove(key);
        _importedSyncGeneration = syncGeneration;
        _accountEntries = WithLocalChanges(entries);
        if (learning.ReaderControl.Note is { } openNote) RestoreNotePosition(openNote.Id);
        var preference = _accountEntries.LastOrDefault(entry => entry.Key == "preference/theme" && !entry.Deleted);
        if (preference is not null && preference.Value.TryGetProperty("name", out var name) &&
            _themes.PreferenceRevision == themeRevision && !_themes.HasPendingPreferenceSave)
        {
            var theme = name.GetString() switch { "pink" => AppTheme.Pink, "mono" => AppTheme.Mono, _ => AppTheme.WarmNeutral };
            if (_themes.CurrentTheme != theme) await _themes.ApplyRemoteAsync(theme, ct);
        }
        if (_closed || generation != _accountGeneration) return;
        _accountEntries = WithLocalChanges(entries);
        _readingPage?.ApplyReadingPositions(_accountEntries);
    }

    private IReadOnlyList<SyncStateEntry> WithLocalChanges(IReadOnlyList<SyncStateEntry> entries)
    {
        var values = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        foreach (var change in _localStateChanges.Values) values[change.Entry.Key] = change.Entry;
        return values.Values.ToArray();
    }

    private async void ThemePreferenceSaved(object? sender, AppTheme theme)
    {
        if (_closed || _sync?.SignedIn != true) return;
        try
        {
            var value = new { name = theme switch { AppTheme.Pink => "pink", AppTheme.Mono => "mono", _ => "warmNeutral" } };
            RememberLocalEntry("preference/theme", value);
            await _sync.QueueStateAsync("preference/theme", value);
        }
        catch (Exception) { SetMessage("主题已保存在本机，云端同步尚未完成。"); }
    }

    private async void NoteReadingPositionChanged(object? sender, NoteReadingPosition position)
    {
        if (_closed || _sync?.SignedIn != true || _learningPage?.CloudNoteIds.GetValueOrDefault(position.NoteId) is not { } cloudId) return;
        try
        {
            var value = new { noteId = cloudId, progress = position.Progress, anchor = position.Anchor, title = position.Title };
            RememberLocalEntry("note-reading/" + cloudId, value);
            await _sync.QueueStateAsync("note-reading/" + cloudId, value);
        }
        catch (Exception) { SetMessage("阅读位置尚未同步，请在联网后重试。"); }
    }
    private void RestoreNotePosition(string localId)
    {
        if (_restoredPositionNoteId == localId) return;
        if (_learningPage?.CloudNoteIds.GetValueOrDefault(localId) is not { } cloudId) return;
        var state = _accountEntries.LastOrDefault(entry => entry.Key == "note-reading/" + cloudId && !entry.Deleted);
        if (state is null || !state.Value.TryGetProperty("progress", out var progress) || !progress.TryGetDouble(out var unit)) return;
        _restoredPositionNoteId = localId;
        var anchor = state.Value.TryGetProperty("anchor", out var location) ? location.GetString() : null;
        _learningPage.ReaderControl.RestoreReadingPosition(unit, anchor);
    }
    private async void ArticleReadingPositionChanged(object? sender, ArticleReadingPosition position)
    {
        if (_closed || _sync?.SignedIn != true || !Uri.TryCreate(position.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        try
        {
            var key = "reading/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(position.Url)));
            var value = new { url = position.Url, progress = position.Progress, anchor = position.Anchor, title = position.Title };
            RememberLocalEntry(key, value);
            await _sync.QueueStateAsync(key, value);
        }
        catch (Exception) { SetMessage("文章阅读位置尚未同步，请在联网后重试。"); }
    }
    private void RememberLocalEntry(string key, object value)
    {
        var entry = new SyncStateEntry(key, JsonSerializer.SerializeToElement(value));
        _localStateChanges[key] = (++_localStateRevision, entry);
        _accountEntries = _accountEntries.Where(item => item.Key != key).Append(entry).ToArray();
    }

    private void UpdateStatusChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        UpdateButton.Content = _updates?.Update is not null ? "发现新版本" : "检查更新";
        UpdateButton.ToolTip = _updates?.Message;
    }
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is null || _closed) return;
        if (_updateWindow is not null) { _updateWindow.Activate(); return; }
        var window = new UpdateWindow(_updates, UpdateInstallationBlockReason, Close) { Owner = this };
        _updateWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_updateWindow, window)) _updateWindow = null; };
        window.Show();
        if (_updates.State is UpdateState.Idle or UpdateState.Current or UpdateState.Failed) await _updates.CheckAsync();
    }
    private string? UpdateInstallationBlockReason()
    {
        if (_closed) return "主窗口已经关闭，请重新启动兔词后更新。";
        if (_preparation is { IsCompleted: false } || _viewModel.IsBusy || _readingPage?.CapturePreviewViewModel?.IsBusy == true
            || _learningPage?.IsBusy == true || _sync?.IsBusy == true)
            return "请等待当前收录、词库处理或同步完成，再安装更新。";
        if (!string.IsNullOrWhiteSpace(_viewModel.Word) || !string.IsNullOrWhiteSpace(_viewModel.Context))
            return "收录区还有输入。请先返回保存需要的内容并清空输入，再安装更新。";
        return null;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        var generation = _accountGeneration;
        var scopeStore = LocalAccountSettings.Store(_store, _sync?.AccountStateDirectory);
        var dialog = new SettingsWindow(_viewModel.Settings, scopeStore, _themes) { Owner = this };
        dialog.ShowDialog();
        if (dialog.SavedSettings is not { } saved) return;
        if (_closed || generation != _accountGeneration) return;
        if (_sync?.SignedIn == true && _sync.ActiveVault is { } accountVault)
        {
            saved = saved with { Vault = accountVault };
            try { await scopeStore.SaveAsync(saved, _accountCancellation.Token); }
            catch (Exception) { SetMessage("本机账号设置暂未保存，请检查配置目录权限。"); }
            if (_closed || generation != _accountGeneration) return;
        }
        else { _guestVault = saved.Vault; _guestApi = saved.Api; }
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
        if (_accountLocked) return;
        if (_viewModel.IsBusy)
        {
            e.Cancel = true; _viewModel.CancelCommand.Execute(null);
            _viewModel.Message = "正在取消本次操作，完成后可以关闭窗口。"; return;
        }
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _accountGeneration++;
        _accountCancellation.Cancel(); _accountCancellation.Dispose();
        if (_updates is not null) { _updates.Changed -= UpdateStatusChanged; _updates.Dispose(); }
        _navigationCancellation.Cancel(); _navigationCancellation.Dispose();
        PageUnderline.Dispose();
        _themes.ThemeChanged -= ThemeChanged;
        _themes.PreferenceSaved -= ThemePreferenceSaved;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_readingViewModel != null) _readingViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_sync != null)
        {
            _sync.Updated -= SyncStatusChanged; _sync.BindingChanged -= AccountBindingChanged;
            _sync.ExportStateAsync = null; _sync.ImportStateAsync = null; _sync.GetNoteIdentitiesAsync = null;
        }
        _learningPage?.Dispose(); NotePreview.Dispose();
        if (_readingPage != null) { _readingPage.WordCaptureRequested -= ReadingWordCaptureRequested; _readingPage.CaptureLinkRequested -= Preview_LinkRequested; _readingPage.Dispose(); }
        _readingClient?.Dispose();
    }
}
