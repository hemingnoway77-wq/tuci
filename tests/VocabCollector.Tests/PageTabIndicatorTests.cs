using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VocabCollector.Desktop.Controls;

namespace VocabCollector.Tests;

[CollectionDefinition("Navigation motion", DisableParallelization = true)]
public sealed class NavigationMotionCollection;

[Collection("Navigation motion")]
public sealed class PageTabIndicatorTests
{
    [Fact]
    public void LeadingEdgeStretchesBeforeTheTrailingEdgeCatchesUp() => WithIndicator(indicator =>
    {
        indicator.MoveTo(0, 56, false);
        indicator.MoveTo(180, 78);
        if (!SystemParameters.ClientAreaAnimation) return;
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(75));
        Assert.True(indicator.RightEdge - indicator.LeftEdge > 90,
            "The leading edge should stretch the line toward the new label.");
        // Re-aligning an unchanged selection must not extend its animation.
        indicator.MoveTo(180, 78);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(275));
        Assert.Equal(180, indicator.LeftEdge, 2);
        Assert.Equal(258, indicator.RightEdge, 2);
        Assert.False(indicator.HasAnimatedProperties);
    });

    [Fact]
    public void SelectionSlidesAndRapidReversalSettlesAtTheNewestTab() => WithIndicator(indicator =>
    {
        indicator.MoveTo(0, 56, false);
        indicator.MoveTo(180, 78);
        if (SystemParameters.ClientAreaAnimation)
        {
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(55));
            Assert.InRange(indicator.RightEdge, 56.1, 258);
            var left = indicator.LeftEdge; var right = indicator.RightEdge;
            indicator.MoveTo(84, 56);
            Assert.InRange(Math.Abs(left - indicator.LeftEdge), 0, .01);
            Assert.InRange(Math.Abs(right - indicator.RightEdge), 0, .01);
        }
        else indicator.MoveTo(84, 56);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(310));
        Assert.Equal(84, indicator.LeftEdge, 2); Assert.Equal(140, indicator.RightEdge, 2);
        Assert.False(indicator.HasAnimatedProperties);
    });

    [Fact]
    public void ReducedMotionAndResizeAlignmentLeaveNoOldClock() => WithIndicator(indicator =>
    {
        indicator.MoveTo(0, 56, false); indicator.MoveTo(180, 78);
        indicator.AnimateTransitions = false;
        Assert.Equal(180, indicator.LeftEdge, 2); Assert.Equal(258, indicator.RightEdge, 2);
        indicator.MoveTo(18, 48, false);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(310));
        Assert.Equal(18, indicator.LeftEdge, 2); Assert.Equal(66, indicator.RightEdge, 2);
        Assert.False(indicator.HasAnimatedProperties);
        Assert.False(indicator.IsHitTestVisible);
    });

    private static void WithIndicator(Action<PageTabIndicator> test) => MarkdownPreviewTests.OnSta(() =>
    {
        using var indicator = new PageTabIndicator { Width = 380, Height = 1 };
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); icon.Freeze();
        var owner = new Window { Style = null, Icon = icon, Content = indicator, Width = 420, Height = 140, ShowInTaskbar = false };
        try { owner.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(40)); test(indicator); }
        finally { owner.Close(); }
    });
}
