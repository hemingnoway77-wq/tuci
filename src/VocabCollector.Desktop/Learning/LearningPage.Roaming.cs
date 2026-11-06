using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VocabCollector.Core.Library;

namespace VocabCollector.Desktop.Learning;

public sealed record RoamingActivityCell(DateOnly Day, int Count, int UniqueWords, bool IsFuture)
{
    private static readonly Brush[] Shades = new[] { "#F5F5F3", "#DBDBD8", "#ADADAA", "#7E7E7B", "#50504D", "#252523" }
        .Select(value => { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!; brush.Freeze(); return (Brush)brush; }).ToArray();
    public Brush Fill => Shades[Count switch { 0 => 0, < 5 => 1, < 10 => 2, < 20 => 3, < 40 => 4, _ => 5 }];
    public double Opacity => IsFuture ? 0.25 : 1;
    public string Tooltip => IsFuture ? $"{Day:yyyy-MM-dd} · 尚未到来" : $"{Day:yyyy-MM-dd}\n漫游 {Count} 次 · {UniqueWords} 个不同单词";
}

public partial class LearningPage
{
    private RoamingStatistics? _statistics;
    private IReadOnlyList<RoamingActivityCell> _activityDays = [];
    private DispatcherTimer? _statisticsTimer;
    private bool _monthly, _previewOpen;
    private string _previewTitle = "当前词条";

    public double WeightLevel => _exposure?.WeightLevel ?? 0;
    public string WeightDescription => _exposure is null ? "等待一次相遇" : WeightLevel switch { < 0.34 => "较低", < 0.67 => "适中", _ => "较高" };
    public double Coverage => (_monthly ? _statistics?.MonthCoverage : _statistics?.WeekCoverage) ?? 0;
    public string CoverageLabel => $"{(_monthly ? _statistics?.MonthReviewed : _statistics?.WeekReviewed) ?? 0} / {_statistics?.TotalWords ?? 0} 词";
    public string CoverageDescription => (_monthly ? "本月" : "本周") + "遇见的不同单词，占当前词库的比例；重复遇见只计一个。";
    public string TrackingHint => _statistics?.HasLegacyHistory == true ? "旧版未记录每日历史，统计从新版开始。复习记录保存在本机。" : "每日记录实际漫游次数，阅读预览不计次数。记录保存在本机。";
    public IReadOnlyList<RoamingActivityCell> ActivityDays => _activityDays;
    public bool IsRoamingPreviewOpen => _previewOpen;
    public NoteReader RoamingReaderControl => RoamingPreviewReader;
    public string RoamingPreviewTitle => _previewTitle;
    public bool CanPreviewGoBack => RoamingPreviewReader?.CanGoBack == true;
    public bool CanPreviewGoForward => RoamingPreviewReader?.CanGoForward == true;

    private void InitializeRoaming()
    {
        RoamingPreviewReader.DirectLinks = true;
        RoamingPreviewReader.NoteChanged += (_, note) =>
        {
            _previewTitle = note.Title;
            Changed(nameof(RoamingPreviewTitle)); Changed(nameof(CanPreviewGoBack)); Changed(nameof(CanPreviewGoForward));
        };
        RoamingPreviewReader.NewTabRequested += (_, note) => RoamingPreviewReader.OpenNote(note);
        RoamingPreviewReader.WebLinkRequested += (_, url) => WebLinkRequested?.Invoke(this, url);
        RoamingPreviewReader.MessageChanged += (_, message) => { if (!string.IsNullOrEmpty(message)) Message = message; };
        _statisticsTimer = new(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _statisticsTimer.Tick += (_, _) =>
        {
            if (RoamingSurface.IsVisible && _statistics?.Today != DateOnly.FromDateTime(DateTime.Now)) UpdateRoamingStatistics();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _statisticsTimer.Start(); UpdateRoamingStatistics(); }
            else { _statisticsTimer.Stop(); RoamingMeaningReveal.Hide(); }
        };
    }

    private void UpdateRoamingStatistics()
    {
        if (_disposed) return;
        var updated = _session?.GetStatistics(_notes);
        if (_statistics is { } previous && updated is { } current && previous.Today == current.Today &&
            previous.WeekReviewed == current.WeekReviewed && previous.MonthReviewed == current.MonthReviewed &&
            previous.TotalWords == current.TotalWords && previous.HasLegacyHistory == current.HasLegacyHistory &&
            previous.TrackingStartedOn == current.TrackingStartedOn && previous.DayCounts.SequenceEqual(current.DayCounts)) return;
        _statistics = updated;
        var days = _statistics?.DayCounts ?? [];
        // UniformGrid fills rows first; transpose chronological weeks into one column per week.
        _activityDays = days.Count == 84
            ? Enumerable.Range(0, 7).SelectMany(day => Enumerable.Range(0, 12).Select(week => days[week * 7 + day]))
                .Select(day => new RoamingActivityCell(day.Day, day.Count, day.UniqueWords, day.IsFuture)).ToArray()
            : [];
        foreach (var name in new[] { nameof(Coverage), nameof(CoverageLabel), nameof(CoverageDescription), nameof(ActivityDays), nameof(TrackingHint) }) Changed(name);
    }

    public void SetStatisticsPeriod(bool monthly)
    {
        _monthly = monthly;
        if (RoamingWeekButton is not null) RoamingWeekButton.IsChecked = !monthly;
        if (RoamingMonthButton is not null) RoamingMonthButton.IsChecked = monthly;
        Changed(nameof(Coverage)); Changed(nameof(CoverageLabel)); Changed(nameof(CoverageDescription));
    }
    private void StatisticsPeriod_Checked(object sender, RoutedEventArgs e)
    { if (sender is RadioButton button) SetStatisticsPeriod((string)button.Tag == "month"); }

    public void OpenRoamingPreview()
    {
        if (_disposed || _catalog is null || _exposure is not { } card) return;
        RoamingMeaningReveal.Hide();
        var opening = !_previewOpen;
        _previewOpen = true;
        SetPreviewLayout();
        DisplayRoamingPreview(card.Note);
        if (opening && SystemParameters.ClientAreaAnimation)
        {
            var movement = new TranslateTransform(); RoamingPreviewPane.RenderTransform = movement;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(180);
            movement.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            RoamingPreviewPane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
        }
        Changed(nameof(IsRoamingPreviewOpen));
    }
    private void DisplayRoamingPreview(LibraryNote note)
    {
        if (_catalog is null) return;
        RoamingPreviewReader.Configure(_catalog, _notes);
        RoamingPreviewReader.OpenNote(note, remember: false);
    }
    public void CloseRoamingPreview()
    {
        _previewOpen = false; RoamingMeaningReveal.Hide();
        SetPreviewLayout(); Changed(nameof(IsRoamingPreviewOpen));
    }
    private void SetPreviewLayout()
    {
        RoamingPreviewPane.Visibility = _previewOpen ? Visibility.Visible : Visibility.Collapsed;
        RoamingStatusColumn.Width = new GridLength(_previewOpen ? 212 : 272);
        RoamingPreviewGap.Width = new GridLength(_previewOpen ? 14 : 0);
        RoamingPreviewColumn.MinWidth = _previewOpen ? 290 : 0;
        RoamingPreviewColumn.Width = _previewOpen ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
        RoamingCardColumn.MinWidth = _previewOpen ? 240 : 0;
        RoamingWordCard.Padding = _previewOpen ? new Thickness(20, 24, 20, 24) : new Thickness(34, 26, 34, 24);
        RoamingStatusPanel.Padding = _previewOpen ? new Thickness(10, 24, 10, 20) : new Thickness(22, 26, 22, 20);
    }
    private void CloseRoamingPreview_Click(object sender, RoutedEventArgs e) => CloseRoamingPreview();
    private void RoamingPreviewBack_Click(object sender, RoutedEventArgs e) => RoamingPreviewReader.GoBack();
    private void RoamingPreviewForward_Click(object sender, RoutedEventArgs e) => RoamingPreviewReader.GoForward();
    private void DisposeRoaming()
    {
        _statisticsTimer?.Stop(); RoamingMeaningReveal.Hide(); RoamingPreviewReader.Dispose();
    }
}
