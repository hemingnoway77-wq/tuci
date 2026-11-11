using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VocabCollector.Core.Library;
using VocabCollector.Core.Reading;
using VocabCollector.Core;
using VocabCollector.Core.Ai;

namespace VocabCollector.Desktop.Learning;

public partial class LearningPage
{
    private PracticeSession? _practiceSession;
    private JsonLinesWordDictionary? _practiceDictionary;
    private readonly IPracticeContextGenerator? _practiceContextGenerator;
    private readonly Func<ApiOptions> _practiceApiOptions;
    private IPracticeContextGenerator? _defaultPracticeContextGenerator;
    private HttpClient? _practiceContextClient;
    private PracticeContextCache? _practiceContextCache;
    private IReadOnlyList<PracticeSense> _practiceContextSenses = [];
    private CancellationTokenSource? _practiceGenerationCancellation;
    private bool _practiceNeedsContext, _practiceGenerating;
    private string _practicePreparationMessage = "";
    private PracticeQuestion? _practiceQuestion;
    private PracticeOutcome? _practiceOutcome;
    private PracticeOutcome? _practiceCommittedOutcome;
    private DispatcherTimer? _practiceTimer;
    private Window? _practiceWindow;
    private bool _practiceInitialized, _practiceEnabled, _practiceLoading, _practiceRevealed, _practiceAdvancing;
    private bool _practiceClockRunning;
    private long _practiceVersion, _practiceClockStarted;
    private int _practiceModeMotionVersion;
    private Guid? _practicePreparingExposure;
    private Guid? _practiceCardExposure;
    private LibraryNote? _practiceCardNote;
    private TimeSpan _practiceRemaining = TimeSpan.FromSeconds(30);
    private string _practiceSubmittedAnswer = "";

    public PracticeQuestion? CurrentPracticeQuestion => _practiceQuestion;
    public bool IsPracticeRevealed => _practiceRevealed;
    public Task RefreshPreparedPracticeAsync(string word)
    {
        if (_disposed || !_practiceEnabled || !_practiceNeedsContext || _practiceGenerating || _practiceLoading ||
            !string.Equals(_exposure?.Note.Title, word, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        return PreparePracticeAsync();
    }
    public TimeSpan PracticeTimeRemaining => _practiceClockRunning
        ? TimeSpan.FromTicks(Math.Max(0, (_practiceRemaining - _clock.GetElapsedTime(_practiceClockStarted)).Ticks))
        : _practiceRemaining;

    private void InitializePractice()
    {
        _practiceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _practiceTimer.Tick += PracticeTimer_Tick;
        Loaded += Practice_Loaded;
        Unloaded += Practice_Unloaded;
        RoamingSurface.IsVisibleChanged += PracticeVisibility_Changed;
        _practiceInitialized = true;
        AlignPracticeModeUnderline(false);
        RenderPractice();
    }

    private void Practice_Loaded(object sender, RoutedEventArgs args)
    {
        var owner = Window.GetWindow(this);
        if (!ReferenceEquals(owner, _practiceWindow))
        {
            DetachPracticeWindow(); _practiceWindow = owner;
            if (owner is not null) { owner.Activated += PracticeWindow_Activated; owner.Deactivated += PracticeWindow_Deactivated; }
        }
        AlignPracticeModeUnderline(false);
        ResumePracticeClock();
    }
    private void Practice_Unloaded(object sender, RoutedEventArgs args)
    {
        PausePracticeClock();
        StopPracticeModeMotion();
        DetachPracticeWindow();
    }
    private void DetachPracticeWindow()
    {
        if (_practiceWindow is not { } owner) return;
        owner.Activated -= PracticeWindow_Activated; owner.Deactivated -= PracticeWindow_Deactivated; _practiceWindow = null;
    }
    private void PracticeWindow_Activated(object? sender, EventArgs args) => ResumePracticeClock();
    private void PracticeWindow_Deactivated(object? sender, EventArgs args) => PausePracticeClock();
    private void PracticeVisibility_Changed(object sender, DependencyPropertyChangedEventArgs args)
    {
        AlignPracticeModeUnderline(false);
        if (RoamingSurface.IsVisible) ResumePracticeClock();
        else
        {
            PausePracticeClock();
            StopPracticeModeMotion();
            _practiceGenerationCancellation?.Cancel();
        }
    }

    private async void PracticeMode_Changed(object sender, RoutedEventArgs args)
    {
        if (!_practiceInitialized || _disposed) return;
        var practice = ReferenceEquals(sender, RoamingPracticeModeButton);
        if (_practiceEnabled == practice) return;
        var wasShowingPractice = PracticePanel.Visibility == Visibility.Visible;
        _practiceEnabled = practice;
        AlignPracticeModeUnderline(true);
        PausePracticeClock();
        if (!_practiceEnabled) _practiceGenerationCancellation?.Cancel();
        var preparation = Task.CompletedTask;
        if (_practiceEnabled)
        {
            CloseRoamingPreview();
            if (_practiceQuestion?.ExposureId != _exposure?.Id) preparation = PreparePracticeAsync();
        }
        RenderPractice();
        if (wasShowingPractice != (PracticePanel.Visibility == Visibility.Visible)) PlayPracticeModeMotion();
        else StopPracticeModeMotion();
        ResumePracticeClock();
        await preparation;
    }

    private void PracticeMode_KeyDown(object sender, KeyEventArgs args)
    {
        var next = args.Key switch
        {
            Key.Home => RoamingBrowseModeButton,
            Key.End => RoamingPracticeModeButton,
            Key.Left or Key.Right or Key.Up or Key.Down => ReferenceEquals(sender, RoamingBrowseModeButton)
                ? RoamingPracticeModeButton : RoamingBrowseModeButton,
            _ => null
        };
        if (next is null) return;
        next.Focus();
        next.IsChecked = true;
        args.Handled = true;
    }

    private void RoamingModeSelector_SizeChanged(object sender, SizeChangedEventArgs args) => AlignPracticeModeUnderline(false);

    private void AlignPracticeModeUnderline(bool animate)
    {
        if (!_practiceInitialized || _disposed) return;
        var selected = _practiceEnabled ? RoamingPracticeModeButton : RoamingBrowseModeButton;
        var center = selected.ActualWidth > 0
            ? selected.TranslatePoint(new Point(selected.ActualWidth / 2, 0), RoamingModeSelector).X
            : RoamingModeSelector.ColumnDefinitions[0].Width.Value * (_practiceEnabled ? 1.5 : .5);
        RoamingModeUnderline.MoveTo(center - 10.5, 21, animate);
    }

    private void PlayPracticeModeMotion()
    {
        StopPracticeModeMotion();
        if (!RoamingSurface.IsVisible || !SystemParameters.ClientAreaAnimation) return;
        var duration = TimeSpan.FromMilliseconds(170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var motion = new DoubleAnimation(8, 0, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        var fade = new DoubleAnimation(.72, 1, duration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        var version = _practiceModeMotionVersion;
        motion.Completed += (_, _) => { if (version == _practiceModeMotionVersion) StopPracticeModeMotion(); };
        RoamingCardBody.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        RoamingModeSlide.BeginAnimation(TranslateTransform.YProperty, motion, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopPracticeModeMotion()
    {
        ++_practiceModeMotionVersion;
        RoamingCardBody.BeginAnimation(OpacityProperty, null);
        RoamingModeSlide.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void PracticeExposureChanged()
    {
        if (!_practiceInitialized || _disposed) return;
        var note = _exposure?.Note;
        // Background sync can refresh the same exposure while no question has
        // been prepared yet. Preserve that request/draft, but revalidate the
        // bank if the same note's text changes during a refresh.
        if (_practiceCardExposure != _exposure?.Id ||
            _practiceCardNote?.Id != note?.Id || _practiceCardNote?.Title != note?.Title ||
            _practiceCardNote?.Markdown != note?.Markdown)
        {
            _practiceCardExposure = _exposure?.Id; _practiceCardNote = note;
            PausePracticeClock();
            ++_practiceVersion;
            _practiceGenerationCancellation?.Cancel();
            _practiceGenerating = false; _practiceNeedsContext = false; _practicePreparationMessage = "";
            _practiceContextSenses = [];
            _practiceLoading = false;
            _practiceQuestion = null; _practiceOutcome = null; _practiceCommittedOutcome = null; _practiceRevealed = false;
            _practicePreparingExposure = null; _practiceSubmittedAnswer = "";
            PracticeAnswerBox.Text = "";
            if (_practiceEnabled) { CloseRoamingPreview(); _ = PreparePracticeAsync(); }
        }
        RenderPractice();
    }

    private async Task PreparePracticeAsync()
    {
        if (_disposed || !_practiceEnabled || _catalog is null || _exposure is not { } card) { RenderPractice(); return; }
        if (_practicePreparingExposure == card.Id) return;
        _practicePreparingExposure = card.Id;
        var version = ++_practiceVersion;
        _practiceLoading = true; RenderPractice();
        try
        {
            _practiceContextSenses = PracticeQuestionBank.Build(card.Note);
            _practiceNeedsContext = _practiceContextSenses.Count > 0;
            _practiceSession ??= new PracticeSession(Path.Combine(_catalog.LocalStateDirectory, "practice.json"));
            _practiceContextCache ??= new PracticeContextCache(Path.Combine(_catalog.LocalStateDirectory, "practice-contexts.json"));
            string? dictionaryLemma = null;
            var senses = _practiceContextSenses;
            if (senses.Count > 0 && senses.All(sense => string.IsNullOrWhiteSpace(sense.Lemma)))
            {
                _practiceDictionary ??= new JsonLinesWordDictionary(Path.Combine(AppContext.BaseDirectory, "Assets", "OfflineDictionary"));
                try { dictionaryLemma = (await _practiceDictionary.LookupAsync(card.Note.Title, _stop.Token))?.Lemma; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { /* The saved word remains an exact answer when optional morphology is unavailable. */ }
            }
            senses = PracticeQuestionBank.Build(card.Note, dictionaryLemma);
            IReadOnlyList<PracticeContext> contexts = senses.Count == 0 ? [] : await _practiceContextCache.ReadAsync(card.Note.Title, senses, _stop.Token);
            var question = await _practiceSession.PrepareAsync(card.Note, card.Id, dictionaryLemma, _stop.Token,
                contexts.Count == 0 ? null : contexts);
            if (_disposed || version != _practiceVersion || _exposure?.Id != card.Id) return;
            _practiceQuestion = question; _practiceOutcome = null; _practiceCommittedOutcome = null; _practiceRevealed = false;
            _practiceSubmittedAnswer = ""; PracticeAnswerBox.Text = "";
            _practiceRemaining = TimeSpan.FromSeconds(30);
            _practiceContextSenses = senses;
            _practiceNeedsContext = question is null && senses.Count > 0;
            if (_practiceNeedsContext && _practicePreparationMessage.Length == 0)
                _practicePreparationMessage = "为各个意思准备对应的例句。会使用当前 AI 接口额度，准备后保存在本机。";
            if (question is not null && _practiceSession.GetCompletion(card.Id) is { } completion)
            {
                _practiceOutcome = _practiceCommittedOutcome = completion.Outcome;
                _practiceSubmittedAnswer = completion.Answer; PracticeAnswerBox.Text = completion.Answer;
                _practiceRevealed = true;
            }
            if (question is null) Message = senses.Count == 0 ? "这篇笔记暂没有可用释义，继续浏览复习。" : "需要先准备对应义项的语境题。";
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (_disposed || _stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (!_disposed && version == _practiceVersion)
            {
                _practicePreparationMessage = "练习记录暂时无法读取，原记录已保留；可关闭作答后继续浏览。";
                Message = _practicePreparationMessage;
            }
        }
        finally
        {
            if (!_disposed && version == _practiceVersion)
            {
                _practiceLoading = false; _practicePreparingExposure = null; RenderPractice(); ResumePracticeClock();
                SchedulePracticePosition();
                if (_practiceEnabled && !_practiceRevealed && _practiceQuestion is not null && RoamingSurface.IsVisible && _practiceWindow?.IsActive == true)
                    PracticeAnswerBox.Focus();
            }
        }
    }

    private void RenderPractice()
    {
        if (!_practiceInitialized || _disposed) return;
        var active = _practiceEnabled && (_practiceQuestion is not null || _practiceLoading || _practiceNeedsContext || _practiceGenerating);
        NormalRoamingBody.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        RoamingBrowseActions.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        PracticePanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PracticeActions.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PracticeInputActions.Visibility = !_practiceRevealed && _practiceQuestion is not null ? Visibility.Visible : Visibility.Collapsed;
        PracticePreparationActions.Visibility = _practiceNeedsContext ? Visibility.Visible : Visibility.Collapsed;
        PracticePrepareButton.IsEnabled = !_practiceGenerating && !_practiceLoading && !_practiceAdvancing;
        PracticeUnpreparedNextButton.IsEnabled = !_practiceGenerating && !_practiceLoading && !_practiceAdvancing;
        PracticePrepareButton.Content = _practiceGenerating ? "正在准备…" : "准备语境题";
        PracticePreparationText.Visibility = _practiceNeedsContext ? Visibility.Visible : Visibility.Collapsed;
        PracticePreparationText.Text = _practicePreparationMessage;
        var meaning = _practiceQuestion?.Kind == PracticeKind.Meaning;
        PracticeNextButton.Visibility = _practiceRevealed && !meaning ? Visibility.Visible : Visibility.Collapsed;
        PracticeSelfAssessmentActions.Visibility = _practiceRevealed && meaning ? Visibility.Visible : Visibility.Collapsed;
        PracticeResultPanel.Visibility = _practiceRevealed ? Visibility.Visible : Visibility.Collapsed;
        PracticeAnswerBox.IsReadOnly = _practiceRevealed || _practiceLoading || _practiceAdvancing;
        PracticeAnswerBox.IsEnabled = !_practiceLoading;
        PracticeAnswerBox.Tag = meaning ? "写下你理解的意思…" : "填写英文单词…";
        PracticeAnswerBox.MaxLength = meaning ? 2000 : 64;
        PracticeSubmitButton.IsEnabled = !_practiceLoading && !_practiceAdvancing && !_practiceRevealed &&
            _practiceQuestion is not null && !string.IsNullOrWhiteSpace(PracticeAnswerBox.Text);
        PracticeSkipButton.IsEnabled = !_practiceLoading && !_practiceAdvancing && _practiceQuestion is not null;
        PracticeNextButton.IsEnabled = PracticeYesButton.IsEnabled = PracticeNoButton.IsEnabled = !_practiceAdvancing && !_advancing;
        if (_practiceCommittedOutcome is { } saved)
        {
            PracticeYesButton.IsEnabled &= saved == PracticeOutcome.SelfConfirmed;
            PracticeNoButton.IsEnabled &= saved == PracticeOutcome.SelfRejected;
        }
        RoamingModeSelector.IsEnabled = !_practiceAdvancing && !_advancing;
        if (_practiceQuestion is not { } question)
        {
            PracticeKindText.Text = _practiceLoading || _practiceGenerating ? "正在准备…" : _practiceNeedsContext ? "语境练习" : "";
            PracticePromptText.Text = PracticeMeaningHint.Text = PracticeLetterHint.Text = PracticeContextText.Text = "";
            PracticeContextText.Visibility = Visibility.Collapsed;
            PracticeAnswerBox.Visibility = Visibility.Collapsed;
            PracticeTimerText.Text = "";
            return;
        }
        PracticeAnswerBox.Visibility = Visibility.Visible;
        PracticeKindText.Text = $"{(meaning ? "写中文" : "拼单词")} · 义项 {question.SenseNumber}/{question.SenseCount}";
        PracticePromptText.Text = question.Prompt;
        PracticeContextText.Text = question.Context;
        PracticeContextText.Visibility = !string.IsNullOrWhiteSpace(question.Context) && !question.Prompt.Contains(question.Context, StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;
        PracticeMeaningHint.Text = meaning ? "" : question.ContextTranslation;
        PracticeMeaningHint.Visibility = meaning || string.IsNullOrWhiteSpace(question.ContextTranslation) ? Visibility.Collapsed : Visibility.Visible;
        PracticeLetterHint.Text = meaning ? "" : $"{question.Word[0]} 开头 · {question.Word.Length} 个字符";
        PracticeLetterHint.Visibility = meaning ? Visibility.Collapsed : Visibility.Visible;
        PracticeCorrectWord.Text = question.Word;
        PracticeReferenceMeaning.Text = question.Meaning;
        PracticeTranslationText.Text = meaning ? question.ContextTranslation : "";
        PracticeTranslationText.Visibility = meaning && !string.IsNullOrWhiteSpace(question.ContextTranslation) ? Visibility.Visible : Visibility.Collapsed;
        PracticeFormText.Text = question.FormNote;
        if (_practiceRevealed)
        {
            PracticeResultText.Text = meaning ? "对照释义" : _practiceOutcome switch
            {
                PracticeOutcome.Correct => "拼写正确",
                PracticeOutcome.LemmaCorrect => "原形正确 · 保留复习权重",
                PracticeOutcome.TimedOut => "时间到",
                PracticeOutcome.Skipped => "再复习一次",
                _ => "再复习一次"
            };
            if (_practiceOutcome == PracticeOutcome.LemmaCorrect && string.IsNullOrWhiteSpace(PracticeFormText.Text))
                PracticeFormText.Text = $"本次收藏的词形是 {question.Word}，原形是 {question.Lemma}。";
        }
        UpdatePracticeTimerText();
    }

    private async void PracticePrepare_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || !_practiceEnabled || !_practiceNeedsContext || _practiceLoading || _practiceGenerating || _catalog is null ||
            _practiceContextCache is null || _practiceContextSenses.Count == 0 || _exposure is not { } card) return;
        var version = _practiceVersion;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _practiceGenerationCancellation = cancellation; _practiceGenerating = true; RenderPractice();
        try
        {
            // Recheck local storage before spending an API request; another
            // preparation may have filled it, or storage may need attention.
            var cached = await _practiceContextCache.ReadAsync(card.Note.Title, _practiceContextSenses, cancellation.Token);
            if (_disposed || !_practiceEnabled || version != _practiceVersion || _exposure?.Id != card.Id || cancellation.IsCancellationRequested) return;
            if (cached.Count > 0)
            {
                _practicePreparationMessage = ""; _practicePreparingExposure = null;
                await PreparePracticeAsync(); return;
            }
            var options = _practiceApiOptions();
            SettingsValidation.ValidateApi(options);
            var generator = _practiceContextGenerator;
            if (generator is null)
            {
                _practiceContextClient ??= new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
                generator = _defaultPracticeContextGenerator ??= new OpenAiPracticeContextGenerator(_practiceContextClient);
            }
            await new PracticeContextPreparation(Path.Combine(_catalog.LocalStateDirectory, "practice-contexts.json"), generator)
                .PrepareAsync(card.Note.Title, _practiceContextSenses, options, cancellation.Token);
            if (_disposed || !_practiceEnabled || version != _practiceVersion || _exposure?.Id != card.Id || cancellation.IsCancellationRequested) return;
            _practicePreparationMessage = ""; _practicePreparingExposure = null;
            await PreparePracticeAsync();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (_disposed || _stop.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or AnalysisException or HttpRequestException or InvalidOperationException)
        {
            if (!_disposed && version == _practiceVersion)
                _practicePreparationMessage = error is ArgumentException ? "请先在设置中填写有效的 AI 地址、API Key 和模型，再准备语境题。"
                    : "语境题暂未准备完成。请检查网络或 AI 配置后重试；不会用纯释义代替语境。";
        }
        finally
        {
            if (ReferenceEquals(_practiceGenerationCancellation, cancellation))
            {
                _practiceGenerationCancellation = null; _practiceGenerating = false;
                if (!_disposed) { RenderPractice(); ResumePracticeClock(); }
            }
            cancellation.Dispose();
        }
    }
    private async void PracticeUnpreparedNext_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || !_practiceEnabled || !_practiceNeedsContext || _practiceGenerating || _practiceAdvancing || _advancing ||
            _session is null || _exposure is not { } card) return;
        _practiceAdvancing = true; RenderPractice();
        try
        {
            var next = await _session.NextPracticeAsync(_notes, card.Id, null, _stop.Token);
            if (_disposed) return;
            _exposure = next; UpdateCard(); UpdateRoamingStatistics();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { if (!_disposed) Message = "暂时无法保存进度，请重试。"; }
        finally { _practiceAdvancing = false; if (!_disposed) RenderPractice(); }
    }
    private void PracticeAnswer_Changed(object sender, TextChangedEventArgs args)
    { if (_practiceInitialized && !_disposed) PracticeSubmitButton.IsEnabled = !_practiceLoading && !_practiceAdvancing && !_practiceRevealed && _practiceQuestion is not null && !string.IsNullOrWhiteSpace(PracticeAnswerBox.Text); }
    private void PracticeAnswer_KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && !args.IsRepeat && !_practiceRevealed)
        { args.Handled = true; if (PracticeSubmitButton.IsEnabled) RevealPractice(false); }
    }
    private void PracticeSubmit_Click(object sender, RoutedEventArgs args) => RevealPractice(false);
    private void PracticeSkip_Click(object sender, RoutedEventArgs args) => RevealPractice(false, skipped: true);
    private void RevealPractice(bool timedOut, bool skipped = false)
    {
        if (_disposed || !_practiceEnabled || _practiceLoading || _practiceAdvancing || _practiceRevealed || _practiceQuestion is not { } question || _exposure?.Id != question.ExposureId) return;
        if (!timedOut && !skipped && string.IsNullOrWhiteSpace(PracticeAnswerBox.Text)) return;
        timedOut |= PracticeTimeRemaining <= TimeSpan.Zero;
        PausePracticeClock(); _practiceSubmittedAnswer = PracticeAnswerBox.Text.Trim();
        _practiceOutcome = question.Kind == PracticeKind.Meaning ? null : skipped ? PracticeOutcome.Skipped : PracticeQuestionBank.Evaluate(question, _practiceSubmittedAnswer, timedOut);
        _practiceRevealed = true;
        RenderPractice();
        SchedulePracticePosition();
    }
    private async void PracticeNext_Click(object sender, RoutedEventArgs args)
    { if (_practiceQuestion?.Kind == PracticeKind.Spelling && _practiceOutcome is { } outcome) await FinishPracticeAsync(outcome); }
    private async void PracticeYes_Click(object sender, RoutedEventArgs args)
    { if (_practiceQuestion?.Kind == PracticeKind.Meaning) await FinishPracticeAsync(PracticeOutcome.SelfConfirmed); }
    private async void PracticeNo_Click(object sender, RoutedEventArgs args)
    { if (_practiceQuestion?.Kind == PracticeKind.Meaning) await FinishPracticeAsync(PracticeOutcome.SelfRejected); }

    private async Task FinishPracticeAsync(PracticeOutcome outcome)
    {
        if (_disposed || !_practiceEnabled || !_practiceRevealed || _practiceAdvancing || _advancing || _session is null || _practiceSession is null ||
            _practiceQuestion is not { } question || _exposure?.Id != question.ExposureId) return;
        _practiceAdvancing = true; PausePracticeClock(); RenderPractice();
        try
        {
            outcome = _practiceCommittedOutcome ?? outcome;
            await _practiceSession.CompleteAsync(question, outcome, _practiceSubmittedAnswer, _stop.Token);
            _practiceCommittedOutcome = outcome;
            if (_disposed || _exposure?.Id != question.ExposureId) return;
            bool? remembered = outcome switch
            {
                PracticeOutcome.Correct or PracticeOutcome.SelfConfirmed => true,
                PracticeOutcome.LemmaCorrect => null,
                _ => false
            };
            var next = await _session.NextPracticeAsync(_notes, question.ExposureId, remembered, _stop.Token);
            if (_disposed) return;
            _exposure = next;
            UpdateCard(); UpdateRoamingStatistics();
            Message = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        { if (!_disposed) Message = "练习记录暂未保存，答案已保留，请重试。"; }
        finally { _practiceAdvancing = false; if (!_disposed) RenderPractice(); }
    }

    private void ResumePracticeClock()
    {
        if (_disposed || _practiceClockRunning || !_practiceEnabled || _practiceLoading || _practiceGenerating || _practiceRevealed || _practiceAdvancing ||
            _practiceQuestion is null || !RoamingSurface.IsVisible || _practiceWindow?.IsActive != true) return;
        if (_practiceRemaining <= TimeSpan.Zero) { RevealPractice(true); return; }
        _practiceClockStarted = _clock.GetTimestamp(); _practiceClockRunning = true;
        _practiceTimer?.Start(); UpdatePracticeTimerText();
    }
    private void PausePracticeClock()
    {
        if (_practiceClockRunning) _practiceRemaining = PracticeTimeRemaining;
        _practiceClockRunning = false; _practiceTimer?.Stop();
        if (_practiceInitialized && !_disposed) UpdatePracticeTimerText();
    }
    private void PracticeTimer_Tick(object? sender, EventArgs args)
    {
        if (PracticeTimeRemaining <= TimeSpan.Zero) RevealPractice(true);
        else UpdatePracticeTimerText();
    }
    private void UpdatePracticeTimerText()
    {
        PracticeTimerText.Text = _practiceRevealed ? "" : $"{Math.Ceiling(PracticeTimeRemaining.TotalSeconds):0} 秒";
    }
    private void SchedulePracticePosition()
    {
        var exposureId = _practiceQuestion?.ExposureId;
        var revealed = _practiceRevealed;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_disposed || !_practiceEnabled || !RoamingSurface.IsVisible || exposureId != _practiceQuestion?.ExposureId || revealed != _practiceRevealed) return;
            if (revealed) PracticeResultPanel.BringIntoView();
            else RoamingCardScroll.ScrollToTop();
        }));
    }
    private void DisposePractice()
    {
        ++_practiceVersion; _practiceTimer?.Stop(); DetachPracticeWindow();
        StopPracticeModeMotion(); RoamingModeUnderline.Dispose();
        _practiceGenerationCancellation?.Cancel(); _practiceContextClient?.Dispose();
        _practiceDictionary?.Dispose();
        if (_practiceTimer is not null) _practiceTimer.Tick -= PracticeTimer_Tick;
        Loaded -= Practice_Loaded; Unloaded -= Practice_Unloaded;
        RoamingSurface.IsVisibleChanged -= PracticeVisibility_Changed;
    }
}
