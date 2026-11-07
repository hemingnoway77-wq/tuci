using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace VocabCollector.Desktop.Learning;

/// <summary>Crossfades a frozen frame while the live sidebar takes its final layout once.</summary>
internal sealed class SidebarSnapshotMotion : IDisposable
{
    private readonly FrameworkElement _surface;
    private readonly Image _snapshot;
    private readonly UIElement _input;
    private bool _restoreHitTesting, _active, _disposed;
    private long _version;
    private DpiScale _dpi;

    public SidebarSnapshotMotion(FrameworkElement surface, Image snapshot, UIElement input)
    {
        _surface = surface; _snapshot = snapshot; _input = input;
        surface.SizeChanged += Resized;
        surface.Unloaded += Unloaded;
        surface.IsVisibleChanged += VisibilityChanged;
        surface.LayoutUpdated += LayoutUpdated;
    }

    public void Run(Action applyLayout)
    {
        if (_disposed) return;
        // Include a still-fading previous frame when the direction changes. This
        // captures the visible presentation, never a live VisualBrush dependency.
        var frame = Capture();
        Stop();
        if (frame is null) { applyLayout(); return; }
        var version = _version;
        _dpi = VisualTreeHelper.GetDpi(_surface);
        _snapshot.Source = frame; _snapshot.Visibility = Visibility.Visible;
        _snapshot.Opacity = 0;
        // An ancestor may temporarily coerce hit testing off during a module
        // transition. Preserve this private layout's own value, not that coercion.
        _restoreHitTesting = _input.ReadLocalValue(UIElement.IsHitTestVisibleProperty) is not bool local || local;
        _active = true;
        _input.SetCurrentValue(UIElement.IsHitTestVisibleProperty, false);
        try
        {
            applyLayout();
            _surface.UpdateLayout();
            if (!_active || version != _version) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            fade.Completed += (_, _) => { if (version == _version) Stop(); };
            _snapshot.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        catch { Stop(); throw; }
    }

    private RenderTargetBitmap? Capture()
    {
        if (!SystemParameters.ClientAreaAnimation || !_surface.IsLoaded || !_surface.IsVisible || _surface.ActualWidth <= 0 || _surface.ActualHeight <= 0)
            return null;
        var dpi = VisualTreeHelper.GetDpi(_surface);
        var width = (int)Math.Ceiling(_surface.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(_surface.ActualHeight * dpi.DpiScaleY);
        // Very large multi-monitor windows can use an immediate final layout
        // rather than allocate an oversized transient texture.
        if (width <= 0 || height <= 0 || (long)width * height > 8_000_000) return null;
        try
        {
            _surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(_surface); bitmap.Freeze(); return bitmap;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OutOfMemoryException) { return null; }
    }

    public void Stop()
    {
        ++_version;
        _snapshot.BeginAnimation(UIElement.OpacityProperty, null);
        _snapshot.Source = null; _snapshot.Visibility = Visibility.Collapsed; _snapshot.Opacity = 1;
        if (_active) _input.SetCurrentValue(UIElement.IsHitTestVisibleProperty, _restoreHitTesting);
        _active = false;
    }
    private void Resized(object sender, SizeChangedEventArgs args) => Stop();
    private void Unloaded(object sender, RoutedEventArgs args) => Stop();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!(bool)args.NewValue) Stop(); }
    private void LayoutUpdated(object? sender, EventArgs args)
    {
        if (!_active) return;
        var dpi = VisualTreeHelper.GetDpi(_surface);
        if (dpi.DpiScaleX != _dpi.DpiScaleX || dpi.DpiScaleY != _dpi.DpiScaleY) Stop();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Stop();
        _surface.SizeChanged -= Resized; _surface.Unloaded -= Unloaded;
        _surface.IsVisibleChanged -= VisibilityChanged; _surface.LayoutUpdated -= LayoutUpdated;
    }
}
