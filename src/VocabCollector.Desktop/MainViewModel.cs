using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using VocabCollector.Core;
using VocabCollector.Core.Storage;
namespace VocabCollector.Desktop;

public sealed record SavedWordCapture(CaptureResult Result, AppSettings Settings);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ICaptureService _service;
    private readonly ILinkMaintenance? _maintenance;
    private CancellationTokenSource? _cancellation;
    private string _word = "", _context = "", _markdown = "", _message = "粘贴一个陌生单词，点击解析并保存。";
    private string? _articleContextWord;
    private bool _busy;
    private NoteLocation? _selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<SavedWordCapture>? CaptureSaved;
    public MainViewModel(ICaptureService service, AppSettings settings, ILinkMaintenance? maintenance = null)
    {
        _service = service; _maintenance = maintenance; Settings = settings;
        GenerateCommand = new(GenerateAsync, () => !IsBusy);
        CancelCommand = new(() => _cancellation?.Cancel(), () => IsBusy);
        RefreshLinksCommand = new(RefreshLinksAsync, () => !IsBusy && _maintenance != null);
    }
    public AppSettings Settings { get; private set; }
    public string Word
    {
        get => _word;
        set
        {
            if (_word == value) return;
            if (_articleContextWord is { } sourceWord && !SameWord(sourceWord, value))
            {
                _articleContextWord = null;
                // Clear before notifying Word bindings so no observer submits the
                // replacement word with the previous article's automatic sentence.
                Context = "";
            }
            Set(ref _word, value);
        }
    }
    public string Context
    {
        get => _context;
        set
        {
            if (_context == value) return;
            _articleContextWord = null; // A real manual edit owns its new sentence.
            Set(ref _context, value);
        }
    }
    public string Markdown { get => _markdown; private set => Set(ref _markdown, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public bool IsBusy
    {
        get => _busy;
        private set { Set(ref _busy, value); Changed(nameof(CanConfigure)); Changed(nameof(CanOpen)); GenerateCommand.Refresh(); CancelCommand.Refresh(); RefreshLinksCommand.Refresh(); }
    }
    public bool CanConfigure => !IsBusy;
    public bool CanOpen => !IsBusy && SelectedNote != null;
    public ObservableCollection<NoteLocation> Notes { get; } = [];
    public NoteLocation? SelectedNote { get => _selected; set { Set(ref _selected, value); Changed(nameof(CanOpen)); } }
    public AsyncCommand GenerateCommand { get; }
    public AsyncCommand RefreshLinksCommand { get; }
    public ActionCommand CancelCommand { get; }
    public bool PrepareDraft(string word, string context)
    {
        if (IsBusy) return false;
        _articleContextWord = null;
        Word = word; Context = context;
        _articleContextWord = word;
        Markdown = ""; Notes.Clear(); SelectedNote = null; Message = "";
        return true;
    }
    public async Task RefreshLinksAsync()
    {
        if (IsBusy || _maintenance == null) return;
        if (string.IsNullOrWhiteSpace(Settings.Vault.RootPath))
        {
            Message = "请先在设置中选择本地词库目录，再刷新词条链接。";
            return;
        }
        _cancellation = new CancellationTokenSource();
        IsBusy = true; Message = "正在刷新词条链接（本地处理，不调用 AI）…";
        try
        {
            var result = await _maintenance.RefreshLinksAsync(Settings.Vault, _cancellation.Token);
            Message = $"链接刷新完成：更新 {result.Updated} 篇，跳过 {result.Skipped} 篇，失败 {result.Failed} 篇。";
            if (SelectedNote is { } selected && Markdown.Length > 0)
            {
                try
                {
                    var root = Settings.Vault.RootPath;
                    var token = _cancellation.Token;
                    var updatedPreview = await Task.Run(async () =>
                    {
                        VaultPaths.EnsureInside(root, selected.AbsolutePath);
                        if (!File.Exists(selected.AbsolutePath)) throw new FileNotFoundException();
                        return await File.ReadAllTextAsync(selected.AbsolutePath, token);
                    }, token);
                    if (SelectedNote == selected) Markdown = updatedPreview;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException or NotSupportedException)
                {
                    Message += " 预览未更新，可在词库中重新打开笔记。";
                }
            }
        }
        catch (OperationCanceledException) { Message = "已取消链接刷新，已完成的更新已保留。"; }
        catch (Exception) { Message = "链接刷新失败，请检查词库目录或文件是否可写。已有笔记已保留。"; }
        finally { IsBusy = false; _cancellation.Dispose(); _cancellation = null; }
    }
    public async Task InitializeAsync(string warning = "")
    {
        if (!string.IsNullOrWhiteSpace(warning)) Message = warning;
        if (_maintenance == null || string.IsNullOrWhiteSpace(Settings.Vault.RootPath)) return;
        await RefreshLinksAsync();
        if (!string.IsNullOrWhiteSpace(warning)) Message = warning + " " + Message;
    }
    public void ApplySettings(AppSettings settings)
    {
        if (IsBusy) throw new InvalidOperationException("处理过程中不能变更设置。");
        Settings = settings; Message = "设置已保存，可以开始收录单词。";
    }
    public async Task GenerateAsync()
    {
        if (IsBusy) return;
        _cancellation = new CancellationTokenSource();
        IsBusy = true; Markdown = ""; Notes.Clear(); SelectedNote = null; Message = "正在检查输入与词库…";
        var progress = new Progress<CaptureStage>(stage =>
        {
            if (IsBusy) Message = stage switch { CaptureStage.CallingAi => "AI 正在解析，请稍候…", CaptureStage.ReceivingAi => "AI 正在生成笔记内容…", CaptureStage.Saving => "正在保存笔记…", _ => "正在检查输入与词库…" };
        });
        try
        {
            var submittedWord = Word;
            var submittedSettings = Settings;
            var result = await _service.CaptureAsync(new(submittedWord, Context), submittedSettings, progress, _cancellation.Token);
            Markdown = result.Markdown; Message = result.Message;
            if (result.Status == CaptureStatus.AlreadyExists && Markdown.Length == 0 && result.Notes.FirstOrDefault() is { } existing)
            {
                try
                {
                    VaultPaths.EnsureInside(VaultPaths.WordRoot(submittedSettings.Vault), existing.AbsolutePath);
                    Markdown = await File.ReadAllTextAsync(existing.AbsolutePath, _cancellation.Token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { Message += " 原笔记暂不可读取，请在词库中刷新查看。"; }
            }
            if (result.Status is CaptureStatus.Saved or CaptureStatus.AlreadyExists && result.CorrectedFrom.Length > 0 && Word == submittedWord)
            {
                // A verified correction is still the same submitted occurrence.
                if (_articleContextWord is not null) _articleContextWord = result.Word;
                Word = result.Word;
            }
            foreach (var note in result.Notes) Notes.Add(note);
            SelectedNote = Notes.FirstOrDefault();
            if (result.Status == CaptureStatus.Saved) NotifyCaptureSaved(result, submittedSettings);
        }
        catch (OperationCanceledException) { Message = "已取消，输入已保留。"; }
        catch (Exception) { Message = "处理失败，请检查配置后重试。输入已保留。"; }
        finally { IsBusy = false; _cancellation.Dispose(); _cancellation = null; }
    }
    private void NotifyCaptureSaved(CaptureResult result, AppSettings settings)
    {
        if (CaptureSaved is not { } handlers) return;
        var capture = new SavedWordCapture(result, settings);
        foreach (EventHandler<SavedWordCapture> handler in handlers.GetInvocationList())
        {
            // The note is committed. Optional observers cannot turn that success into a capture failure.
            try { handler(this, capture); }
            catch (Exception) { }
        }
    }
    private static bool SameWord(string first, string second)
    {
        try { return WordInput.Normalize(first) == WordInput.Normalize(second); }
        catch (ArgumentException) { return string.Equals(first, second, StringComparison.Ordinal); }
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Changed(name); }
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
