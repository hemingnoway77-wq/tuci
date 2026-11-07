using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VocabCollector.Desktop.Controls;

public sealed class PageTabIndicator : FrameworkElement, IDisposable
{
    public static readonly DependencyProperty AnimateTransitionsProperty = DependencyProperty.Register(nameof(AnimateTransitions), typeof(bool), typeof(PageTabIndicator),
        new PropertyMetadata(true, (sender, args) => { if (!(bool)args.NewValue) ((PageTabIndicator)sender).Settle(); }));
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(nameof(LineBrush), typeof(Brush), typeof(PageTabIndicator), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty LeftEdgeProperty = DependencyProperty.Register(nameof(LeftEdge), typeof(double), typeof(PageTabIndicator), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty RightEdgeProperty = DependencyProperty.Register(nameof(RightEdge), typeof(double), typeof(PageTabIndicator), new FrameworkPropertyMetadata(56d, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool AnimateTransitions { get => (bool)GetValue(AnimateTransitionsProperty); set => SetValue(AnimateTransitionsProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
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
        drawingContext.DrawRectangle(LineBrush, null, new Rect(LeftEdge, 0, Math.Max(0, RightEdge - LeftEdge), 1));
    }
    public void MoveTo(double left, double width, bool animate = true)
    {
        if (_disposed) return;
        if (!double.IsFinite(left) || !double.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        var fromLeft = LeftEdge; var fromRight = RightEdge;
        _left = left; _right = left + width;
        StopClocks();
        if (!animate || !AnimateTransitions || !SystemParameters.ClientAreaAnimation || !IsLoaded || !IsVisible ||
            Math.Abs(fromLeft - _left) + Math.Abs(fromRight - _right) < .1) { Settle(); return; }
        SetValue(LeftEdgeProperty, fromLeft); SetValue(RightEdgeProperty, fromRight);
        var forward = _left + _right >= fromLeft + fromRight;
        var leftMotion = Edge(fromLeft, _left, !forward);
        var rightMotion = Edge(fromRight, _right, forward);
        var version = _version;
        (forward ? leftMotion : rightMotion).Completed += (_, _) => { if (!_disposed && version == _version) Settle(); };
        BeginAnimation(LeftEdgeProperty, leftMotion, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(RightEdgeProperty, rightMotion, HandoffBehavior.SnapshotAndReplace);
    }
    private static DoubleAnimationUsingKeyFrames Edge(double from, double to, bool leading)
    {
        var motion = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(250), FillBehavior = FillBehavior.HoldEnd };
        motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (!leading) motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(leading ? 150 : 250)),
            new CubicEase { EasingMode = leading ? EasingMode.EaseOut : EasingMode.EaseInOut }));
        if (leading) motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
        return motion;
    }
    private void StopClocks() { _version++; BeginAnimation(LeftEdgeProperty, null); BeginAnimation(RightEdgeProperty, null); }
    private void Settle() { StopClocks(); SetValue(LeftEdgeProperty, _left); SetValue(RightEdgeProperty, _right); }
    public void Dispose() { if (_disposed) return; Settle(); _disposed = true; }
}
