using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class ReadingPositionSyncTests
{
    [Fact]
    public void UnknownCrossDeviceAnchorRestoresNormalizedProgressAtAnotherViewportSize() => OnUi(() =>
    {
        var reader = new RichTextBox { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        reader.Document.Blocks.Clear();
        foreach (var index in Enumerable.Range(0, 80)) reader.Document.Blocks.Add(new Paragraph(new Run($"Paragraph {index}. " + new string('a', 180))));
        var window = new Window { Content = reader, Width = 700, Height = 500, ShowInTaskbar = false };
        try
        {
            window.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
            ReadingPositionTools.Restore(reader, .6, null);
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(40));
            Assert.InRange(ReadingPositionTools.Capture(reader).Progress, .59, .61);
            window.Width = 380; window.Height = 350;
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
            ReadingPositionTools.Restore(reader, .6, "an-anchor-from-another-platform");
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(40));
            Assert.InRange(ReadingPositionTools.Capture(reader).Progress, .59, .61);
            Assert.Equal(80, reader.Document.Blocks.Count);
        }
        finally { window.Close(); }
    });

    private static void OnUi(Action action) => ((Lazy<Dispatcher>)typeof(ReadingPageTests)
        .GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(action);
}
