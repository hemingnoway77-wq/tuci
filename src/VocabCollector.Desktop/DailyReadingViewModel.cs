using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using VocabCollector.Core.Reading;

namespace VocabCollector.Desktop;

public sealed class DailyReadingViewModel : INotifyPropertyChanged
{
    private readonly IDailyReadingService _service;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _cancellation;
    private int _activeRequest;
    private bool _busy, _isFromCache, _isOffline;
    private DateOnly _day;
    private DateOnly? _lastAttemptDay;
    private string _message = "打开即获取今日阅读，同日优先使用缓存。";

    public DailyReadingViewModel(IDailyReadingService service, TimeProvider? clock = null)
    {
        _service = service;
        _clock = clock ?? TimeProvider.System;
        _day = LocalToday;
        RefreshCommand = new(() => LoadAsync(true), () => !IsBusy);
        CancelCommand = new(Cancel, () => IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ReadingArticle> Articles { get; } = [];
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            RefreshCommand.Refresh();
            CancelCommand.Refresh();
        }
    }
    public bool IsFromCache { get => _isFromCache; private set => Set(ref _isFromCache, value); }
    public bool IsOffline { get => _isOffline; private set => Set(ref _isOffline, value); }
    public string DayLabel => _day.ToString("yyyy 年 MM 月 dd 日 · dddd", CultureInfo.GetCultureInfo("zh-CN"));
    public string Composition => Articles.Count == 0
        ? "尚未获取今日文章"
        : $"{Articles.Count(article => article.Category == ReadingCategory.General)} 篇通用泛读 · {Articles.Count(article => article.Category == ReadingCategory.Technology)} 篇技术阅读";
    public string Message { get => _message; set => Set(ref _message, value); }
    public AsyncCommand RefreshCommand { get; }
    public ActionCommand CancelCommand { get; }
    private DateOnly LocalToday => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
    public Task RefreshIfNewDayAsync() => _lastAttemptDay == LocalToday ? Task.CompletedTask : LoadAsync();
    public async Task LoadAsync(bool forceRefresh = false)
    {
        if (Interlocked.CompareExchange(ref _activeRequest, 1, 0) != 0) return;
        _lastAttemptDay = LocalToday;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        Message = "正在获取今日阅读，稍等片刻…";
        try
        {
            var result = await _service.GetAsync(forceRefresh, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _day = result.Day;
            Changed(nameof(DayLabel));
            Articles.Clear();
            foreach (var article in result.Articles) Articles.Add(article);
            Changed(nameof(Composition));
            IsFromCache = result.IsFromCache;
            IsOffline = result.IsOffline;
            Message = result.Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Message = Articles.Count > 0 ? "已取消获取，已有文章已保留。" : "已取消获取，随时可以刷新重试。";
        }
        catch (Exception)
        {
            Message = "网络暂时不可用，请检查连接后点击“刷新文章”重试。"
                + (Articles.Count > 0 ? "已有文章已保留。" : "");
        }
        finally
        {
            _cancellation = null;
            Interlocked.Exchange(ref _activeRequest, 0);
            IsBusy = false;
        }
    }

    public void Cancel()
    {
        try { _cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name);
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
