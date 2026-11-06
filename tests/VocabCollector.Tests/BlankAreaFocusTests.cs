using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

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
        var owner = new Window { Content = layout, Width = 350, Height = 230, ShowInTaskbar = false };
        try
        {
            owner.Show(); owner.Activate(); input.Focus();
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30));
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
