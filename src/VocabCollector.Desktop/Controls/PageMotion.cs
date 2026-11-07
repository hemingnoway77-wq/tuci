using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VocabCollector.Desktop.Controls;

/// <summary>Short presentation-only motion; document layout and scroll offsets remain untouched.</summary>
public static class PageMotion
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(PageMotion));
    private sealed class State(FrameworkElement view)
    {
        public readonly Transform Original = view.RenderTransform;
        public readonly double Opacity = view.Opacity;
        public readonly TranslateTransform Offset = new();
        public TransformGroup? Group;
        public int Version;
    }
    public static void Enter(FrameworkElement view, bool forward = true)
    {
        Reset(view);
        if (!SystemParameters.ClientAreaAnimation || !view.IsLoaded || !view.IsVisible) return;
        var state = (State?)view.GetValue(StateProperty);
        if (state is null)
        {
            state = new State(view); view.SetValue(StateProperty, state);
            view.Unloaded += (_, _) => Reset(view);
        }
        var group = state.Group = new TransformGroup(); group.Children.Add(state.Original); group.Children.Add(state.Offset);
        view.RenderTransform = group;
        var version = ++state.Version;
        var duration = TimeSpan.FromMilliseconds(200);
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var offset = new DoubleAnimation(forward ? 16 : -16, 0, duration)
            { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
        offset.Completed += (_, _) => { if (state.Version == version) Reset(view); };
        state.Offset.BeginAnimation(TranslateTransform.XProperty, offset, HandoffBehavior.SnapshotAndReplace);
        view.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(state.Opacity * .76, state.Opacity, duration)
            { EasingFunction = easing, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }
    public static void Reset(FrameworkElement view)
    {
        if (view.GetValue(StateProperty) is not State state) return;
        state.Version++; state.Offset.BeginAnimation(TranslateTransform.XProperty, null);
        view.BeginAnimation(UIElement.OpacityProperty, null); view.Opacity = state.Opacity;
        if (ReferenceEquals(view.RenderTransform, state.Group)) view.RenderTransform = state.Original;
        state.Group = null;
    }
}
