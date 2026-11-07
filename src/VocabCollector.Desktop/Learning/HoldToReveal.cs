using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VocabCollector.Desktop.Learning.Effects;

namespace VocabCollector.Desktop.Learning;

public sealed class HoldToReveal : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HoldToReveal), new FrameworkPropertyMetadata("", OnTextChanged));
    public static readonly DependencyProperty CanRevealProperty = DependencyProperty.Register(nameof(CanReveal), typeof(bool), typeof(HoldToReveal), new FrameworkPropertyMetadata(true, OnCanRevealChanged));
    public static readonly DependencyProperty AnimateTransitionsProperty = DependencyProperty.Register(nameof(AnimateTransitions), typeof(bool), typeof(HoldToReveal), new PropertyMetadata(true, OnAnimationPreferenceChanged));
    private static readonly DependencyPropertyKey IsRevealedPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsRevealed), typeof(bool), typeof(HoldToReveal), new PropertyMetadata(false));
    public static readonly DependencyProperty IsRevealedProperty = IsRevealedPropertyKey.DependencyProperty;
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool CanReveal { get => (bool)GetValue(CanRevealProperty); set => SetValue(CanRevealProperty, value); }
    public bool AnimateTransitions { get => (bool)GetValue(AnimateTransitionsProperty); set => SetValue(AnimateTransitionsProperty, value); }
    public bool IsRevealed => (bool)GetValue(IsRevealedProperty);
    private const double FallbackRadius = 12, HiddenRadius = 3.6, HiddenDistortion = 1;
    private readonly BlurEffect _blur = new() { Radius = FallbackRadius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
    private readonly InkDistortionEffect? _distortion;
    private readonly TextBlock _meaning = new() { Name = "RoamingMeaningText", TextWrapping = TextWrapping.Wrap, FontSize = 17, LineHeight = 28, Focusable = false, IsHitTestVisible = false };
    private Window? _window;
    private bool _mouseHeld, _spaceHeld, _hiding;
    private long _visualVersion;

    public HoldToReveal()
    {
        NameScope.SetNameScope(this, new NameScope());
        RegisterName(_meaning.Name, _meaning);
        SetResourceReference(FontFamilyProperty, "UiFont");
        _meaning.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        Background = Brushes.Transparent; Padding = new Thickness(0); BorderThickness = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Top;
        // Blur expands its rendered bounds beyond the glyphs. Do not trim that
        // halo to the unblurred text rectangle or move the clear text inward.
        // A scroll host must likewise reserve bleed outside its aligned content.
        VerticalAlignment = VerticalAlignment.Top; FocusVisualStyle = null; ClipToBounds = false;
        _meaning.Effect = _blur;
        // Keep a conservative Gaussian mask when no suitable graphics device is
        // available. PS 2.0 also supports software fallback after device loss.
        if ((RenderCapability.Tier >> 16) > 0 && RenderCapability.IsPixelShaderVersionSupported(2, 0))
        {
            try { _distortion = new InkDistortionEffect(); }
            catch (Exception error) when (error is System.IO.IOException or InvalidOperationException or ArgumentException or TypeInitializationException) { }
        }
        var inkSurface = new Grid { Background = Brushes.Transparent, Effect = _distortion, ClipToBounds = false };
        inkSurface.Children.Add(_meaning);
        _meaning.SizeChanged += (_, _) =>
        {
            if (_distortion is not null)
                _distortion.SurfaceSize = new Point(Math.Max(1, _meaning.ActualWidth), Math.Max(1, _meaning.ActualHeight));
        };
        var hitSurface = new Grid { Background = Brushes.Transparent };
        hitSurface.Children.Add(inkSurface); Content = hitSurface;
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
        };
        UpdateVisualState();
    }

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (HoldToReveal)sender;
        control.Hide(); control._meaning.Text = args.NewValue as string ?? "";
    }
    private static void OnCanRevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((HoldToReveal)sender).Hide();
    private static void OnAnimationPreferenceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((HoldToReveal)sender).UpdateVisualState();

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
        args.Handled = true; Conceal(animate: true);
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
        args.Handled = true; Conceal(animate: true);
    }
    private void Reveal()
    {
        SetValue(IsRevealedPropertyKey, true); UpdateVisualState(animate: true);
    }

    /// <summary>Conceal on every exposure change, including identical meaning text.</summary>
    public void Hide() => Conceal(animate: false);

    private void Conceal(bool animate)
    {
        if (_hiding) return;
        _hiding = true;
        try
        {
            _mouseHeld = _spaceHeld = false;
            SetValue(IsRevealedPropertyKey, false);
            if (ReferenceEquals(Mouse.Captured, this)) ReleaseMouseCapture();
            UpdateVisualState(animate);
        }
        finally { _hiding = false; }
    }
    private void UpdateVisualState(bool animate = false)
    {
        var version = ++_visualVersion;
        var hidden = !IsRevealed;
        var motion = animate && AnimateTransitions && SystemParameters.ClientAreaAnimation && IsLoaded && IsVisible;
        var duration = TimeSpan.FromMilliseconds(hidden ? 100 : 120);
        Transition(_blur, BlurEffect.RadiusProperty, hidden ? (_distortion is null ? FallbackRadius : HiddenRadius) : 0, motion, duration, version);
        if (_distortion is not null)
            Transition(_distortion, InkDistortionEffect.AmountProperty, hidden ? HiddenDistortion : 0, motion, duration, version);
        Focusable = CanReveal && IsEnabled;
        Cursor = CanReveal && IsEnabled ? Cursors.Hand : Cursors.Arrow;
        AutomationProperties.SetName(this, IsRevealed ? "释义已显示，松开后隐藏" : CanReveal ? "释义，按住查看" : "复习提示");
    }

    private void Transition(Animatable effect, DependencyProperty property, double target, bool animate, TimeSpan duration, long version)
    {
        var current = (double)effect.GetValue(property);
        effect.BeginAnimation(property, null);
        effect.SetValue(property, animate ? current : target);
        if (!animate || Math.Abs(current - target) < .001) return;
        var animation = new DoubleAnimation(current, target, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
        animation.Completed += (_, _) =>
        {
            if (_visualVersion != version) return;
            effect.SetValue(property, target);
            effect.BeginAnimation(property, null);
        };
        effect.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
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
