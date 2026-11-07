using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media.Animation;
namespace VocabCollector.Desktop;

public static class AutoHideScrollbars
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(AutoHideScrollbars), new PropertyMetadata(false, EnableChanged));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(AutoHideScrollbars), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits, ActiveChanged));
    public static readonly DependencyProperty UseFadingProperty = DependencyProperty.RegisterAttached("UseFading", typeof(bool), typeof(AutoHideScrollbars), new PropertyMetadata(false, FadingChanged));
    private static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached("Timer", typeof(DispatcherTimer), typeof(AutoHideScrollbars));
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsActive(DependencyObject target) => (bool)target.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject target, bool value) => target.SetValue(IsActiveProperty, value);
    public static bool GetUseFading(DependencyObject target) => (bool)target.GetValue(UseFadingProperty);
    public static void SetUseFading(DependencyObject target, bool value) => target.SetValue(UseFadingProperty, value);
    private static void EnableChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollViewer viewer) return;
        if ((bool)args.NewValue) { viewer.ScrollChanged += Scrolled; viewer.Unloaded += Unloaded; }
        else { viewer.ScrollChanged -= Scrolled; viewer.Unloaded -= Unloaded; Stop(viewer); }
    }
    private static void Scrolled(object sender, ScrollChangedEventArgs args)
    {
        if (sender is ScrollViewer viewer && ReferenceEquals(args.OriginalSource, viewer) && viewer.IsLoaded && (args.VerticalChange != 0 || args.HorizontalChange != 0)) Reveal(viewer);
    }
    private static void ActiveChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    { if (target is ScrollBar bar && GetUseFading(bar)) Fade(bar); }
    private static void FadingChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollBar bar) return;
        if ((bool)args.NewValue)
        { bar.MouseEnter += BarPointer; bar.MouseLeave += BarPointer; bar.IsMouseCaptureWithinChanged += BarCapture; bar.Unloaded += BarUnloaded; Fade(bar); }
        else
        { bar.MouseEnter -= BarPointer; bar.MouseLeave -= BarPointer; bar.IsMouseCaptureWithinChanged -= BarCapture; bar.Unloaded -= BarUnloaded; bar.BeginAnimation(UIElement.OpacityProperty, null); }
    }
    private static void BarPointer(object sender, MouseEventArgs args) => Fade((ScrollBar)sender);
    private static void BarCapture(object sender, DependencyPropertyChangedEventArgs args) => Fade((ScrollBar)sender);
    private static void BarUnloaded(object sender, RoutedEventArgs args) => ((ScrollBar)sender).BeginAnimation(UIElement.OpacityProperty, null);
    private static void Fade(ScrollBar bar)
    {
        var target = bar.IsMouseCaptureWithin ? .9 : bar.IsMouseOver ? .75 : GetIsActive(bar) ? .48 : 0;
        var current = bar.Opacity;
        bar.BeginAnimation(UIElement.OpacityProperty, null);
        bar.SetCurrentValue(UIElement.OpacityProperty, target);
        if (bar.IsLoaded && SystemParameters.ClientAreaAnimation && Math.Abs(current - target) > .001)
            bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(target == 0 ? 140 : 80))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    private static void Reveal(ScrollViewer viewer)
    {
        var timer = (DispatcherTimer?)viewer.GetValue(TimerProperty);
        if (timer is null)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, viewer.Dispatcher) { Interval = TimeSpan.FromMilliseconds(1000) };
            timer.Tick += (_, _) => { timer.Stop(); viewer.SetValue(IsActiveProperty, false); };
            viewer.SetValue(TimerProperty, timer);
        }
        timer.Stop(); viewer.SetValue(IsActiveProperty, true); timer.Start();
    }
    private static void Unloaded(object sender, RoutedEventArgs args) => Stop((ScrollViewer)sender);
    private static void Stop(ScrollViewer viewer) { ((DispatcherTimer?)viewer.GetValue(TimerProperty))?.Stop(); viewer.ClearValue(TimerProperty); viewer.SetValue(IsActiveProperty, false); }
}
