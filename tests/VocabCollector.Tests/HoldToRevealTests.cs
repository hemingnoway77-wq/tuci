using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Learning.Effects;

namespace VocabCollector.Tests;

[Collection("Hold-to-reveal WPF input")]
public sealed class HoldToRevealTests
{
    [Fact]
    public void FrostShaderSpreadsAnEightPixelMarkHorizontallyWithoutVerticalRefraction() => MarkdownPreviewTests.OnSta(() =>
    {
        var original = RenderMark(null, 3.6);
        var spread = RenderMark(new InkDistortionEffect { SurfaceSize = new Point(160, 160) }, 3.6);
        var before = InkBounds(original);
        var after = InkBounds(spread);
        Assert.True(after.Width >= before.Width * 2, $"Horizontal ink spread must dominate: source={before}, spread={after}.");
        Assert.InRange(after.Height, before.Height - 2, before.Height + 2);
        Assert.True(after.Width >= after.Height * 2, $"Expected horizontal texture, not isotropic blur: {after}.");
    });

    [Fact]
    public void FrostShaderAtZeroStrengthPreservesTheOriginalTextPositionAndPixels() => MarkdownPreviewTests.OnSta(() =>
    {
        var original = RenderMark(null, 0);
        var clear = RenderMark(new InkDistortionEffect { Amount = 0, SurfaceSize = new Point(160, 160) }, 0);
        Assert.Equal(InkBounds(original), InkBounds(clear));
        Assert.All(original.Zip(clear), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 1));
    });

    private static byte[] RenderMark(Effect? effect, double radius)
    {
        var canvas = new Canvas { Width = 160, Height = 160, Background = Brushes.Transparent };
        var surface = new Canvas { Width = 160, Height = 160, Background = Brushes.Transparent, Effect = effect };
        var mark = new Rectangle { Width = 8, Height = 8, Fill = Brushes.Black, Effect = new BlurEffect { Radius = radius } };
        Canvas.SetLeft(mark, 76); Canvas.SetTop(mark, 76); surface.Children.Add(mark); canvas.Children.Add(surface);
        canvas.Measure(new Size(160, 160)); canvas.Arrange(new Rect(0, 0, 160, 160)); canvas.UpdateLayout();
        var image = new RenderTargetBitmap(160, 160, 96, 96, PixelFormats.Pbgra32); image.Render(canvas);
        var pixels = new byte[160 * 160 * 4]; image.CopyPixels(pixels, 160 * 4, 0); return pixels;
    }

    private static Rect InkBounds(byte[] pixels)
    {
        var points = Enumerable.Range(0, 160 * 160).Where(index => pixels[index * 4 + 3] > 3).ToArray();
        var left = points.Min(index => index % 160); var right = points.Max(index => index % 160);
        var top = points.Min(index => index / 160); var bottom = points.Max(index => index / 160);
        return new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    [Fact]
    public void FrostUsesStrongerStaticBlurAndKeepsItsEffectWhileRevealing() => Run((control, owner, other) =>
    {
        var meaning = Meaning(control);
        var blur = Assert.IsType<BlurEffect>(meaning.Effect);
        AssertHiddenFrost(control);
        Assert.False(blur.HasAnimatedProperties);
        MouseDown(control);
        Assert.True(control.IsRevealed);
        Assert.Same(blur, meaning.Effect);
        if (SystemParameters.ClientAreaAnimation)
        {
            Assert.True(blur.HasAnimatedProperties);
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(45));
            Assert.InRange(blur.Radius, .05, ExpectedHiddenRadius(control) - .05);
        }
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(160));
        Assert.InRange(blur.Radius, 0, .01);
        MouseUp(control);
        Assert.False(control.IsRevealed);
        Assert.Same(blur, meaning.Effect);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(150));
        AssertHiddenFrost(control);
    });

    [Fact]
    public void FrostReplacementImmediatelyStopsARevealBeforeShowingTheNewMeaning() => Run((control, owner, other) =>
    {
        var blur = Assert.IsType<BlurEffect>(Meaning(control).Effect);
        MouseDown(control);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(45));
        control.Text = "下一张词条必须始终被完全遮蔽。";
        Assert.False(control.IsRevealed);
        Assert.Same(blur, Meaning(control).Effect);
        AssertHiddenFrost(control);
        Assert.False(blur.HasAnimatedProperties);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(180));
        AssertHiddenFrost(control);
        Assert.False(control.IsRevealed);
        AssertPlainMeaning(control);
    });

    [Fact]
    public void FrostRapidReleaseAndPressContinueFromTheCurrentRadius() => Run((control, owner, other) =>
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var blur = Assert.IsType<BlurEffect>(Meaning(control).Effect);
        MouseDown(control);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(45));
        var beforeRelease = blur.Radius;
        MouseUp(control);
        Assert.InRange(Math.Abs(blur.Radius - beforeRelease), 0, .01);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30));
        var beforePress = blur.Radius;
        MouseDown(control);
        Assert.InRange(Math.Abs(blur.Radius - beforePress), 0, .01);
        control.Hide();
        AssertHiddenFrost(control);
        Assert.False(blur.HasAnimatedProperties);
    });

    [Fact]
    public void FrostCanDisableMotionAndKeepsEverySafetyDismissalFullyMasked() => Run((control, owner, other) =>
    {
        control.AnimateTransitions = false;
        var blur = Assert.IsType<BlurEffect>(Meaning(control).Effect);
        MouseDown(control);
        Assert.Equal(0, blur.Radius);
        Assert.False(blur.HasAnimatedProperties);
        MouseUp(control);
        AssertHiddenFrost(control);
        MouseDown(control);
        control.CanReveal = false;
        Assert.False(control.IsRevealed);
        AssertHiddenFrost(control);
        Assert.False(blur.HasAnimatedProperties);
        var warp = Descendants<Grid>(control).Select(grid => grid.Effect).OfType<InkDistortionEffect>().SingleOrDefault();
        if (warp is not null) { Assert.Equal(1, warp.Amount); Assert.False(warp.HasAnimatedProperties); }
        AssertPlainMeaning(control);
    });

    [Fact]
    public void FrostRemainsStationaryWhenThereIsNoHeldInput() => MarkdownPreviewTests.OnSta(() =>
    {
        var canvas = RenderCanvas();
        var control = new HoldToReveal { Text = "MMMM dissolved ink", Width = 180 };
        Canvas.SetLeft(control, 32); Canvas.SetTop(control, 32); canvas.Children.Add(control);
        var before = Snapshot(canvas);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(160));
        Assert.Equal(before, Snapshot(canvas));
        Assert.False(Assert.IsType<BlurEffect>(Meaning(control).Effect).HasAnimatedProperties);
    });

    [Fact]
    public void BlurHaloFadesAcrossTheTextBoundaryInsteadOfBeingCutAtTheControlEdge() => MarkdownPreviewTests.OnSta(() =>
    {
        var canvas = RenderCanvas();
        var control = new HoldToReveal { Text = "MMMM meaning", Width = 180 };
        Canvas.SetLeft(control, 32); Canvas.SetTop(control, 32); canvas.Children.Add(control);
        var pixels = Snapshot(canvas);
        var left = Enumerable.Range(0, 300).First(x => ColumnAlpha(pixels, x) > 0);
        Assert.True(left < 32, $"The blur was clipped at the text/control edge: first alpha column {left}.");
        Assert.InRange(ColumnAlpha(pixels, left), 1, 12);
        Assert.True(ColumnAlpha(pixels, left) < ColumnAlpha(pixels, 34));
        Assert.True(ColumnAlpha(pixels, 31) > 0);
        Assert.Equal(new Point(32, 32), Meaning(control).TranslatePoint(new Point(), canvas));
        Assert.Equal(control.Text, Meaning(control).Text);
    });

    [Fact]
    public void WiderScrollViewportPreservesHaloWhileMeaningKeepsItsOriginalAlignmentAndWidth() => MarkdownPreviewTests.OnSta(() =>
    {
        var clipped = ScrollHost(bleed: false);
        var clippedPixels = Snapshot(clipped.Canvas);
        var padded = ScrollHost(bleed: true);
        var paddedPixels = Snapshot(padded.Canvas);
        Assert.Equal(0, ColumnAlpha(clippedPixels, 31));
        Assert.True(ColumnAlpha(paddedPixels, 31) > 0, "A 32-DIP viewport allowance must preserve the horizontal ink diffusion halo.");
        Assert.Equal(Meaning(clipped.Control).TranslatePoint(new Point(), clipped.Canvas), Meaning(padded.Control).TranslatePoint(new Point(), padded.Canvas));
        Assert.Equal(Meaning(clipped.Control).ActualWidth, Meaning(padded.Control).ActualWidth);
        Assert.Equal(Meaning(clipped.Control).ActualHeight, Meaning(padded.Control).ActualHeight);
    });

    private static Canvas RenderCanvas()
    {
        var canvas = new Canvas { Width = 300, Height = 120, Background = Brushes.Transparent };
        canvas.Resources["Ink"] = Brushes.Black;
        canvas.Resources["UiFont"] = new FontFamily("Segoe UI");
        return canvas;
    }
    private static (Canvas Canvas, HoldToReveal Control) ScrollHost(bool bleed)
    {
        var canvas = RenderCanvas();
        var frame = new Grid { Width = 220, Height = 70 };
        Canvas.SetLeft(frame, 32); Canvas.SetTop(frame, 32);
        var viewport = new ScrollViewer { Style = null, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(bleed ? -32 : 0, 0, bleed ? -32 : 0, 0) };
        var content = new StackPanel { Margin = new Thickness(bleed ? 32 : 0, 0, bleed ? 32 : 0, 0) };
        var control = new HoldToReveal { Text = "MMMM meaning" };
        content.Children.Add(control); viewport.Content = content; frame.Children.Add(viewport); canvas.Children.Add(frame);
        return (canvas, control);
    }
    private static byte[] Snapshot(FrameworkElement element)
    {
        element.Measure(new Size(300, 120)); element.Arrange(new Rect(0, 0, 300, 120)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(300, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[300 * 120 * 4]; bitmap.CopyPixels(pixels, 300 * 4, 0); return pixels;
    }
    private static int ColumnAlpha(byte[] pixels, int column) => Enumerable.Range(0, 120).Max(row => pixels[(row * 300 + column) * 4 + 3]);
    [Fact]
    public void LeavingMeaningAreaConcealsEvenWhileInputRemainsHeld() => Run((control, owner, other) =>
    {
        MouseDown(control); Assert.True(control.IsRevealed);
        control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        Assert.False(control.IsRevealed);
        Assert.IsType<BlurEffect>(Meaning(control).Effect);
        Assert.NotSame(control, Mouse.Captured);
        control.Focus(); Key(control, Keyboard.KeyDownEvent);
        control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        Key(control, Keyboard.KeyDownEvent, repeat: true);
        Assert.False(control.IsRevealed);
    });

    [Fact]
    public void FocusAndTeardownKeepPlainTextWithoutChromeOrRebindingResources() => Run((control, owner, other) =>
    {
        var themes = new ThemeManager(owner.Resources);
        var meaning = Meaning(control);
        var inkReference = meaning.ReadLocalValue(TextBlock.ForegroundProperty);
        control.Focus();
        AssertPlainMeaning(control);
        themes.Apply(AppTheme.Pink);
        Assert.Same(owner.Resources["Ink"], meaning.Foreground);
        MouseDown(control);
        other.Focus();
        Assert.False(control.IsRevealed);
        AssertPlainMeaning(control);
        Assert.Same(inkReference, meaning.ReadLocalValue(TextBlock.ForegroundProperty));
        owner.Resources.Clear();
        owner.Resources.MergedDictionaries.Clear();
        owner.Content = null;
        control.Hide();
        Assert.False(control.IsRevealed);
        Assert.Same(inkReference, meaning.ReadLocalValue(TextBlock.ForegroundProperty));
    });
    [Fact]
    public void MouseReleaseStillConcealsWhenSpaceWasAlsoPressed() => Run((control, owner, other) =>
    {
        MouseDown(control);
        Key(control, Keyboard.KeyDownEvent);
        Assert.True(control.IsRevealed);
        MouseUp(control);
        Assert.False(control.IsRevealed);
        Key(control, Keyboard.KeyDownEvent, repeat: true);
        Assert.False(control.IsRevealed);
    });
    [Fact]
    public void MouseHoldUsesRealBlurAndReleaseImmediatelyHidesMeaning() => Run((control, owner, other) =>
    {
        var meaning = Meaning(control);
        var blur = Assert.IsType<BlurEffect>(meaning.Effect);
        AssertHiddenFrost(control);
        Assert.Equal(1, meaning.Opacity);
        Assert.False(control.IsRevealed);
        AssertPlainMeaning(control);
        owner.UpdateLayout();
        var size = new Size(meaning.ActualWidth, meaning.ActualHeight);
        Assert.InRange(Math.Abs(control.ActualWidth - size.Width), 0, 0.1);
        Assert.InRange(Math.Abs(control.ActualHeight - size.Height), 0, 0.1);
        MouseDown(control);
        owner.UpdateLayout();
        Assert.Same(control, Mouse.Captured);
        Assert.True(control.IsRevealed);
        Assert.Same(blur, meaning.Effect);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(160));
        Assert.InRange(blur.Radius, 0, .01);
        Assert.Equal(size, new Size(meaning.ActualWidth, meaning.ActualHeight));
        MouseUp(control);
        owner.UpdateLayout();
        Assert.False(control.IsRevealed);
        Assert.IsType<BlurEffect>(meaning.Effect);
        Assert.NotSame(control, Mouse.Captured);
        Assert.Equal(size, new Size(meaning.ActualWidth, meaning.ActualHeight));
        Assert.Equal(control.Text, meaning.Text);
    });

    [Fact]
    public void LostMouseCaptureAndKeyboardFocusConcealImmediately() => Run((control, owner, other) =>
    {
        MouseDown(control);
        Assert.True(control.IsRevealed);
        Mouse.Capture(other);
        Assert.False(control.IsRevealed);
        Assert.Same(other, Mouse.Captured);
        Mouse.Capture(null);
        MouseDown(control);
        Assert.True(control.IsRevealed);
        other.Focus();
        Assert.False(control.IsRevealed);
        Assert.IsType<BlurEffect>(Meaning(control).Effect);
    });

    [Fact]
    public void SpaceIsAHoldAndAutoRepeatCannotRevealAfterExplicitHide() => Run((control, owner, other) =>
    {
        control.Focus();
        var down = Key(control, Keyboard.KeyDownEvent);
        Assert.True(down.Handled);
        Assert.True(control.IsRevealed);
        control.Hide();
        Assert.False(control.IsRevealed);
        Key(control, Keyboard.KeyDownEvent, repeat: true);
        Assert.False(control.IsRevealed);
        Key(control, Keyboard.KeyUpEvent);
        Key(control, Keyboard.KeyDownEvent);
        Assert.True(control.IsRevealed);
        var up = Key(control, Keyboard.KeyUpEvent);
        Assert.True(up.Handled);
        Assert.False(control.IsRevealed);
    });

    [Fact]
    public void TextChangeDisablePageHideAndUnloadAllConceal() => Run((control, owner, other) =>
    {
        MouseDown(control); control.Text = "另一张卡的释义";
        Assert.False(control.IsRevealed);
        Assert.Equal(control.Text, Meaning(control).Text);
        MouseDown(control); control.Hide();
        Assert.False(control.IsRevealed);
        MouseDown(control); control.IsEnabled = false;
        Assert.False(control.IsRevealed);
        Assert.IsType<BlurEffect>(Meaning(control).Effect);
        control.IsEnabled = true;
        MouseDown(control); control.Visibility = Visibility.Collapsed;
        Assert.False(control.IsRevealed);
        control.Visibility = Visibility.Visible; owner.UpdateLayout();
        MouseDown(control); Assert.True(control.IsRevealed);
        owner.Content = new TextBlock { Text = "another page" };
        Assert.False(control.IsRevealed);
        Assert.NotSame(control, Mouse.Captured);
    });

    [Fact]
    public void WindowDeactivationHidesAStillHeldMeaning() => Run((control, owner, other) =>
    {
        MouseDown(control); Assert.True(control.IsRevealed);
        // Local Window values isolate this STA from an Application/theme created
        // by another test STA, whose implicit Window style owns image resources.
        var another = new Window { Style = null, Icon = null, Content = new Button { Content = "another window" }, Width = 240, Height = 120, ShowInTaskbar = false };
        try
        {
            another.Show(); another.Activate();
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30));
            Assert.False(control.IsRevealed);
            Assert.IsType<BlurEffect>(Meaning(control).Effect);
        }
        finally { another.Close(); }
    });

    [Fact]
    public void UnavailableStateStaysMaskedAndAllThreeThemesUpdateTheSamePlainText() => Run((control, owner, other) =>
    {
        control.CanReveal = false; control.Text = "还没有可复习词条。";
        AssertHiddenFrost(control);
        MouseDown(control); Assert.False(control.IsRevealed);
        AssertPlainMeaning(control);
        var themes = new ThemeManager(owner.Resources);
        var meaning = Meaning(control);
        foreach (var theme in new[] { AppTheme.WarmNeutral, AppTheme.Mono, AppTheme.Pink })
        {
            themes.Apply(theme);
            Assert.Same(owner.Resources["Ink"], meaning.Foreground);
            Assert.Equal(1, meaning.Opacity);
            AssertPlainMeaning(control);
        }
        control.CanReveal = true;
        Assert.IsType<BlurEffect>(meaning.Effect);
    });

    private static double ExpectedHiddenRadius(HoldToReveal control) =>
        Descendants<Grid>(control).Any(grid => grid.Effect is InkDistortionEffect) ? 3.6 : 12;

    private static void AssertHiddenFrost(HoldToReveal control)
    {
        Assert.Equal(ExpectedHiddenRadius(control), Assert.IsType<BlurEffect>(Meaning(control).Effect).Radius);
        var warp = Descendants<Grid>(control).Select(grid => grid.Effect).OfType<InkDistortionEffect>().SingleOrDefault();
        if (warp is not null)
        {
            Assert.Equal(1, warp.Amount);
            Assert.Equal(24, warp.SmearWidth);
            Assert.Equal(.30, warp.Grain);
        }
    }

    private static void AssertPlainMeaning(HoldToReveal control)
    {
        Assert.Same(Meaning(control), Assert.Single(Descendants<TextBlock>(control)));
        Assert.All(Descendants<Border>(control), border =>
        {
            Assert.Equal(new Thickness(0), border.BorderThickness);
            Assert.Equal(new Thickness(0), border.Padding);
            Assert.True(border.Background is null || border.Background is SolidColorBrush { Color.A: 0 });
        });
        Assert.All(Descendants<Panel>(control), panel =>
            Assert.True(panel.Background is null || panel.Background is SolidColorBrush { Color.A: 0 }));
        Assert.Null(control.FocusVisualStyle);
    }

    [Fact]
    public void HiddenMeaningDoesNotBecomeAnAutomationNameOrASelectableTextBox() => Run((control, owner, other) =>
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(control);
        Assert.NotNull(peer);
        Assert.DoesNotContain(control.Text, peer.GetName());
        Assert.DoesNotContain(control.Text, peer.GetHelpText());
        Assert.All(peer.GetChildren() ?? [], child => Assert.DoesNotContain(control.Text, child.GetName()));
        Assert.Empty(Descendants<TextBox>(control));
        Assert.Empty(Descendants<RichTextBox>(control));
        Assert.False(Meaning(control).Focusable);
    });

    [Fact]
    public void WrappedMeaningDeterminesHeightWithoutScalingTheFont() => Run((control, owner, other) =>
    {
        control.Width = 180; control.Text = "短释义"; owner.UpdateLayout();
        var shortHeight = control.ActualHeight;
        var size = Meaning(control).FontSize;
        control.Text = string.Concat(Enumerable.Repeat("较长的中文释义需要按照实际宽度自然换行。", 12));
        owner.UpdateLayout();
        Assert.True(control.ActualHeight > shortHeight + 40);
        Assert.Equal(size, Meaning(control).FontSize);
    });

    private static void Run(Action<HoldToReveal, Window, Button> action) => MarkdownPreviewTests.OnSta(() =>
    {
        var control = new HoldToReveal { Text = "v. 介绍；把某人介绍给别人认识\nv. 引进；首次引入某事物" };
        var other = new Button { Content = "another action" };
        var panel = new StackPanel(); panel.Children.Add(control); panel.Children.Add(other);
        // Keep local values even when the teardown test removes these resources:
        // falling back to a foreign STA's implicit Window style can load its Icon.
        var owner = new Window { Style = null, Icon = null, Content = panel, Width = 560, Height = 380, ShowInTaskbar = false };
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try { owner.Show(); owner.Activate(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30)); action(control, owner, other); }
        finally { if (Mouse.Captured is not null) Mouse.Capture(null); owner.Close(); }
    });
    private static TextBlock Meaning(HoldToReveal control) => Assert.IsType<TextBlock>(control.FindName("RoamingMeaningText"));
    private static void MouseDown(HoldToReveal control) => control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
    private static void MouseUp(HoldToReveal control) => control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
    private static KeyEventArgs Key(HoldToReveal control, RoutedEvent routedEvent, bool repeat = false)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control)!, Environment.TickCount, System.Windows.Input.Key.Space) { RoutedEvent = routedEvent };
        if (repeat)
        {
            var setter = typeof(KeyEventArgs).GetProperty(nameof(KeyEventArgs.IsRepeat))!.GetSetMethod(true);
            if (setter is not null) setter.Invoke(args, [true]);
            else typeof(KeyEventArgs).GetField("_isRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(args, true);
        }
        control.RaiseEvent(args); return args;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}

[CollectionDefinition("Hold-to-reveal WPF input", DisableParallelization = true)]
public sealed class HoldToRevealInputCollection;
