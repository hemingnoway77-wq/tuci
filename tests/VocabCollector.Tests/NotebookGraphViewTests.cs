using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VocabCollector.Core.Library;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

public sealed class NotebookGraphViewTests
{
    [Fact]
    public void GraphUsesResolvedVisibleLinksAndLocalModeIgnoresCodeAndAmbiguousNames() => MarkdownPreviewTests.OnSta(() =>
    {
        using var fixture = new LibraryFixture();
        fixture.Write("source.md", "[[real]] `[[code]]` [[same]]");
        fixture.Write("real.md", "target");
        fixture.Write("code.md", "code-only reference");
        fixture.Write("a/same.md", "first namesake");
        fixture.Write("b/same.md", "second namesake");
        var catalog = fixture.Catalog;
        var notes = catalog.ScanAsync().GetAwaiter().GetResult().Notes;
        using var graph = new NotebookGraphView();
        graph.Configure(catalog, notes);
        Wait(graph);
        Assert.Equal(5, graph.DisplayedNodeCount);
        Assert.Equal(1, graph.DisplayedEdgeCount);
        graph.FocusNote = notes.Single(note => note.Title == "source");
        graph.LocalMode = true;
        Wait(graph);
        Assert.Equal(2, graph.DisplayedNodeCount);
        Assert.Equal(1, graph.DisplayedEdgeCount);
        graph.LocalMode = false;
        graph.Filter = "file:real";
        Wait(graph);
        Assert.Equal(1, graph.DisplayedNodeCount);
        Assert.Equal(0, graph.DisplayedEdgeCount);
    });

    [Fact]
    public void LargeLibraryIsBoundedAndExplainsTruncation() => MarkdownPreviewTests.OnSta(() =>
    {
        using var fixture = new LibraryFixture();
        var notes = Enumerable.Range(0, 650).Select(index => new LibraryNote(index.ToString(), "note" + index + ".md", Path.Combine(fixture.WordRoot, "note" + index + ".md"), "plain body", DateTimeOffset.UnixEpoch)).ToArray();
        using var graph = new NotebookGraphView();
        graph.Configure(fixture.Catalog, notes);
        Wait(graph);
        Assert.Equal(300, graph.DisplayedNodeCount);
        Assert.Contains("650", graph.Status);
        Assert.Contains("300", graph.Status);
        Assert.Contains("筛选", graph.Status);
    });

    [Fact]
    public void SelectedNodeCanOpenAndDisposalCancelsQueuedLayout() => MarkdownPreviewTests.OnSta(() =>
    {
        using var fixture = new LibraryFixture();
        fixture.Write("word.md", "word body");
        var catalog = fixture.Catalog;
        var note = catalog.ScanAsync().GetAwaiter().GetResult().Notes.Single();
        var graph = new NotebookGraphView();
        graph.Resources.MergedDictionaries.Add(NotebookPalette.Create(dark: true));
        var window = new Window { Content = graph, Width = 800, Height = 600, ShowInTaskbar = false };
        try
        {
            window.Show();
            graph.Configure(catalog, [note]);
            Wait(graph);
            graph.FocusNote = note;
            LibraryNote? opened = null;
            graph.NoteRequested += (_, selected) => opened = selected;
            var open = Descendants<Button>(graph).Single(button => button.Name == "GraphOpenNoteButton");
            Assert.True(open.IsEnabled);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(note.Id, opened?.Id);
            graph.Filter = "word";
            graph.Dispose();
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(100));
            Assert.False(graph.IsBusy);
            graph.Configure(catalog, [note]);
            Assert.False(graph.IsBusy);
        }
        finally { graph.Dispose(); window.Close(); }
    });

    private static void Wait(NotebookGraphView graph)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(20));
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Graph layout did not finish: " + graph.Status);
        } while (graph.IsBusy);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
