using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Controls;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

[Collection("Pane close WPF input")]
public sealed class PaneCloseButtonTests
{
    [Fact]
    public void IconOnlyCloseActionHasACompactHitAreaAndAnAccessibleName() => Run((button, _) =>
    {
        var glyph = Assert.IsType<System.Windows.Shapes.Path>(button.Content);
        Assert.InRange(glyph.ActualWidth, 10, 12);
        Assert.InRange(glyph.ActualHeight, 10, 12);
        Assert.InRange(glyph.StrokeThickness, 1.1, 1.3);
        Assert.Equal(PenLineCap.Round, glyph.StrokeStartLineCap);
        Assert.Equal(PenLineCap.Round, glyph.StrokeEndLineCap);
        Assert.InRange(button.ActualWidth, 28, 30);
        Assert.InRange(button.ActualHeight, 28, 30);
        Assert.NotNull(button.InputHitTest(new Point(2, button.ActualHeight / 2)));
        Assert.Equal("关闭", button.ToolTip);
        Assert.Equal("关闭", AutomationProperties.GetName(button));
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(button.Background).Color);
        Assert.Equal(new Thickness(0), button.BorderThickness);
        Assert.Empty(Descendants<TextBlock>(button));
    });

    [Fact]
    public void HoverPressedAndFocusFeedbackFollowTheCurrentThemeWithoutAFrame() => Run((button, window) =>
    {
        var glyph = Assert.IsType<System.Windows.Shapes.Path>(button.Content);
        var surface = Assert.IsType<Border>(button.Template.FindName("CloseSurface", button));
        var themes = new ThemeManager(window.Resources);
        foreach (var theme in new[] { AppTheme.Mono, AppTheme.WarmNeutral, AppTheme.Pink })
        {
            themes.Apply(theme);
            Assert.Same(window.Resources["InkSecondary"], glyph.Stroke);
            SetMouseOver(button, true);
            Assert.Same(window.Resources["Hover"], surface.Background);
            Assert.InRange(surface.CornerRadius.TopLeft, 10, 15);
            SetMouseOver(button, false);
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(surface.Background).Color);
        }
        button.Focus();
        Assert.Same(window.Resources["Hover"], surface.Background);
        Key(button, Keyboard.KeyDownEvent, System.Windows.Input.Key.Space);
        Assert.True(button.IsPressed);
        Assert.Same(window.Resources["AccentSoft"], surface.Background);
        Key(button, Keyboard.KeyUpEvent, System.Windows.Input.Key.Space);
        Assert.False(button.IsPressed);
        Assert.Equal(new Thickness(0), surface.BorderThickness);
    });

    [Fact]
    public void KeyboardAndAutomationInvokeTheOriginalNativeClickAction() => Run((button, _) =>
    {
        AutomationProperties.SetName(button, "关闭笔记预览");
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        button.Focus();
        Key(button, Keyboard.KeyDownEvent, System.Windows.Input.Key.Space);
        Key(button, Keyboard.KeyUpEvent, System.Windows.Input.Key.Space);
        Assert.Equal(1, clicks);
        var peer = Assert.IsType<ButtonAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(button));
        Assert.Equal("关闭笔记预览", peer.GetName());
        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
        Assert.Equal(2, clicks);
    });

    [Fact]
    public void RoamingPreviewUsesTheSharedIconAndItsExistingClickHandler() => MarkdownPreviewTests.OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pane-close-" + Guid.NewGuid().ToString("N"));
        var vault = new VaultOptions(System.IO.Path.Combine(root, "vault"), "words", false);
        Directory.CreateDirectory(vault.RootPath);
        Directory.CreateDirectory(VaultPaths.WordRoot(vault));
        var notePath = System.IO.Path.Combine(VaultPaths.WordRoot(vault), "apple.md");
        const string markdown = "# apple\n\n> n. 苹果";
        File.WriteAllText(notePath, markdown);
        using var page = new LearningPage(vault, System.IO.Path.Combine(root, "state"));
        var window = Window(page);
        try
        {
            window.Show(); Await(page.ReloadAsync()); Await(page.ShowRoamingAsync());
            page.OpenRoamingPreview(); Pump();
            var close = Assert.IsType<PaneCloseButton>(page.FindName("CloseRoamingPreviewButton"));
            Assert.Equal("关闭笔记预览", AutomationProperties.GetName(close));
            Assert.IsType<System.Windows.Shapes.Path>(close.Content);
            Assert.True(page.IsRoamingPreviewOpen);
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(page.IsRoamingPreviewOpen);
            Assert.Equal(markdown, File.ReadAllText(notePath));
        }
        finally
        {
            page.Dispose(); window.Close();
            var temporary = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!System.IO.Path.GetFullPath(root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected temporary fixture path.");
            Directory.Delete(root, true);
        }
    });

    private static void Run(Action<PaneCloseButton, Window> action) => MarkdownPreviewTests.OnSta(() =>
    {
        var button = new PaneCloseButton { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var host = new Grid { Focusable = true };
        host.Children.Add(button);
        var window = Window(host);
        try { window.Show(); window.Activate(); host.Focus(); Pump(); action(button, window); }
        finally { if (Mouse.Captured is not null) Mouse.Capture(null); window.Close(); }
    });
    private static Window Window(UIElement content)
    {
        var window = new Window { Style = null, Icon = null, Content = content, Width = 920, Height = 700, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        return window;
    }
    private static void SetMouseOver(UIElement element, bool value)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        element.SetValue(key, value);
    }
    private static void Key(Button button, RoutedEvent routedEvent, System.Windows.Input.Key key) => button.RaiseEvent(
        new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, Environment.TickCount, key) { RoutedEvent = routedEvent });
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Pump() => MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(35));
    private static void Await(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump();
        Assert.True(task.IsCompleted); task.GetAwaiter().GetResult();
    }
}

[CollectionDefinition("Pane close WPF input", DisableParallelization = true)]
public sealed class PaneCloseInputCollection;
