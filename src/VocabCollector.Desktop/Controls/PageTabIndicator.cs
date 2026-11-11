using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VocabCollector.Desktop.Controls;

public sealed class PageTabIndicator : FrameworkElement, IDisposable
{
    public static readonly DependencyProperty AnimateTransitionsProperty = DependencyProperty.Register(nameof(AnimateTransitions), typeof(bool), typeof(PageTabIndicator),
        new PropertyMetadata(true, (sender, args) => { if (!(bool)args.NewValue) ((PageTabIndicator)sender).Settle(); }));
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(PageTabIndicator), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(PageTabIndicator),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty LeftEdgeProperty = DependencyProperty.Register(nameof(LeftEdge), typeof(double), typeof(PageTabIndicator), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty RightEdgeProperty = DependencyProperty.Register(nameof(RightEdge), typeof(double), typeof(PageTabIndicator), new FrameworkPropertyMetadata(56d, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool AnimateTransitions { get => (bool)GetValue(AnimateTransitionsProperty); set => SetValue(AnimateTransitionsProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public double LeftEdge => (double)GetValue(LeftEdgeProperty);
    public double RightEdge => (double)GetValue(RightEdgeProperty);
    private double _left, _right = 56;
    private int _version;
    private bool _disposed;

    public PageTabIndicator()
    {
        IsHitTestVisible = false; Focusable = false; Height = 1; Opacity = .8;
        SetResourceReference(LineBrushProperty, "Accent");
        Unloaded += (_, _) => Settle();
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var length = Math.Max(0, RightEdge - LeftEdge);
        var line = Orientation == Orientation.Vertical
            ? new Rect(0, LeftEdge, 1, length)
            : new Rect(LeftEdge, 0, length, 1);
        drawingContext.DrawRectangle(LineBrush, null, line);
    }
    public void MoveTo(double left, double width, bool animate = true)
    {
        if (_disposed) return;
        if (!double.IsFinite(left) || !double.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (animate && Math.Abs(left - _left) + Math.Abs(left + width - _right) < .1) return;
        var fromLeft = LeftEdge; var fromRight = RightEdge;
        _left = left; _right = left + width;
        StopClocks();
        if (!animate || !AnimateTransitions || !SystemParameters.ClientAreaAnimation || !IsLoaded || !IsVisible ||
            Math.Abs(fromLeft - _left) + Math.Abs(fromRight - _right) < .1) { Settle(); return; }
        // WPF can expose the base value until the replacement clock's first
        // tick. Keep that value at the current position to avoid a one-frame jump.
        SetValue(LeftEdgeProperty, fromLeft); SetValue(RightEdgeProperty, fromRight);
        var toRight = _left + _right > fromLeft + fromRight;
        var leftMotion = CreateEdgeAnimation(fromLeft, _left, leading: !toRight);
        var rightMotion = CreateEdgeAnimation(fromRight, _right, leading: toRight);
        var version = _version;
        (toRight ? leftMotion : rightMotion).Completed += (_, _) => { if (!_disposed && version == _version) Settle(); };
        BeginAnimation(LeftEdgeProperty, leftMotion, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(RightEdgeProperty, rightMotion, HandoffBehavior.SnapshotAndReplace);
    }
    internal static DoubleAnimationUsingKeyFrames CreateEdgeAnimation(double from, double target, bool leading)
    {
        var duration = TimeSpan.FromMilliseconds(250);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (!leading)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(target, KeyTime.FromTimeSpan(leading ? TimeSpan.FromMilliseconds(150) : duration),
            new CubicEase { EasingMode = leading ? EasingMode.EaseOut : EasingMode.EaseInOut }));
        if (leading)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(target, KeyTime.FromTimeSpan(duration)));
        return animation;
    }
    private void StopClocks() { _version++; BeginAnimation(LeftEdgeProperty, null); BeginAnimation(RightEdgeProperty, null); }
    private void Settle() { StopClocks(); SetValue(LeftEdgeProperty, _left); SetValue(RightEdgeProperty, _right); }
    public void Dispose() { if (_disposed) return; Settle(); _disposed = true; }
}
