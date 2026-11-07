using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Desktop.Learning;

public sealed record LibraryFolder(string Name, string RelativePath, IReadOnlyList<LibraryFolder> Children);

public partial class LearningPage : UserControl, INotifyPropertyChanged, IDisposable
{
    private readonly VaultOptions _vault;
    private readonly string? _stateDirectory;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly DispatcherTimer _refreshTimer;
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(30);
    private Task? _loadTask;
    private long _changeVersion, _loadedVersion = -1, _loadedTimestamp;
    private int _refreshQueued;
    private bool _hasLoaded;
    private string? _watchedWordRoot;
    private FileSystemWatcher? _watcher;
    private LibraryCatalog? _catalog;
    private RoamingSession? _session;
    private IReadOnlyList<LibraryNote> _notes = [];
    private IReadOnlyList<LibraryFolder> _folders = [];
    private LibraryNote? _selected;
    private RoamingExposure? _exposure;
    private string _query = "", _folder = "", _message = "本地词库可以直接阅读与复习。";
    private string _reviewError = "", _libraryWarnings = "";
    private bool _loading, _advancing, _disposed, _refreshingReader;

    public LearningPage(VaultOptions vault, string? stateDirectory = null, TimeProvider? clock = null)
    {
        _vault = vault;
        _stateDirectory = stateDirectory;
        _clock = clock ?? TimeProvider.System;
        InitializeComponent();
        DataContext = this;
        Browser.NoteChanged += Reader_NoteChanged;
        Browser.NavigationRequested += (_, _) => { if (!_refreshingReader) { ShowLibrary(); ReaderRequested?.Invoke(this, EventArgs.Empty); } };
        Browser.MessageChanged += (_, message) => Message = message;
        Browser.WebLinkRequested += (_, url) => WebLinkRequested?.Invoke(this, url);
        Browser.ReloadRequested += (_, _) => RequestReload();
        Browser.ImportRequested += async (_, files) => await ImportAsync(files);
        InitializeRoaming();
        _refreshTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _refreshTimer.Tick += async (_, _) => { _refreshTimer.Stop(); await EnsureLoadedAsync(); };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? StatusChanged;
    public event EventHandler? ReaderRequested;
    public event EventHandler<string>? WebLinkRequested;
    public LibraryBrowser BrowserControl => Browser;
    public NoteReader ReaderControl => Browser.Reader;
    public IReadOnlyList<LibraryNote> AllNotes => _notes;
    public IReadOnlyList<LibraryFolder> Folders => _folders;
    public ObservableCollection<LibraryNote> VisibleNotes { get; } = [];
    public LibraryNote? SelectedNote { get => _selected; set { _selected = value; Changed(nameof(SelectedNote)); } }
    public string Query { get => _query; set { if (_query == value) return; _query = value; Changed(nameof(Query)); Filter(); } }
    public bool IsBusy => _loading || _advancing;
    public bool CanInteract => !IsBusy;
    public bool HasExposure => _exposure != null;
    public bool CanAdvance => HasExposure && !_advancing && !_disposed;
    public string Message { get => _message; private set { _message = value; StatusChanged?.Invoke(this, EventArgs.Empty); } }
    public string FolderLabel => _folder.Length == 0 ? "全部笔记" : _folder;
    public string Summary => $"{VisibleNotes.Count} 篇笔记 · {_notes.Count(n => n.IsWord)} 个可复习词条";
    public string EmptyMessage => _notes.Count == 0 ? "还没有笔记。可以先收录单词，或导入已有 Markdown。" : "当前目录没有匹配的笔记。";
    public Visibility EmptyVisibility => VisibleNotes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ReaderTitle => Browser.Reader.Note?.Title ?? "笔记阅读";
    public Visibility ReaderEmptyVisibility => Browser.Reader.Note == null ? Visibility.Visible : Visibility.Collapsed;
    public string RoamingTitle => _exposure?.Note.Title ?? "还没有可复习的单词";
    public string RoamingPhonetics => _exposure?.Note.Phonetics ?? "";
    public string RoamingMeaning => _exposure is { } card ? (card.Note.Meaning.Length > 0 ? card.Note.Meaning : card.Note.Excerpt) : "";
    public string RememberedLabel => _exposure?.RememberedToday == true ? "✓ 今天记住过" : "";
    public string RoamingCountLabel => $"词库里有 {_notes.Count(n => n.IsWord)} 个词，随时可以继续。";
    public string RoamingWeightLabel => WeightDescription;

    /// <summary>Prepare local notes on the UI dispatcher without drawing a roaming exposure.</summary>
    public Task EnsureLoadedAsync(bool forceRefresh = false)
    {
        if (_disposed) return Task.CompletedTask;
        if (forceRefresh) Interlocked.Increment(ref _changeVersion);
        if (_loadTask is { IsCompleted: false }) return _loadTask;
        if (!NeedsReload()) return Task.CompletedTask;
        _refreshTimer.Stop();
        return _loadTask = LoadUntilCurrentAsync();
    }

    public Task ReloadAsync() => EnsureLoadedAsync(forceRefresh: true);

    private bool NeedsReload() => !_hasLoaded || _loadedVersion != Interlocked.Read(ref _changeVersion) ||
        _clock.GetElapsedTime(_loadedTimestamp) >= FreshnessWindow;

    private async Task LoadUntilCurrentAsync()
    {
        _loading = true; BusyChanged();
        try
        {
            do
            {
                var version = Interlocked.Read(ref _changeVersion);
                if (!await ReloadOnceAsync() || _disposed) return;
                _hasLoaded = true;
                _loadedVersion = version;
                _loadedTimestamp = _clock.GetTimestamp();
                // Changes arriving while a snapshot was read/applied are part of
                // this same task. Do not publish them as already refreshed.
            } while (_loadedVersion != Interlocked.Read(ref _changeVersion));
        }
        finally { _loading = false; BusyChanged(); }
    }

    private async Task<bool> ReloadOnceAsync()
    {
        if (_disposed) return false;
        try
        {
            await _scanGate.WaitAsync(_stop.Token);
        }
        catch (OperationCanceledException) { return false; }
        try
        {
            if (_disposed) return false;
            if (_catalog == null)
            {
                var state = _stateDirectory == null ? null : Path.Combine(_stateDirectory,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(VaultPaths.WordRoot(_vault).ToUpperInvariant()))));
                _catalog = new LibraryCatalog(_vault, state);
            }
            // Watch before scanning, including an empty library whose notes
            // directory has not been created yet, so first-load changes are seen.
            try { StartWatching(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
            if (_session == null)
            {
                try
                {
                    var session = await Task.Run(() => new RoamingSession(Path.Combine(_catalog.LocalStateDirectory, "roaming.json"), _clock), _stop.Token);
                    if (_disposed) return false;
                    _session = session;
                    _reviewError = "";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                { _reviewError = "本机复习记录暂时无法读取，原记录已保留；词库仍可正常阅读。请检查文件后刷新词库。"; }
            }
            var snapshot = await Task.Run(() => _catalog.ScanAsync(_stop.Token), _stop.Token);
            if (_disposed) return false;
            _notes = snapshot.Notes;
            _folders = BuildFolders(_notes);
            Changed(nameof(Folders)); Changed(nameof(AllNotes));
            _refreshingReader = true;
            try { Browser.Configure(_catalog, _notes, VaultPaths.WordRoot(_vault)); }
            finally { _refreshingReader = false; }
            Filter();
            Changed(nameof(ReaderTitle)); Changed(nameof(ReaderEmptyVisibility));
            Changed(nameof(RoamingCountLabel));
            RoamingPreviewReader.Configure(_catalog, _notes);
            UpdateRoamingStatistics();
            _libraryWarnings = string.Join(" ", snapshot.Warnings);
            Message = WithWarnings($"词库已更新：{_notes.Count} 篇笔记。");
            Browser.SetMessage(Message);
            return snapshot.Warnings.Count == 0 && _reviewError.Length == 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { Message = "词库暂时无法读取，请检查设置中的目录和文件权限。已有笔记保持不变。"; Browser.SetMessage(Message); }
        finally { _scanGate.Release(); }
        return false;
    }

    private void Filter()
    {
        var id = _selected?.Id;
        var query = _query.Trim();
        VisibleNotes.Clear();
        foreach (var note in _notes.Where(n => (_folder.Length == 0 || n.RelativePath.StartsWith(_folder + "/", StringComparison.OrdinalIgnoreCase)) &&
            (query.Length == 0 || n.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || n.Markdown.Contains(query, StringComparison.CurrentCultureIgnoreCase))))
            VisibleNotes.Add(note);
        SelectedNote = VisibleNotes.FirstOrDefault(n => n.Id == id);
        Changed(nameof(Summary)); Changed(nameof(EmptyVisibility)); Changed(nameof(EmptyMessage));
    }

    private static IReadOnlyList<LibraryFolder> BuildFolders(IReadOnlyList<LibraryNote> notes)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            var parts = note.RelativePath.Split('/');
            for (var i = 1; i < parts.Length; i++) paths.Add(string.Join('/', parts.Take(i)));
        }
        IReadOnlyList<LibraryFolder> Children(string prefix) => paths.Where(p =>
                (p.Contains('/') ? p[..p.LastIndexOf('/')] : "") == prefix)
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new LibraryFolder(p.Split('/').Last(), p, Children(p))).ToArray();
        return [new("全部笔记", "", Children(""))];
    }

    private void Folder_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not LibraryFolder folder) return;
        _folder = folder.RelativePath;
        Changed(nameof(FolderLabel));
        Filter();
    }
    private void Note_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingReader || _loading || e.AddedItems.OfType<LibraryNote>().FirstOrDefault() is not { } note || ReaderControl.Note?.Id == note.Id) return;
        OpenNote(note);
    }
    public void OpenNote(LibraryNote note)
    {
        if (_disposed || _catalog == null) return;
        ShowLibrary();
        Browser.OpenNote(note);
    }
    public void ShowLibrary()
    {
        RoamingMeaningReveal.Hide();
        LibrarySurface.Visibility = Visibility.Visible;
        RoamingSurface.Visibility = Visibility.Collapsed;
    }
    public async Task ShowRoamingAsync()
    {
        await EnsureLoadedAsync();
        if (_disposed) return;
        LibrarySurface.Visibility = Visibility.Collapsed;
        RoamingSurface.Visibility = Visibility.Visible;
        await AdvanceAsync(null, false);
    }
    private void Reader_NoteChanged(object? sender, LibraryNote note)
    {
        SelectedNote = VisibleNotes.FirstOrDefault(n => n.Id == note.Id);
        Changed(nameof(ReaderTitle)); Changed(nameof(ReaderEmptyVisibility));
    }
    public void HandleLink(PreviewLinkEventArgs args, NoteLocation? source, FrameworkElement? placementTarget = null)
    {
        if (_catalog == null) return;
        var current = source == null ? null : _notes.FirstOrDefault(n => string.Equals(n.AbsolutePath, source.AbsolutePath, StringComparison.OrdinalIgnoreCase));
        Browser.HandleLink(args, current, placementTarget);
    }

    private async Task AdvanceAsync(Guid? finishing, bool remembered)
    {
        if (_advancing || _disposed) return;
        if (_session == null) { Message = _reviewError.Length > 0 ? _reviewError : "复习记录暂时不可用，请刷新词库后重试。"; return; }
        _advancing = true; BusyChanged();
        RoamingMeaningReveal.Hide();
        try
        {
            var next = await _session.NextAsync(_notes, finishing, remembered, _stop.Token);
            if (_disposed) return;
            _exposure = next;
            RoamingMeaningReveal.Hide();
            UpdateCard();
            UpdateRoamingStatistics();
            if (_previewOpen && next is not null) DisplayRoamingPreview(next.Note);
            Message = WithWarnings(next == null ? "还没有可复习的英文词条。" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
        { Message = "复习记录暂未保存，当前卡片已保留，请重试。"; }
        finally { _advancing = false; BusyChanged(); }
    }
    private async void Skip_Click(object sender, RoutedEventArgs e) { if (_exposure is { } card) await AdvanceAsync(card.Id, false); }
    private async void Remember_Click(object sender, RoutedEventArgs e) { if (_exposure is { } card) await AdvanceAsync(card.Id, true); }
    private void RoamingRead_Click(object sender, RoutedEventArgs e) => OpenRoamingPreview();
    private void UpdateCard()
    {
        foreach (var name in new[] { nameof(HasExposure), nameof(CanAdvance), nameof(RoamingTitle), nameof(RoamingPhonetics), nameof(RoamingMeaning), nameof(RememberedLabel), nameof(RoamingWeightLabel), nameof(RoamingCountLabel) }) Changed(name);
        Changed(nameof(WeightLevel)); Changed(nameof(WeightDescription));
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "导入 Markdown 笔记", Filter = "Markdown 笔记|*.md", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await ImportAsync(picker.FileNames);
    }
    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (_catalog == null) await ReloadAsync();
        if (_catalog == null || IsBusy) return;
        _loading = true; BusyChanged();
        try
        {
            var imported = await Task.Run(() => _catalog.ImportAsync(paths, _stop.Token), _stop.Token);
            _loading = false;
            await ReloadAsync();
            Message = WithWarnings($"已导入 {imported.Count} 篇 Markdown，原文件保持不变。");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { Message = "导入未全部完成：同名笔记不会覆盖，请刷新词库查看已导入的内容。"; }
        finally { _loading = false; BusyChanged(); }
    }

    private void StartWatching()
    {
        if (_disposed) return;
        if (_watcher is not null && Directory.Exists(_watcher.Path)) return;
        _watcher?.Dispose(); _watcher = null;
        var root = VaultPaths.WordRoot(_vault);
        var watchRoot = Directory.Exists(root) ? root : _vault.RootPath;
        if (!Directory.Exists(watchRoot)) return;
        _watchedWordRoot = root;
        _watcher = new(watchRoot, "*") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName };
        _watcher.Changed += Watched;
        _watcher.Created += Watched;
        _watcher.Deleted += Watched;
        _watcher.Renamed += Watched;
        _watcher.Error += (_, _) => RequestReload();
        _watcher.EnableRaisingEvents = true;
    }
    private void Watched(object sender, FileSystemEventArgs e)
    {
        if (_disposed || sender is not FileSystemWatcher || _watchedWordRoot is not { } root) return;
        bool Relevant(string path)
        {
            var relative = Path.GetRelativePath(root, path);
            if (relative == ".") return true;
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.'));
        }
        if (Relevant(e.FullPath) || e is RenamedEventArgs renamed && Relevant(renamed.OldFullPath)) RequestReload();
    }
    public void RequestReload(bool forceRefresh = true)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        if (forceRefresh) Interlocked.Increment(ref _changeVersion);
        // A sync batch can raise hundreds of watcher events before the UI gets
        // a turn. Queue one dispatcher operation, then debounce the actual scan.
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_disposed) return;
            _refreshTimer.Stop();
            if (NeedsReload()) _refreshTimer.Start();
        }));
    }
    private void BusyChanged()
    {
        if (_disposed) return;
        Changed(nameof(IsBusy)); Changed(nameof(CanInteract)); Changed(nameof(CanAdvance));
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
    private string WithWarnings(string message) => string.Join(" ", new[] { message, _libraryWarnings, _reviewError }.Where(s => s.Length > 0));
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel(); _refreshTimer.Stop(); _watcher?.Dispose(); Browser.Dispose();
        DisposeRoaming();
    }
}
