using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VocabCollector.Core;
namespace VocabCollector.Desktop;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ICaptureService _service;
    private CancellationTokenSource? _cancellation;
    private string _word = "", _context = "", _markdown = "", _message = "粘贴一个陌生单词，点击解析并保存。";
    private bool _busy;
    private NoteLocation? _selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public MainViewModel(ICaptureService service, AppSettings settings)
    {
        _service = service; Settings = settings;
        GenerateCommand = new(GenerateAsync, () => !IsBusy);
        CancelCommand = new(() => _cancellation?.Cancel(), () => IsBusy);
    }
    public AppSettings Settings { get; private set; }
    public string Word { get => _word; set => Set(ref _word, value); }
    public string Context { get => _context; set => Set(ref _context, value); }
    public string Markdown { get => _markdown; private set => Set(ref _markdown, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public bool IsBusy
    {
        get => _busy;
        private set { Set(ref _busy, value); Changed(nameof(CanConfigure)); Changed(nameof(CanOpen)); GenerateCommand.Refresh(); CancelCommand.Refresh(); }
    }
    public bool CanConfigure => !IsBusy;
    public bool CanOpen => !IsBusy && SelectedNote != null;
    public ObservableCollection<NoteLocation> Notes { get; } = [];
    public NoteLocation? SelectedNote { get => _selected; set { Set(ref _selected, value); Changed(nameof(CanOpen)); } }
    public AsyncCommand GenerateCommand { get; }
    public ActionCommand CancelCommand { get; }
    public void ApplySettings(AppSettings settings)
    {
        if (IsBusy) throw new InvalidOperationException("解析过程中不能变更设置。");
        Settings = settings; Message = "设置已保存，可以开始收录单词。";
    }
    public async Task GenerateAsync()
    {
        if (IsBusy) return;
        _cancellation = new CancellationTokenSource();
        IsBusy = true; Markdown = ""; Notes.Clear(); SelectedNote = null; Message = "正在检查输入与词库…";
        var progress = new Progress<CaptureStage>(stage =>
        {
            if (IsBusy) Message = stage switch { CaptureStage.CallingAi => "AI 正在解析，请稍候…", CaptureStage.Saving => "正在保存笔记…", _ => "正在检查输入与词库…" };
        });
        try
        {
            var result = await _service.CaptureAsync(new(Word, Context), Settings, progress, _cancellation.Token);
            Markdown = result.Markdown; Message = result.Message;
            foreach (var note in result.Notes) Notes.Add(note);
            SelectedNote = Notes.FirstOrDefault();
        }
        catch (OperationCanceledException) { Message = "已取消，输入已保留。"; }
        catch (Exception) { Message = "处理失败，请检查配置后重试。输入已保留。"; }
        finally { IsBusy = false; _cancellation.Dispose(); _cancellation = null; }
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Changed(name); }
    private void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
