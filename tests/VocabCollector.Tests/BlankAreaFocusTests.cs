using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

[CollectionDefinition("Blank-area WPF input", DisableParallelization = true)]
public sealed class BlankAreaFocusInputCollection;

[Collection("Blank-area WPF input")]
public sealed class BlankAreaFocusTests
{
    [Fact]
    public void BlankHeaderClickDismissesCaretButInputAndActionClicksKeepNormalFocus() => MarkdownPreviewTests.OnSta(() =>
    {
        var layout = new Grid { Focusable = true, Background = System.Windows.Media.Brushes.Transparent };
        var input = new TextBox { Text = "query", Width = 120, Height = 30, VerticalAlignment = VerticalAlignment.Bottom };
        var header = new Border { Height = 50, VerticalAlignment = VerticalAlignment.Top, Background = System.Windows.Media.Brushes.Transparent };
        var actionText = new TextBlock { Text = "action" };
        var action = new Button { Content = actionText, Width = 70, Height = 30 };
        layout.Children.Add(header); layout.Children.Add(input); layout.Children.Add(action);
        BlankAreaFocus.SetIsEnabled(layout, true);
        // This independent STA must not use another test Application's cached icon decoder.
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        icon.Freeze();
        var owner = new Window { Icon = icon, Content = layout, Width = 350, Height = 230, ShowInTaskbar = false };
        try
        {
            // Parallel test windows can steal foreground focus while Loaded is pumped.
            owner.Show();
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30));
            owner.Activate(); input.Focus();
            Assert.True(owner.IsActive && input.IsKeyboardFocused,
                $"Focus setup failed: windowActive={owner.IsActive}, inputFocused={input.IsKeyboardFocused}, keyboardTarget={Keyboard.FocusedElement?.GetType().Name ?? "none"}.");
            Click(input);
            Assert.True(input.IsKeyboardFocused);
            Click(actionText);
            Assert.True(input.IsKeyboardFocused); // Preview handler does not steal normal action processing.
            Click(header);
            Assert.False(input.IsKeyboardFocusWithin);
            Assert.True(layout.IsKeyboardFocused);
            Assert.Same(layout, FocusManager.GetFocusedElement(owner));
        }
        finally { owner.Close(); }
    });

    private static void Click(UIElement element) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,
        Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
}
