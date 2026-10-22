using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
namespace VocabCollector.Desktop;

public static class AutoHideScrollbars
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(AutoHideScrollbars), new PropertyMetadata(false, EnableChanged));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(AutoHideScrollbars), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    private static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached("Timer", typeof(DispatcherTimer), typeof(AutoHideScrollbars));
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsActive(DependencyObject target) => (bool)target.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject target, bool value) => target.SetValue(IsActiveProperty, value);
    private static void EnableChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollViewer viewer) return;
        if ((bool)args.NewValue) { viewer.ScrollChanged += Scrolled; viewer.PreviewMouseWheel += Wheel; viewer.Unloaded += Unloaded; }
        else { viewer.ScrollChanged -= Scrolled; viewer.PreviewMouseWheel -= Wheel; viewer.Unloaded -= Unloaded; Stop(viewer); }
    }
    private static void Scrolled(object sender, ScrollChangedEventArgs args)
    {
        if (sender is ScrollViewer viewer && viewer.IsLoaded && (args.VerticalChange != 0 || args.HorizontalChange != 0)) Reveal(viewer);
    }
    private static void Wheel(object sender, MouseWheelEventArgs args) { if (sender is ScrollViewer viewer && viewer.ScrollableHeight > 0) Reveal(viewer); }
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
