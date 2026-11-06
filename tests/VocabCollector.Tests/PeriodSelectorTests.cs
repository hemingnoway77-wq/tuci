using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

[Collection("Period selector WPF input")]
public sealed class PeriodSelectorTests
{
    [Fact]
    public void FocusUsesTextEmphasisWithoutAddingAFrameAroundTheLabel() => Run((selector, _) =>
    {
        selector.AnimateTransitions = false;
        selector.MonthButton.Focus();
        Assert.True(selector.MonthButton.IsKeyboardFocused);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(selector.MonthButton.BorderBrush).Color);
        Assert.True(selector.MonthButton.FontWeight >= FontWeights.Medium);
        Key(selector.MonthButton, Keyboard.KeyDownEvent, System.Windows.Input.Key.Space);
        Key(selector.MonthButton, Keyboard.KeyUpEvent, System.Windows.Input.Key.Space);
        Assert.True(selector.IsMonthly);
        AssertAligned(selector, selector.MonthButton);
    });

    [Fact]
    public void ProgrammaticSelectionKeepsNativeButtonsAndNotifiesOnlyActualChanges() => Run((selector, _) =>
    {
        var changes = 0;
        selector.SelectionChanged += (_, _) => changes++;
        Assert.True(selector.WeekButton.IsChecked);
        Assert.False(selector.MonthButton.IsChecked);
        selector.IsMonthly = true;
        Assert.True(selector.MonthButton.IsChecked);
        Assert.False(selector.WeekButton.IsChecked);
        selector.IsMonthly = true;
        selector.WeekButton.IsChecked = true;
        Assert.False(selector.IsMonthly);
        Assert.False(selector.MonthButton.IsChecked);
        Assert.Equal(2, changes);
    });

    [Fact]
    public void UnderlineExtendsAcrossLabelsBeforeItsTailCatchesUp() => Run((selector, _) =>
    {
        var line = Underline(selector);
        AssertAligned(selector, selector.WeekButton);
        var height = selector.ActualHeight;
        selector.IsMonthly = true;
        if (SystemParameters.ClientAreaAnimation)
        {
            Pump(95);
            Assert.True(line.ActualWidth > 35, $"The moving line never stretched: {line.ActualWidth}.");
            Assert.True(selector.HasAnimatedProperties);
        }
        Pump(300);
        AssertAligned(selector, selector.MonthButton);
        Assert.Equal(height, selector.ActualHeight);
        Assert.False(selector.HasAnimatedProperties);
    });

    [Fact]
    public void RapidReversalContinuesFromTheVisibleEdgesAndFinishesAtTheLatestSelection() => Run((selector, _) =>
    {
        selector.IsMonthly = true;
        Pump(60);
        var line = Underline(selector);
        var left = Canvas.GetLeft(line);
        var right = left + line.ActualWidth;
        selector.IsMonthly = false;
        Assert.InRange(Math.Abs(Canvas.GetLeft(line) - left), 0, 1.5);
        Assert.InRange(Math.Abs(Canvas.GetLeft(line) + line.Width - right), 0, 1.5);
        Pump(320);
        AssertAligned(selector, selector.WeekButton);
        Assert.False(selector.HasAnimatedProperties);
        Assert.False(selector.IsMonthly);
    });

    [Fact]
    public void ReducedMotionStillSupportsKeyboardAndSelectionAutomation() => Run((selector, _) =>
    {
        selector.AnimateTransitions = false;
        var changes = 0;
        selector.SelectionChanged += (_, _) => changes++;
        selector.MonthButton.Focus();
        Key(selector.MonthButton, Keyboard.KeyDownEvent, System.Windows.Input.Key.Space);
        Key(selector.MonthButton, Keyboard.KeyUpEvent, System.Windows.Input.Key.Space);
        Assert.True(selector.IsMonthly);
        AssertAligned(selector, selector.MonthButton);
        Assert.False(selector.HasAnimatedProperties);
        Assert.Equal("本周", AutomationProperties.GetName(selector.WeekButton));
        Assert.Equal("本月", AutomationProperties.GetName(selector.MonthButton));
        var peer = Assert.IsType<RadioButtonAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(selector.WeekButton));
        var selection = Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetPattern(PatternInterface.SelectionItem));
        selection.Select();
        Assert.True(selection.IsSelected);
        Assert.False(selector.IsMonthly);
        AssertAligned(selector, selector.WeekButton);
        Assert.Equal(2, changes);
    });

    [Fact]
    public void HidingStopsTheTransitionAndShowingAlignsTheCurrentSelection() => Run((selector, _) =>
    {
        selector.IsMonthly = true;
        Pump(35);
        selector.Visibility = Visibility.Collapsed;
        Assert.False(selector.HasAnimatedProperties);
        selector.IsMonthly = false;
        selector.IsMonthly = true;
        selector.Visibility = Visibility.Visible;
        Pump(40);
        AssertAligned(selector, selector.MonthButton);
        Assert.False(selector.HasAnimatedProperties);
    });

    [Fact]
    public void InitialMonthlySelectionAndResizeUseTheSelectedLabelGeometry() => Run((selector, _) =>
    {
        selector.AnimateTransitions = false;
        selector.IsMonthly = true;
        selector.Width = 140;
        Pump(40);
        AssertAligned(selector, selector.MonthButton);
        var line = Underline(selector);
        var label = Assert.IsType<TextBlock>(selector.MonthButton.Content);
        var bottom = label.TranslatePoint(new Point(0, label.ActualHeight), selector).Y;
        Assert.InRange(Canvas.GetTop(line) - bottom, 7.5, 9);
        Assert.InRange(line.Height, .75, 1.1);
        Assert.InRange(line.Opacity, .45, .65);
    }, initiallyMonthly: true);

    [Fact]
    public void DisposingDuringATransitionRemovesClocksAndFurtherNotifications() => Run((selector, _) =>
    {
        selector.IsMonthly = true;
        Pump(35);
        var changes = 0;
        selector.SelectionChanged += (_, _) => changes++;
        selector.Dispose();
        Assert.False(selector.HasAnimatedProperties);
        var left = Canvas.GetLeft(Underline(selector));
        selector.IsMonthly = false;
        Pump(300);
        Assert.False(selector.HasAnimatedProperties);
        Assert.Equal(left, Canvas.GetLeft(Underline(selector)));
        Assert.Equal(0, changes);
    });

    private static void Run(Action<PeriodSelector, Window> action, bool initiallyMonthly = false) => MarkdownPreviewTests.OnSta(() =>
    {
        using var selector = new PeriodSelector { IsMonthly = initiallyMonthly, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Style = null, Icon = null, Content = selector, Width = 300, Height = 180, ShowInTaskbar = false };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try { window.Show(); window.Activate(); Pump(40); action(selector, window); }
        finally { selector.Dispose(); window.Close(); }
    });

    private static Border Underline(PeriodSelector selector) => Assert.IsType<Border>(selector.FindName("PeriodUnderline"));
    private static void AssertAligned(PeriodSelector selector, RadioButton selected)
    {
        selector.UpdateLayout();
        var line = Underline(selector);
        Assert.InRange(line.ActualWidth, 20, 22);
        var label = Assert.IsType<TextBlock>(selected.Content);
        var center = label.TranslatePoint(new Point(label.ActualWidth / 2, 0), selector).X;
        Assert.InRange(Math.Abs(Canvas.GetLeft(line) + line.ActualWidth / 2 - center), 0, .5);
    }
    private static void Key(RadioButton button, RoutedEvent routedEvent, System.Windows.Input.Key key) => button.RaiseEvent(
        new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!, Environment.TickCount, key) { RoutedEvent = routedEvent });
    private static void Pump(int milliseconds) => MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(milliseconds));
}

[CollectionDefinition("Period selector WPF input", DisableParallelization = true)]
public sealed class PeriodSelectorInputCollection;
