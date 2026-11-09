using System.IO;
using System.Net.Http;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using VocabCollector.Core.Reading;
using VocabCollector.Desktop.Reading;
using VocabCollector.Desktop.Preview;
using System.Windows.Media.Animation;
using System.Text.Json;
using VocabCollector.Core.Sync;

namespace VocabCollector.Desktop;

public partial class ReadingPage : UserControl, IDisposable
{
    private readonly DailyReadingViewModel _viewModel;
    private readonly Action<string> _openArticle;
    private readonly IArticleContentService _contentService;
    private readonly IWordTranslationService _translationService;
    private readonly bool _ownsContent, _ownsTranslation;
    private CancellationTokenSource? _articleCancellation, _wordCancellation;
    private ReadingWordSelection? _selectedWord;
    private long _articleVersion, _wordVersion;
    private double _directoryOffset;
    private bool _hasBody, _disposed;
    private Window? _wordOwner;
    private ArticleContent? _loadedContent;
    private readonly DispatcherTimer _positionTimer;
    private readonly Dictionary<string, ArticleReadingPosition> _readingPositions = new(StringComparer.Ordinal);
    private bool _restoringPosition;
    private readonly DependencyPropertyDescriptor _popupOpen = DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup));

    public ReadingPage(DailyReadingViewModel viewModel, Action<string>? openArticle = null,
        IArticleContentService? articleContentService = null, IWordTranslationService? wordTranslationService = null)
    {
        _viewModel = viewModel;
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(650) };
        _positionTimer.Tick += (_, _) => SaveReadingPosition();
        _openArticle = openArticle ?? (url => new ArticleWindow(url) { Owner = Window.GetWindow(this) }.Show());
        _ownsContent = articleContentService is null; _ownsTranslation = wordTranslationService is null;
        _contentService = articleContentService ?? new ArticleContentService(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VocabCollector", "reading", "articles"));
        InitializeComponent();
        _translationService = wordTranslationService ?? new OfflineDictionaryWordTranslationService(
            new JsonLinesWordDictionary(Path.Combine(AppContext.BaseDirectory, "Assets", "OfflineDictionary")),
            new OfflineWordTranslationService(new WebViewOfflineTranslationEngine(TranslationRuntimeHost)));
        DataContext = viewModel;
        ArticleBodyReader.WordClicked += OnWordClicked;
        ArticleBodyReader.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.VerticalChange != 0) CloseWordActions();
            if (e.VerticalChange == 0 || !_hasBody || _restoringPosition || !IsVisible || _disposed) return;
            _positionTimer.Stop(); _positionTimer.Start();
        }));
        PreviewKeyDown += CloseWordOnEscape;
        ReadingWordPopup.Child.PreviewKeyDown += CloseWordOnEscape;
        _popupOpen.AddValueChanged(ReadingWordPopup, OnWordPopupOpenChanged);
        IsVisibleChanged += (_, _) => { if (!IsVisible) { CloseWordActions(); SmoothScroll.Cancel(ArticleBodyReader); } };
        Unloaded += (_, _) => Cancel();
        SizeChanged += (_, _) => { if (IsCapturePreviewOpen) ReadingCaptureColumn.Width = new GridLength(Math.Clamp(ActualWidth * .32, 310, 370)); };
    }

    public DailyReadingViewModel ViewModel => _viewModel;
    public bool IsArticleOpen => CurrentArticle is not null;
    public ReadingArticle? CurrentArticle { get; private set; }
    public Popup WordActionsPopup => ReadingWordPopup;
    public event EventHandler<ReadingWordSelection>? WordCaptureRequested;
    public event EventHandler<PreviewLinkEventArgs>? CaptureLinkRequested;
    public event EventHandler<ArticleReadingPosition>? ReadingPositionChanged;
    public void ApplyReadingPositions(IEnumerable<SyncStateEntry> entries)
    {
        foreach (var entry in entries.Where(entry => entry.Key.StartsWith("reading/", StringComparison.Ordinal) && !entry.Deleted))
        {
            var value = entry.Value;
            if (!value.TryGetProperty("url", out var url) || !value.TryGetProperty("progress", out var progress) || !progress.TryGetDouble(out var unit) ||
                !double.IsFinite(unit) || unit < 0 || unit > 1 || url.ValueKind != JsonValueKind.String || url.GetString() is not { } address) continue;
            var anchor = value.TryGetProperty("anchor", out var location) && location.ValueKind == JsonValueKind.String ? location.GetString() ?? "" : "";
            var title = value.TryGetProperty("title", out var caption) && caption.ValueKind == JsonValueKind.String ? caption.GetString() ?? "" : "";
            _readingPositions[address] = new(address, unit, anchor, title);
        }
    }
    private void SaveReadingPosition()
    {
        _positionTimer.Stop();
        if (_disposed || !_hasBody || _restoringPosition || CurrentArticle is not { } article) return;
        var location = ReadingPositionTools.Capture(ArticleBodyReader);
        var state = new ArticleReadingPosition(article.Url, location.Progress, location.Anchor, article.Title);
        _readingPositions[article.Url] = state;
        ReadingPositionChanged?.Invoke(this, state);
    }
    private MainViewModel? _capturePreviewViewModel;
    private bool _captureGenerating;
    public bool IsCapturePreviewOpen => ReadingCapturePane.Visibility == Visibility.Visible;
    public MainViewModel? CapturePreviewViewModel => _capturePreviewViewModel;

    public Task EnsureLoadedAsync() => EnsureLoadedAsync(default);
    public async Task EnsureLoadedAsync(CancellationToken navigationToken)
    {
        if (_viewModel.Articles.Count == 0) await _viewModel.LoadAsync();
        else await _viewModel.RefreshIfNewDayAsync();
        if (_disposed || navigationToken.IsCancellationRequested) return;
        if (IsArticleOpen && !_hasBody && _articleCancellation is null && CurrentArticle is { } article)
            await OpenArticleAsync(article);
    }

    public async Task OpenArticleAsync(ReadingArticle article, bool forceRefresh = false)
    {
        if (_disposed) return;
        SaveReadingPosition();
        CloseCapturePreview();
        if (!IsArticleOpen) _directoryOffset = ReadingArticlesScroll.VerticalOffset;
        CancelArticle(); CloseWordActions();
        var version = ++_articleVersion;
        var cancellation = _articleCancellation = new CancellationTokenSource();
        CurrentArticle = article; _hasBody = false;
        ReadingDirectory.Visibility = Visibility.Collapsed; ArticleSurface.Visibility = Visibility.Visible;
        ArticleSourceText.Text = article.SourceName;
        ArticleTitleText.Text = article.Title;
        ArticleStatusText.Text = "正在提取纯净正文…";
        ArticleBodyReader.Visibility = Visibility.Collapsed;
        ArticleLoadState.Visibility = Visibility.Visible;
        ArticleLoadMessage.Text = "正在载入全文…";
        RetryArticleButton.Visibility = Visibility.Collapsed;
        _viewModel.Message = "正在获取文章全文…";
        try
        {
            var retained = !forceRefresh && _loadedContent?.Article.Url == article.Url;
            var content = retained ? _loadedContent! : await _contentService.GetAsync(article, forceRefresh, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || version != _articleVersion || CurrentArticle != article) return;
            if (content.Paragraphs.Count == 0) throw new ArticleContentException("来源暂时没有可读取的正文。");
            if (!retained) ArticleBodyReader.SetParagraphs(content.Paragraphs);
            _loadedContent = content;
            _hasBody = true;
            _restoringPosition = true;
            ArticleBodyReader.Visibility = Visibility.Visible; ArticleLoadState.Visibility = Visibility.Collapsed;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_disposed && version == _articleVersion && CurrentArticle == article && _readingPositions.TryGetValue(article.Url, out var position))
                    ReadingPositionTools.Restore(ArticleBodyReader, position.Progress, position.Anchor);
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                { if (version == _articleVersion) _restoringPosition = false; }));
            }));
            ArticleStatusText.Text = content.IsOffline ? "离线缓存" : "";
            _viewModel.Message = content.IsOffline ? "正在阅读已缓存的全文。" : "全文已载入。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        {
            if (_disposed || version != _articleVersion) return;
            ArticleStatusText.Text = "全文暂未载入";
            ArticleLoadMessage.Text = error is ArticleContentException
                ? error.Message + "\n也可通过「来源网页」查看原文。"
                : "暂时无法获取全文。请重试，或通过「来源网页」查看原文。";
            RetryArticleButton.Visibility = Visibility.Visible;
            _viewModel.Message = "全文获取失败，可以重试；当前没有用摘要替代全文。";
        }
        finally
        {
            if (ReferenceEquals(_articleCancellation, cancellation)) _articleCancellation = null;
            cancellation.Dispose();
        }
    }

    public void ReturnToDirectory()
    {
        SaveReadingPosition();
        CloseCapturePreview();
        CancelArticle(); CloseWordActions();
        CurrentArticle = null; _hasBody = false;
        ArticleSurface.Visibility = Visibility.Collapsed; ReadingDirectory.Visibility = Visibility.Visible;
        _viewModel.Message = "已返回每日阅读目录。";
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_disposed && !IsArticleOpen) ReadingArticlesScroll.ScrollToVerticalOffset(_directoryOffset);
        }));
    }

    public void Cancel()
    {
        SaveReadingPosition();
        CloseCapturePreview();
        _viewModel.Cancel(); CancelArticle(); CloseWordActions();
        if (IsArticleOpen && !_hasBody)
        {
            ArticleStatusText.Text = "全文载入已暂停";
            ArticleLoadMessage.Text = "返回此页后会继续载入全文。";
            RetryArticleButton.Visibility = Visibility.Visible;
        }
    }
    private void CancelArticle()
    {
        _restoringPosition = false;
        SmoothScroll.Cancel(ArticleBodyReader);
        ++_articleVersion;
        _articleCancellation?.Cancel(); _articleCancellation = null;
    }

    private void CancelWord()
    {
        ++_wordVersion;
        _wordCancellation?.Cancel(); _wordCancellation = null;
    }
    private void CloseWordActions()
    {
        CancelWord(); _selectedWord = null;
        DetachWordOwner();
        ReadingWordPopup.IsOpen = false; ArticleBodyReader.ClearWordSelection();
    }
    private void OnWordClicked(object? sender, ReadingWordEventArgs e)
    {
        CancelWord();
        _selectedWord = e.Selection;
        ReadingSelectedWord.Text = e.Selection.Word;
        ReadingTranslateButton.IsEnabled = true;
        ResetWordDetails();
        WordTranslationText.Text = ""; WordTranslationText.Visibility = Visibility.Collapsed;
        ReadingWordPopup.PlacementRectangle = e.Bounds.IsEmpty ? new Rect(0, 0, 1, 1) : e.Bounds;
        ReadingWordPopup.IsOpen = true;
        // A popup that captures all outside mouse input interrupts native double-click selection.
        // Observe the owner instead, so the original click/drag still reaches the document.
        if (_wordOwner is null && Window.GetWindow(this) is { } owner)
        {
            _wordOwner = owner;
            owner.PreviewMouseDown += OnWordOutsideClick;
            owner.Deactivated += OnWordOwnerDeactivated;
            owner.LocationChanged += OnWordOwnerDeactivated;
            owner.SizeChanged += OnWordOwnerResized;
        }
    }
    private void OnWordOutsideClick(object sender, MouseButtonEventArgs e)
    {
        // Popup children still route input through their logical owner. They are
        // inside clicks even though the popup uses a separate presentation source.
        for (var node = e.OriginalSource as DependencyObject; node is not null;)
        {
            if (ReferenceEquals(node, ReadingWordPopup.Child)) return;
            node = node is FrameworkContentElement content ? content.Parent
                : node is Visual ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        CloseWordActions();
    }
    private void OnWordOwnerDeactivated(object? sender, EventArgs e) => CloseWordActions();
    private void OnWordOwnerResized(object sender, SizeChangedEventArgs e) => CloseWordActions();
    private void CloseWordOnEscape(object sender, KeyEventArgs e)
    { if (e.Key == Key.Escape && ReadingWordPopup.IsOpen) { CloseWordActions(); e.Handled = true; } }
    private void DetachWordOwner()
    {
        if (_wordOwner is not { } owner) return;
        owner.PreviewMouseDown -= OnWordOutsideClick;
        owner.Deactivated -= OnWordOwnerDeactivated;
        owner.LocationChanged -= OnWordOwnerDeactivated;
        owner.SizeChanged -= OnWordOwnerResized;
        _wordOwner = null;
    }
    private async void TranslateWord_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWord is not { } word || _disposed) return;
        CancelWord();
        var version = _wordVersion;
        var cancellation = _wordCancellation = new CancellationTokenSource();
        ReadingTranslateButton.IsEnabled = false;
        ResetWordDetails();
        WordTranslationText.Visibility = Visibility.Visible; WordTranslationText.Text = "正在翻译…";
        try
        {
            var translated = await _translationService.TranslateAsync(word.Word, cancellation.Token);
            if (!_disposed && ReadingWordPopup.IsOpen && !cancellation.IsCancellationRequested && version == _wordVersion && _selectedWord == word)
                ShowWordTranslation(translated);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or ArgumentException)
        {
            if (!_disposed && ReadingWordPopup.IsOpen && version == _wordVersion)
                WordTranslationText.Text = error is WordTranslationException ? error.Message : "暂时无法翻译，请稍后重试。";
        }
        finally
        {
            if (ReferenceEquals(_wordCancellation, cancellation)) _wordCancellation = null;
            cancellation.Dispose();
            if (!_disposed && version == _wordVersion) ReadingTranslateButton.IsEnabled = true;
        }
    }
    private void ResetWordDetails()
    {
        WordPhoneticText.Text = ""; WordPhoneticText.Visibility = Visibility.Collapsed;
        WordLemmaText.Text = ""; WordLemmaText.Visibility = Visibility.Collapsed;
        WordMeaningsList.ItemsSource = null; WordDetailsScroll.Visibility = Visibility.Collapsed;
        WordDetailsScroll.ScrollToTop();
    }
    private void ShowWordTranslation(WordTranslation translation)
    {
        WordTranslationText.Text = translation.TranslatedText;
        if (!string.IsNullOrWhiteSpace(translation.Phonetic))
        {
            WordPhoneticText.Text = "/" + translation.Phonetic.Trim().Trim('/') + "/";
            WordPhoneticText.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrWhiteSpace(translation.Lemma) && !string.Equals(translation.Word, translation.Lemma, StringComparison.OrdinalIgnoreCase))
        {
            WordLemmaText.Text = "原形 · " + translation.Lemma;
            WordLemmaText.Visibility = Visibility.Visible;
        }
        var hasMeanings = translation.Meanings is { Count: > 0 };
        WordMeaningsList.ItemsSource = translation.Meanings;
        WordDetailsScroll.Visibility = hasMeanings ? Visibility.Visible : Visibility.Collapsed;
        WordTranslationText.Visibility = hasMeanings ? Visibility.Collapsed : Visibility.Visible;
    }
    private void CaptureWord_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWord is not { } word) return;
        CloseWordActions();
        WordCaptureRequested?.Invoke(this, word);
    }
    public void OpenCapturePreview(MainViewModel viewModel, ReadingWordSelection word)
    {
        if (_disposed) return;
        if (!viewModel.PrepareDraft(word.Word, word.Context))
        { _viewModel.Message = "请等待当前收录完成。"; return; }
        CloseWordActions();
        _capturePreviewViewModel = viewModel;
        ReadingCapturePane.DataContext = viewModel;
        var opening = !IsCapturePreviewOpen;
        ReadingCaptureGap.Width = new GridLength(14);
        ReadingCaptureColumn.Width = new GridLength(Math.Clamp(ActualWidth * .32, 310, 370));
        ReadingCapturePane.Visibility = Visibility.Visible;
        if (opening && SystemParameters.ClientAreaAnimation)
        {
            var movement = new TranslateTransform(); ReadingCapturePane.RenderTransform = movement;
            movement.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
    }
    public void CloseCapturePreview()
    {
        if (_captureGenerating) _capturePreviewViewModel?.CancelCommand.Execute(null);
        ReadingCapturePane.Visibility = Visibility.Collapsed;
        ReadingCaptureColumn.Width = new GridLength(0); ReadingCaptureGap.Width = new GridLength(0);
        ReadingCapturePane.DataContext = null;
        _capturePreviewViewModel = null;
    }
    private void CloseReadingCapture_Click(object sender, RoutedEventArgs e) => CloseCapturePreview();
    private async void GenerateCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_capturePreviewViewModel is not { IsBusy: false } capture || _captureGenerating) return;
        _captureGenerating = true;
        try { await capture.GenerateAsync(); }
        finally { _captureGenerating = false; }
    }
    private void CapturePreview_LinkRequested(object? sender, PreviewLinkEventArgs e) => CaptureLinkRequested?.Invoke(sender, e);
    private void ReadingWordPopup_Closed(object? sender, EventArgs e)
    {
        if (ReadingWordPopup.IsOpen) return; // A quick next-word click may reopen during the fade-out.
        CancelWord(); _selectedWord = null; DetachWordOwner(); ArticleBodyReader.ClearWordSelection();
    }
    private void OnWordPopupOpenChanged(object? sender, EventArgs e)
    {
        if (ReadingWordPopup.IsOpen) return;
        CancelWord(); _selectedWord = null; DetachWordOwner(); ArticleBodyReader.ClearWordSelection();
    }
    private async void OpenArticle_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ReadingArticle article }) await OpenArticleAsync(article); }
    private async void ArticleTitle_Click(object sender, MouseButtonEventArgs e)
    { if (sender is FrameworkElement { DataContext: ReadingArticle article }) await OpenArticleAsync(article); }
    private void BackToReadingList_Click(object sender, RoutedEventArgs e) => ReturnToDirectory();
    private async void RetryArticle_Click(object sender, RoutedEventArgs e)
    { if (CurrentArticle is { } article) await OpenArticleAsync(article, true); }
    private void ArticleSource_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentArticle is not { } article || !ArticleWindow.IsAllowed(article.Url)) return;
        try { _openArticle(article.Url); }
        catch (Exception) { _viewModel.Message = "来源网页暂时无法打开，请稍后重试。"; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _positionTimer.Stop();
        _disposed = true; Cancel();
        _popupOpen.RemoveValueChanged(ReadingWordPopup, OnWordPopupOpenChanged);
        ArticleBodyReader.WordClicked -= OnWordClicked;
        ReadingCapturePreview.Dispose();
        if (_ownsContent && _contentService is IDisposable content) content.Dispose();
        if (_ownsTranslation && _translationService is IDisposable translation) translation.Dispose();
    }
}
