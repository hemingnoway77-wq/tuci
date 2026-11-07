using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Reading;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class ScrollingTests
{
    [Fact]
    public void WheelEasesAndAccumulatesWhileKeepingTheSameContent() => OnUi(() =>
    {
        using var fixture = new ScrollFixture();
        var viewer = fixture.Viewer;
        var content = viewer.Content;
        var wheel = Wheel(fixture.Content, -120);
        Assert.True(wheel.Handled, "The attached behavior must own the wheel route.");
        viewer.UpdateLayout();
        if (SystemParameters.ClientAreaAnimation) Assert.Equal(0, viewer.VerticalOffset);
        Pump(180);
        var oneNotch = viewer.VerticalOffset;
        Assert.True(oneNotch > 0);
        viewer.ScrollToTop(); viewer.UpdateLayout();
        for (var index = 0; index < 3; index++) Wheel(fixture.Content, -120);
        Pump(180);
        Assert.InRange(viewer.VerticalOffset, oneNotch * 3 - 1, oneNotch * 3 + 1);
        Assert.Same(content, viewer.Content);
    });

    [Fact]
    public void ReversingWheelStartsFromTheCurrentPositionAndDirectNavigationWins() => OnUi(() =>
    {
        using var fixture = new ScrollFixture();
        var viewer = fixture.Viewer;
        viewer.ScrollToVerticalOffset(400); viewer.UpdateLayout();
        Wheel(fixture.Content, -120); Pump(25);
        var reverseAt = viewer.VerticalOffset;
        Wheel(fixture.Content, 120); Pump(180);
        Assert.True(viewer.VerticalOffset < reverseAt, "A reversal must immediately head back rather than finish the stale downward target.");
        Wheel(fixture.Content, -120);
        viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        viewer.ScrollToVerticalOffset(700); viewer.UpdateLayout(); Pump(180);
        Assert.InRange(viewer.VerticalOffset, 699, 701);
        Wheel(fixture.Content, -120); Pump(20);
        viewer.ScrollToVerticalOffset(1050); viewer.UpdateLayout(); Pump(180);
        Assert.InRange(viewer.VerticalOffset, 1049, 1051);
    });

    [Fact]
    public void InnerViewerHandsWheelToItsOuterViewerOnlyAtTheBoundary() => OnUi(() =>
    {
        var innerContent = new Border { Height = 1400 };
        var inner = new ScrollViewer { Content = innerContent, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel(); stack.Children.Add(inner); stack.Children.Add(new Border { Height = 1800 });
        var outer = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SmoothScroll.SetIsEnabled(inner, true); SmoothScroll.SetIsEnabled(outer, true);
        using var owner = Show(outer, 350, 240);
        inner.ScrollToBottom(); inner.UpdateLayout();
        Assert.True(Wheel(innerContent, -120).Handled);
        Pump(180);
        Assert.True(outer.VerticalOffset > 0);
        Assert.InRange(inner.VerticalOffset, inner.ScrollableHeight - 1, inner.ScrollableHeight);
        outer.ScrollToBottom(); inner.ScrollToBottom(); outer.UpdateLayout(); inner.UpdateLayout();
        Wheel(innerContent, -120); Pump(180);
        Assert.InRange(outer.VerticalOffset, outer.ScrollableHeight - 1, outer.ScrollableHeight);
    });

    [Fact]
    public void KeyboardSelectionAndDisablingTheBehaviorCancelPendingWheelMotion() => OnUi(() =>
    {
        using var fixture = new ScrollFixture();
        var viewer = fixture.Viewer;
        Wheel(fixture.Content, -120); Pump(20);
        viewer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(viewer)!, Environment.TickCount, Key.End) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        viewer.ScrollToVerticalOffset(900); viewer.UpdateLayout(); Pump(180);
        Assert.InRange(viewer.VerticalOffset, 899, 901);
        Wheel(fixture.Content, -120); Pump(20);
        SmoothScroll.SetIsEnabled(viewer, false); viewer.UpdateLayout();
        var stopped = viewer.VerticalOffset;
        Pump(180);
        Assert.InRange(viewer.VerticalOffset, stopped - 1, stopped + 1);
        Assert.False(Wheel(fixture.Content, -120).Handled);
    });

    [Fact]
    public void ArticleViewportKeepsTheBarAtTheRightAndASeparateReadableTextColumn() => OnUi(() =>
    {
        var reader = new ArticleTextReader();
        reader.SetParagraphs(Enumerable.Repeat("An apple is worth reading about in this selectable article paragraph.", 80).ToArray());
        using var owner = Show(reader, 1340, 500);
        var viewer = Assert.IsType<ScrollViewer>(reader.Template.FindName("PART_ContentHost", reader));
        var bar = Descendants<ScrollBar>(reader).Single(item => item.Orientation == Orientation.Vertical);
        var bounds = bar.TransformToAncestor(reader).TransformBounds(new Rect(0, 0, bar.ActualWidth, bar.ActualHeight));
        Assert.True(bounds.Right >= reader.ActualWidth - 12, "The scrollbar belongs at the full viewport edge.");
        Assert.True(reader.Document.PagePadding.Right >= 24, $"Expected reading gutter; padding={reader.Document.PagePadding}, viewport={viewer.ViewportWidth}.");
        Assert.True(reader.Document.PagePadding.Left > 0);
        Assert.InRange(viewer.ViewportWidth - reader.Document.PagePadding.Left - reader.Document.PagePadding.Right, 930, 951);
        var document = reader.Document;
        var word = document.Blocks.OfType<Paragraph>().First().Inlines.OfType<Span>().First();
        reader.Selection.Select(word.ContentStart, word.ContentEnd);
        var selected = reader.Selection.Text;
        owner.Window.Width = 720; Pump(50);
        Assert.Same(document, reader.Document);
        Assert.Equal(selected, reader.Selection.Text);
        Assert.Equal(0, reader.Document.PagePadding.Left);
        Assert.True(reader.Document.PagePadding.Right >= 24);
    });

    [Fact]
    public void ArticleWordRunWheelUsesItsTemplateViewportWithoutChangingTheSelection() => OnUi(() =>
    {
        var reader = new ArticleTextReader();
        reader.SetParagraphs(Enumerable.Repeat("An apple remains selectable during wheel movement.", 80).ToArray());
        using var owner = Show(reader, 800, 500);
        var document = reader.Document;
        var span = document.Blocks.OfType<Paragraph>().First().Inlines.OfType<Span>().First();
        var run = span.Inlines.OfType<Run>().Single();
        reader.Selection.Select(run.ContentStart, run.ContentEnd);
        var selected = reader.Selection.Text;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        run.RaiseEvent(wheel);
        Assert.True(wheel.Handled, "RichTextBox content has a logical event route distinct from its template viewport.");
        Pump(180);
        Assert.True(reader.VerticalOffset > 0);
        Assert.Same(document, reader.Document);
        Assert.Equal(selected, reader.Selection.Text);
    });

    private static void OnUi(Action action) => ((Lazy<Dispatcher>)typeof(ReadingPageTests).GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(action);
    private static void Pump(int milliseconds) => MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(milliseconds));
    private static MouseWheelEventArgs Wheel(UIElement target, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        target.RaiseEvent(args); return args;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { var child = VisualTreeHelper.GetChild(parent, index); if (child is T item) yield return item; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static WindowOwner Show(UIElement content, double width, double height)
    {
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); icon.Freeze();
        var window = new Window { Style = null, Icon = icon, Content = content, Width = width, Height = height, ShowInTaskbar = false };
        window.Show(); Pump(40); return new WindowOwner(window);
    }
    private sealed class WindowOwner(Window window) : IDisposable
    { public Window Window => window; public void Dispose() { window.Close(); Pump(20); } }
    private sealed class ScrollFixture : IDisposable
    {
        public Border Content { get; } = new() { Height = 3000 };
        public ScrollViewer Viewer { get; }
        private readonly WindowOwner _owner;
        public ScrollFixture()
        {
            Viewer = new ScrollViewer { Content = Content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            SmoothScroll.SetIsEnabled(Viewer, true); _owner = Show(Viewer, 350, 240);
        }
        public void Dispose() => _owner.Dispose();
    }
}
