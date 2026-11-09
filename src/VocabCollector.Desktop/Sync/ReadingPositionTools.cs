using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace VocabCollector.Desktop;

public sealed record ReadingPositionValue(double Progress, string Anchor);
public sealed record NoteReadingPosition(string NoteId, double Progress, string Anchor, string Title);
public sealed record ArticleReadingPosition(string Url, double Progress, string Anchor, string Title);

public static class ReadingPositionTools
{
    public static ReadingPositionValue Capture(RichTextBox reader)
    {
        var range = Math.Max(0, reader.ExtentHeight - reader.ViewportHeight);
        var progress = range > 0 ? Math.Clamp(reader.VerticalOffset / range, 0, 1) : 0;
        if (!double.IsFinite(progress)) progress = 0;
        var top = reader.GetPositionFromPoint(new Point(Math.Max(2, reader.Padding.Left + 2), Math.Max(2, reader.Padding.Top + 2)), true)?.Paragraph;
        var paragraphs = Paragraphs(reader.Document.Blocks).ToArray();
        var index = top is null ? -1 : Array.IndexOf(paragraphs, top);
        return new(progress, index < 0 ? "" : "p:" + index.ToString(CultureInfo.InvariantCulture));
    }

    public static void Restore(RichTextBox reader, double progress, string? anchor)
    {
        if (!double.IsFinite(progress)) return;
        reader.UpdateLayout();
        var paragraph = anchor?.StartsWith("paragraph:", StringComparison.Ordinal) == true ? anchor[10..] :
            anchor?.StartsWith("p:", StringComparison.Ordinal) == true ? anchor[2..] : null;
        if (paragraph is not null &&
            int.TryParse(paragraph, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 &&
            Paragraphs(reader.Document.Blocks).ElementAtOrDefault(index) is { } block)
            block.BringIntoView();
        else reader.ScrollToVerticalOffset(Math.Clamp(progress, 0, 1) * Math.Max(0, reader.ExtentHeight - reader.ViewportHeight));
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
            switch (block)
            {
                case Paragraph paragraph: yield return paragraph; break;
                case Section section:
                    foreach (var child in Paragraphs(section.Blocks)) yield return child;
                    break;
                case System.Windows.Documents.List list:
                    foreach (var item in list.ListItems)
                        foreach (var child in Paragraphs(item.Blocks)) yield return child;
                    break;
            }
    }
}
