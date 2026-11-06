using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

[Collection("Hold-to-reveal WPF input")]
public sealed class HoldToRevealTests
{
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
        Assert.True(ColumnAlpha(paddedPixels, 31) > 0, "A 10-DIP viewport allowance must preserve the 8-DIP blur halo.");
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
            Margin = new Thickness(bleed ? -10 : 0, 0, bleed ? -10 : 0, 0) };
        var content = new StackPanel { Margin = new Thickness(bleed ? 10 : 0, 0, bleed ? 10 : 0, 0) };
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
        Assert.InRange(Assert.IsType<BlurEffect>(meaning.Effect).Radius, 6, 9);
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
        Assert.Null(meaning.Effect);
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
    public void EmptyStateRemainsReadableAndAllThreeThemesUpdateTheSamePlainText() => Run((control, owner, other) =>
    {
        control.CanReveal = false; control.Text = "还没有可复习词条。";
        Assert.Null(Meaning(control).Effect);
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
