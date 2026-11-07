using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace VocabCollector.Desktop;

/// <summary>Short wheel-only easing; direct input and document navigation retain control.</summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(SmoothScroll));
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);

    public static void Cancel(DependencyObject target)
    {
        var viewer = target as ScrollViewer ?? (target is Control control ? control.Template?.FindName("PART_ContentHost", control) as ScrollViewer : null);
        if (viewer?.GetValue(StateProperty) is State state) state.Stop(freezePending: true);
    }

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ScrollViewer viewer) return;
        if (viewer.GetValue(StateProperty) is State previous) previous.Detach();
        viewer.ClearValue(StateProperty);
        if ((bool)args.NewValue) viewer.SetValue(StateProperty, new State(viewer));
    }

    private static DependencyObject? Parent(DependencyObject node) => node switch
    {
        FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
        Visual => VisualTreeHelper.GetParent(node),
        _ => LogicalTreeHelper.GetParent(node)
    };

    private static ScrollViewer? WheelTarget(DependencyObject? source)
    {
        for (var node = source; node is not null; node = Parent(node))
        {
            if (node is ScrollViewer viewer) return viewer;
            // Runs in a FlowDocument belong logically to its editor, while the
            // editor's ScrollViewer is in the separate control template tree.
            if (node is TextBoxBase editor && editor.Template?.FindName("PART_ContentHost", editor) is ScrollViewer inner) return inner;
        }
        return null;
    }

    private static ScrollViewer? OuterViewer(ScrollViewer viewer)
    {
        for (var node = Parent(viewer); node is not null; node = Parent(node))
            if (node is ScrollViewer outer) return outer;
        return null;
    }

    private sealed class State
    {
        private const double DurationMilliseconds = 90;
        private readonly ScrollViewer _viewer;
        private TextBoxBase? _editor;
        private bool _active;
        private double _start, _target, _requested;
        private long _started;

        public State(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.PreviewMouseWheel += Wheel;
            viewer.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Pointer), true);
            viewer.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Key), true);
            viewer.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(BringIntoView), true);
            viewer.ScrollChanged += Scrolled;
            viewer.Loaded += Loaded;
            viewer.Unloaded += Unloaded;
            viewer.IsVisibleChanged += VisibilityChanged;
            AttachEditor();
        }

        private void AttachEditor()
        {
            if (_editor is not null) return;
            var editor = _viewer.TemplatedParent as TextBoxBase;
            for (var node = Parent(_viewer); editor is null && node is not null; node = Parent(node)) editor = node as TextBoxBase;
            if (editor is null) return;
            _editor = editor;
            editor.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Wheel));
            editor.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Pointer), true);
            editor.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Key), true);
            editor.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(BringIntoView), true);
        }

        private void DetachEditor()
        {
            if (_editor is not { } editor) return;
            _editor = null;
            editor.RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(Wheel));
            editor.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Pointer));
            editor.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Key));
            editor.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(BringIntoView));
        }

        private void Wheel(object sender, MouseWheelEventArgs args)
        {
            if (args.Handled || args.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None || !_viewer.IsVisible || !_viewer.IsLoaded) return;
            if (WheelTarget(args.OriginalSource as DependencyObject) is { } target && !ReferenceEquals(target, _viewer)) return;
            for (var node = args.OriginalSource as DependencyObject; node is not null && node != _viewer; node = Parent(node))
                if (node is Slider or ComboBox) return;
            if (_viewer.IsMouseCaptureWithin) { Stop(freezePending: true); return; }
            if (RouteWheel(_viewer, args.Delta)) args.Handled = true;
        }

        private static bool RouteWheel(ScrollViewer viewer, int delta)
        {
            for (var current = viewer; current is not null; current = OuterViewer(current))
            {
                if (!GetIsEnabled(current) || current.GetValue(StateProperty) is not State state) return false;
                var canMove = delta < 0 ? current.VerticalOffset < current.ScrollableHeight - .1 : current.VerticalOffset > .1;
                if (!canMove) { state.Stop(freezePending: true); continue; }
                // Item-based virtualized lists retain their native integer-unit
                // scrolling. Interpolating those offsets would move whole rows.
                if (current.CanContentScroll && state._editor is null) return false;
                var lines = SystemParameters.WheelScrollLines;
                if (lines == 0) return false;
                var distance = lines < 0 ? Math.Max(1, current.ViewportHeight - 16) : lines * 16d;
                state.Move(-delta / 120d * distance);
                return true;
            }
            return false;
        }

        private void Move(double distance)
        {
            var current = _viewer.VerticalOffset;
            var accumulated = _active && Math.Sign(distance) == Math.Sign(_target - current) ? _target : current;
            var target = Math.Clamp(accumulated + distance, 0, _viewer.ScrollableHeight);
            Stop(freezePending: true);
            if (!SystemParameters.ClientAreaAnimation)
            { _viewer.ScrollToVerticalOffset(target); return; }
            _start = current; _target = target; _requested = current; _started = Stopwatch.GetTimestamp();
            _active = true; CompositionTarget.Rendering += Frame;
        }

        private void Frame(object? sender, EventArgs args)
        {
            if (!_viewer.IsLoaded || !_viewer.IsVisible || !GetIsEnabled(_viewer)) { Stop(freezePending: false); return; }
            var progress = Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / DurationMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            _requested = Math.Clamp(_start + (_target - _start) * eased, 0, _viewer.ScrollableHeight);
            _viewer.ScrollToVerticalOffset(_requested);
            if (progress >= 1) Stop(freezePending: false);
        }

        private void Scrolled(object sender, ScrollChangedEventArgs args)
        {
            if (!_active || !ReferenceEquals(args.OriginalSource, _viewer)) return;
            if (args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0 ||
                (args.VerticalChange != 0 && Math.Abs(_viewer.VerticalOffset - _requested) > 1))
                Stop(freezePending: false); // A direct offset or document/layout change takes precedence.
        }
        private void Pointer(object sender, MouseButtonEventArgs args) => Stop(freezePending: true);
        private void Key(object sender, KeyEventArgs args) => Stop(freezePending: true);
        private void BringIntoView(object sender, RequestBringIntoViewEventArgs args) => Stop(freezePending: false);
        private void Loaded(object sender, RoutedEventArgs args) => AttachEditor();
        private void Unloaded(object sender, RoutedEventArgs args) { Stop(freezePending: false); DetachEditor(); }
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!(bool)args.NewValue) Stop(freezePending: true); }

        public void Stop(bool freezePending)
        {
            if (!_active) return;
            _active = false; CompositionTarget.Rendering -= Frame;
            // Replace an offset queued by the last rendering frame before a
            // pointer/key action queues its own precise native movement.
            if (freezePending && _viewer.IsLoaded) _viewer.ScrollToVerticalOffset(_viewer.VerticalOffset);
        }

        public void Detach()
        {
            Stop(freezePending: true);
            DetachEditor();
            _viewer.PreviewMouseWheel -= Wheel;
            _viewer.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Pointer));
            _viewer.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Key));
            _viewer.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler(BringIntoView));
            _viewer.ScrollChanged -= Scrolled;
            _viewer.Loaded -= Loaded;
            _viewer.Unloaded -= Unloaded;
            _viewer.IsVisibleChanged -= VisibilityChanged;
        }
    }
}
