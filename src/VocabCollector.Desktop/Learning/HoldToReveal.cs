using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace VocabCollector.Desktop.Learning;

public sealed class HoldToReveal : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HoldToReveal), new FrameworkPropertyMetadata("", OnTextChanged));
    public static readonly DependencyProperty CanRevealProperty = DependencyProperty.Register(nameof(CanReveal), typeof(bool), typeof(HoldToReveal), new FrameworkPropertyMetadata(true, OnCanRevealChanged));
    private static readonly DependencyPropertyKey IsRevealedPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsRevealed), typeof(bool), typeof(HoldToReveal), new PropertyMetadata(false));
    public static readonly DependencyProperty IsRevealedProperty = IsRevealedPropertyKey.DependencyProperty;
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool CanReveal { get => (bool)GetValue(CanRevealProperty); set => SetValue(CanRevealProperty, value); }
    public bool IsRevealed => (bool)GetValue(IsRevealedProperty);
    private readonly BlurEffect _blur = new() { Radius = 8, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
    private readonly TextBlock _meaning = new() { Name = "RoamingMeaningText", TextWrapping = TextWrapping.Wrap, FontSize = 17, LineHeight = 28, Focusable = false, IsHitTestVisible = false };
    private readonly Border _frost = new() { Name = "FrostedOverlay", Opacity = 0.68, CornerRadius = new CornerRadius(3), IsHitTestVisible = false };
    private readonly TextBlock _hint = new() { Name = "RevealHint", Text = "按住查看释义", FontSize = 14, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 4, 12, 4), IsHitTestVisible = false };
    private readonly Border _surface = new() { Name = "MeaningSurface", Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true };
    private readonly Border _focusRing = new() { Name = "FocusRing", BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private Window? _window;
    private bool _mouseHeld, _spaceHeld, _hiding;

    public HoldToReveal()
    {
        _blur.Freeze();
        NameScope.SetNameScope(this, new NameScope());
        foreach (var element in new FrameworkElement[] { _meaning, _frost, _hint, _surface, _focusRing }) RegisterName(element.Name, element);
        SetResourceReference(FontFamilyProperty, "UiFont");
        _meaning.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        _frost.SetResourceReference(Border.BackgroundProperty, "Sheet");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "InkSecondary");
        _surface.SetResourceReference(Border.BackgroundProperty, "Sheet");
        _surface.SetResourceReference(Border.BorderBrushProperty, "Line");
        _focusRing.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var grid = new Grid(); grid.Children.Add(_meaning); grid.Children.Add(_frost); grid.Children.Add(_hint);
        _surface.Child = grid;
        var layers = new Grid(); layers.Children.Add(_surface); layers.Children.Add(_focusRing); Content = layers;
        AutomationProperties.SetName(this, "释义，按住查看");
        AutomationProperties.SetHelpText(this, "按住鼠标左键或空格查看释义，松开后隐藏。");
        AutomationProperties.SetName(_meaning, "释义内容");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsEnabledChanged += (_, _) => Hide();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Hide(); };
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            if (!IsKeyboardFocusWithin) Hide();
            UpdateFocusRing();
        };
        UpdateVisualState();
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (HoldToReveal)sender;
        control.Hide(); control._meaning.Text = args.NewValue as string ?? "";
    }
    private static void OnCanRevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((HoldToReveal)sender).Hide();

    private bool CanInteract => CanReveal && IsEnabled && IsVisible && IsLoaded;

    protected override void OnMouseDown(MouseButtonEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.ChangedButton != MouseButton.Left || !CanInteract) return;
        args.Handled = true;
        Focus();
        if (!CaptureMouse()) { Hide(); return; }
        _mouseHeld = true; Reveal();
    }
    protected override void OnMouseUp(MouseButtonEventArgs args)
    {
        base.OnMouseUp(args);
        if (args.ChangedButton != MouseButton.Left || !_mouseHeld) return;
        args.Handled = true; Hide();
    }
    protected override void OnMouseLeave(MouseEventArgs args) { base.OnMouseLeave(args); Hide(); }
    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (!_mouseHeld) return;
        var point = args.GetPosition(this);
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight) Hide();
    }
    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        base.OnLostMouseCapture(args);
        if (_mouseHeld) Hide();
    }
    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Key != Key.Space) return;
        args.Handled = true;
        // A repeat following a page change, Hide(), or window deactivation must
        // never reveal the replacement card while the old key is still held.
        if (args.IsRepeat || !CanInteract || !IsKeyboardFocusWithin) return;
        _spaceHeld = true; Reveal();
    }
    protected override void OnKeyUp(KeyEventArgs args)
    {
        base.OnKeyUp(args);
        if (args.Key != Key.Space || (!_spaceHeld && !IsKeyboardFocusWithin)) return;
        args.Handled = true; Hide();
    }
    private void Reveal()
    {
        SetValue(IsRevealedPropertyKey, true); UpdateVisualState();
    }

    /// <summary>Conceal on every exposure change, including identical meaning text.</summary>
    public void Hide()
    {
        if (_hiding) return;
        _hiding = true;
        try
        {
            _mouseHeld = _spaceHeld = false;
            SetValue(IsRevealedPropertyKey, false);
            if (ReferenceEquals(Mouse.Captured, this)) ReleaseMouseCapture();
            UpdateVisualState();
        }
        finally { _hiding = false; }
    }
    private void UpdateVisualState()
    {
        var hidden = CanReveal && !IsRevealed;
        _meaning.Effect = hidden ? _blur : null;
        _frost.Visibility = _hint.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        Focusable = CanReveal && IsEnabled;
        Cursor = CanReveal && IsEnabled ? Cursors.Hand : Cursors.Arrow;
        AutomationProperties.SetName(this, IsRevealed ? "释义已显示，松开后隐藏" : CanReveal ? "释义，按住查看" : "复习提示");
        UpdateFocusRing();
    }
    private void UpdateFocusRing()
    {
        // Keep both brush resource expressions stable for the control's lifetime.
        // Rebinding a DynamicResource while app resources are being torn down can
        // resolve a disposed DeferredAppResourceReference during focus cleanup.
        _focusRing.Visibility = IsLoaded && IsVisible && IsEnabled && CanReveal && IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        DetachWindow(); _window = Window.GetWindow(this);
        if (_window is not null) _window.Deactivated += OnWindowDeactivated;
        Hide();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args) { Hide(); DetachWindow(); }
    private void OnWindowDeactivated(object? sender, EventArgs args) => Hide();
    private void DetachWindow()
    {
        if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
        _window = null;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new HoldToRevealAutomationPeer(this);
    private sealed class HoldToRevealAutomationPeer(HoldToReveal owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(HoldToReveal);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        // The semantic control remains keyboard-focusable with an action description;
        // its blurred, non-selectable rendering is not announced as a hidden answer.
        protected override List<AutomationPeer> GetChildrenCore() => [];
    }
}
