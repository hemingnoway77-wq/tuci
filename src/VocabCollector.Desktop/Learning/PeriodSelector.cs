using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VocabCollector.Desktop.Learning;

public sealed class PeriodSelector : UserControl, IDisposable
{
    public static readonly DependencyProperty IsMonthlyProperty = DependencyProperty.Register(nameof(IsMonthly), typeof(bool), typeof(PeriodSelector),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectionChanged));
    public static readonly DependencyProperty AnimateTransitionsProperty = DependencyProperty.Register(nameof(AnimateTransitions), typeof(bool), typeof(PeriodSelector),
        new PropertyMetadata(true, OnAnimationPreferenceChanged));
    private static readonly DependencyProperty LeftEdgeProperty = DependencyProperty.Register("LeftEdge", typeof(double), typeof(PeriodSelector), new PropertyMetadata(0d, OnEdgeChanged));
    private static readonly DependencyProperty RightEdgeProperty = DependencyProperty.Register("RightEdge", typeof(double), typeof(PeriodSelector), new PropertyMetadata(21d, OnEdgeChanged));

    public bool IsMonthly { get => (bool)GetValue(IsMonthlyProperty); set => SetValue(IsMonthlyProperty, value); }
    /// <summary>Allows a host to disable motion; the system's reduced-motion preference always takes priority.</summary>
    public bool AnimateTransitions { get => (bool)GetValue(AnimateTransitionsProperty); set => SetValue(AnimateTransitionsProperty, value); }
    public RadioButton WeekButton { get; } = new() { Name = "RoamingWeekButton", Content = new TextBlock { Text = "本周" } };
    public RadioButton MonthButton { get; } = new() { Name = "RoamingMonthButton", Content = new TextBlock { Text = "本月" } };
    public event EventHandler? SelectionChanged;

    private readonly Border _underline = new() { Name = "PeriodUnderline", Width = 21, Height = 1, Opacity = .55, IsHitTestVisible = false };
    private readonly Grid _layout = new();
    private DispatcherOperation? _alignment;
    private bool _synchronizing, _disposed;
    private int _animationVersion;

    public PeriodSelector()
    {
        Name = "PeriodSelector"; Width = 112; Height = 40; Focusable = false;
        NameScope.SetNameScope(this, new NameScope());
        _layout.ColumnDefinitions.Add(new ColumnDefinition());
        _layout.ColumnDefinitions.Add(new ColumnDefinition());
        var group = "PeriodSelector_" + Guid.NewGuid().ToString("N");
        var buttonStyle = CreateButtonStyle();
        foreach (var button in new[] { WeekButton, MonthButton })
        {
            RegisterName(button.Name, button);
            button.GroupName = group;
            button.Style = buttonStyle;
            AutomationProperties.SetName(button, ReferenceEquals(button, WeekButton) ? "本周" : "本月");
            button.Checked += OnButtonChecked;
            button.PreviewKeyDown += OnButtonKeyDown;
            _layout.Children.Add(button);
        }
        Grid.SetColumn(MonthButton, 1);
        KeyboardNavigation.SetTabNavigation(_layout, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(_layout, KeyboardNavigationMode.Contained);
        _underline.SetResourceReference(Border.BackgroundProperty, "Accent");
        RegisterName(_underline.Name, _underline);
        var overlay = new Canvas { IsHitTestVisible = false };
        overlay.Children.Add(_underline); Grid.SetColumnSpan(overlay, 2); _layout.Children.Add(overlay);
        Content = _layout;
        SynchronizeButtons();
        AlignUnderline(false);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        IsVisibleChanged += OnVisibilityChanged;
    }

    private static Style CreateButtonStyle()
    {
        var style = new Style(typeof(RadioButton));
        style.Setters.Add(new Setter(FontFamilyProperty, new DynamicResourceExtension("UiFont")));
        style.Setters.Add(new Setter(FontSizeProperty, 13d));
        style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("InkSecondary")));
        style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FocusVisualStyleProperty, null));
        var surface = new FrameworkElementFactory(typeof(Border), "FocusBorder");
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        surface.SetValue(Border.PaddingProperty, new Thickness(0, 3, 0, 0));
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        surface.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        surface.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentProperty));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        surface.AppendChild(content);
        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = surface };
        style.Setters.Add(new Setter(TemplateProperty, template));
        var selected = new Trigger { Property = RadioButton.IsCheckedProperty, Value = true };
        selected.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("Ink"))); style.Triggers.Add(selected);
        var focused = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(FontWeightProperty, FontWeights.Medium)); style.Triggers.Add(focused);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .4)); style.Triggers.Add(disabled);
        return style;
    }

    private static void OnSelectionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var selector = (PeriodSelector)sender;
        if (selector._disposed) return;
        selector.SynchronizeButtons();
        selector.AlignUnderline(true);
        selector.SelectionChanged?.Invoke(selector, EventArgs.Empty);
    }
    private static void OnAnimationPreferenceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var selector = (PeriodSelector)sender;
        if (!selector._disposed && !(bool)args.NewValue) selector.AlignUnderline(false);
    }
    private static void OnEdgeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var selector = (PeriodSelector)sender;
        var left = (double)selector.GetValue(LeftEdgeProperty);
        var right = (double)selector.GetValue(RightEdgeProperty);
        Canvas.SetLeft(selector._underline, left);
        selector._underline.Width = Math.Max(0, right - left);
    }
    private void OnButtonChecked(object sender, RoutedEventArgs args)
    {
        if (!_synchronizing && !_disposed) SetCurrentValue(IsMonthlyProperty, ReferenceEquals(sender, MonthButton));
    }
    private void SynchronizeButtons()
    {
        _synchronizing = true;
        try
        {
            WeekButton.SetCurrentValue(RadioButton.IsCheckedProperty, !IsMonthly);
            MonthButton.SetCurrentValue(RadioButton.IsCheckedProperty, IsMonthly);
        }
        finally { _synchronizing = false; }
    }
    private void OnButtonKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
        var monthly = args.Key switch { Key.Home => false, Key.End => true, _ => !ReferenceEquals(sender, MonthButton) };
        (monthly ? MonthButton : WeekButton).Focus();
        SetCurrentValue(IsMonthlyProperty, monthly);
        args.Handled = true;
    }

    private (double Left, double Right, double Top) SelectedGeometry()
    {
        var label = (TextBlock)(IsMonthly ? MonthButton : WeekButton).Content;
        if (label.ActualWidth > 0 && label.IsArrangeValid)
        {
            var point = label.TranslatePoint(new Point(label.ActualWidth / 2, label.ActualHeight), this);
            return (point.X - 10.5, point.X + 10.5, point.Y + 8);
        }
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var center = width * (IsMonthly ? .75 : .25);
        return (center - 10.5, center + 10.5, 29);
    }
    private void AlignUnderline(bool animate)
    {
        CancelAlignment();
        var fromLeft = (double)GetValue(LeftEdgeProperty);
        var fromRight = (double)GetValue(RightEdgeProperty);
        StopClocks();
        var target = SelectedGeometry();
        Canvas.SetTop(_underline, target.Top);
        if (!animate || !IsLoaded || !IsVisible || !AnimateTransitions || !SystemParameters.ClientAreaAnimation ||
            (Math.Abs(target.Left - fromLeft) < .1 && Math.Abs(target.Right - fromRight) < .1))
        {
            SetValue(LeftEdgeProperty, target.Left); SetValue(RightEdgeProperty, target.Right);
            return;
        }

        // Read presentation values before removing either clock, so reversals never
        // jump back to the previous label or to an old animation's base position.
        SetValue(LeftEdgeProperty, fromLeft); SetValue(RightEdgeProperty, fromRight);
        var toRight = target.Left + target.Right > fromLeft + fromRight;
        var left = EdgeAnimation(fromLeft, target.Left, leading: !toRight);
        var right = EdgeAnimation(fromRight, target.Right, leading: toRight);
        var version = _animationVersion;
        (toRight ? left : right).Completed += (_, _) =>
        {
            if (!_disposed && version == _animationVersion) AlignUnderline(false);
        };
        BeginAnimation(LeftEdgeProperty, left, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(RightEdgeProperty, right, HandoffBehavior.SnapshotAndReplace);
    }
    private static DoubleAnimationUsingKeyFrames EdgeAnimation(double from, double target, bool leading)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(250), FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (!leading) animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(leading ? 150 : 250)),
            new CubicEase { EasingMode = leading ? EasingMode.EaseOut : EasingMode.EaseInOut }));
        if (leading) animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        return animation;
    }
    private void StopClocks()
    {
        _animationVersion++;
        BeginAnimation(LeftEdgeProperty, null); BeginAnimation(RightEdgeProperty, null);
    }
    private void CancelAlignment() { _alignment?.Abort(); _alignment = null; }
    private void QueueAlignment()
    {
        CancelAlignment();
        _alignment = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _alignment = null;
            if (!_disposed && IsLoaded && IsVisible) AlignUnderline(false);
        }));
    }
    private void OnLoaded(object sender, RoutedEventArgs args) { AlignUnderline(false); QueueAlignment(); }
    private void OnUnloaded(object sender, RoutedEventArgs args) => AlignUnderline(false);
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) { AlignUnderline(false); if (IsLoaded && IsVisible) QueueAlignment(); }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        AlignUnderline(false);
        if (IsVisible && IsLoaded) QueueAlignment();
    }

    public void Dispose()
    {
        if (_disposed) return;
        AlignUnderline(false);
        _disposed = true;
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; SizeChanged -= OnSizeChanged; IsVisibleChanged -= OnVisibilityChanged;
        foreach (var button in new[] { WeekButton, MonthButton })
        { button.Checked -= OnButtonChecked; button.PreviewKeyDown -= OnButtonKeyDown; }
        SelectionChanged = null;
    }
}
