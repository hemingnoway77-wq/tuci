using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Core.Library;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Tests;

public sealed class NoteWorkspaceTests
{
    [Fact]
    public void SourceHeadingNavigationUsesRenderedHeadingOffsetForQuotedHeading() => Run((workspace, fixture, notes) =>
    {
        const string markdown = "# First\n\n<div>\n# Hidden\n</div>\n\n**Ordinary bold**\n\n> ## Quoted **heading**\n\n### Last\n";
        workspace.OpenNote(notes[0] with { Markdown = markdown });
        workspace.ToggleEditing();
        var editor = Descendants<TextBox>(workspace).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible);
        workspace.NavigateHeading(1);
        Assert.Equal(markdown.IndexOf("## Quoted", StringComparison.Ordinal), editor.SelectionStart);
        Assert.StartsWith("## Quoted", editor.SelectedText);
    });
    [Fact]
    public void EmptyTabsNotifyActiveChangeAndOfferOpenAndCreateActions() => Run((workspace, fixture, notes) =>
    {
        workspace.OpenNote(notes[0]);
        var activeChanges = 0; var opens = 0; var creates = 0;
        workspace.ActiveTabChanged += (_, _) => activeChanges++;
        workspace.QuickOpenRequested += (_, _) => opens++;
        workspace.NewNoteRequested += (_, _) => creates++;
        workspace.NewTab();
        workspace.UpdateLayout();
        Assert.Equal(1, activeChanges);
        Assert.Null(workspace.Note);
        Assert.Contains(Descendants<TextBlock>(workspace), block => block.Text == "未打开笔记");
        Descendants<Button>(workspace).Single(button => Equals(button.Content, "打开笔记")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Descendants<Button>(workspace).Single(button => Equals(button.Content, "新建笔记")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, opens); Assert.Equal(1, creates);
    });
    [Fact]
    public void TabsKeepIndependentReadersOffsetsAndCanCloseAndReopen() => Run((workspace, fixture, notes) =>
    {
        workspace.OpenNote(notes[0]);
        var first = workspace.ActiveReader;
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(70));
        Descendants<MarkdownPreview>(first).Single().ScrollToVerticalOffset(220);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
        workspace.OpenNote(notes[1], newTab: true);
        Assert.NotSame(first, workspace.ActiveReader);
        workspace.NextTab(-1);
        Assert.Same(first, workspace.ActiveReader);
        Assert.InRange(first.VerticalOffset, 218, 222);
        workspace.NextTab(1);
        Await(workspace.CloseActiveTabAsync());
        Assert.Equal(notes[0].Id, workspace.Note?.Id);
        workspace.ReopenClosedTab();
        Assert.Equal(notes[1].Id, workspace.Note?.Id);
        workspace.NewTab();
        Assert.NotNull(workspace.ActiveReader);
        Assert.Null(workspace.Note);
    });

    [Fact]
    public void EditingSavesMarkdownAndExternalConflictKeepsDraftAndPreventsClosing() => Run((workspace, fixture, notes) =>
    {
        workspace.OpenNote(notes[0]);
        workspace.ToggleEditing();
        var editor = Descendants<TextBox>(workspace).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible);
        editor.Text = "# apple\n\nMy draft.";
        Assert.True(workspace.HasUnsavedChanges);
        Assert.True(Await(workspace.SaveAllAsync()));
        Assert.Equal(editor.Text, File.ReadAllText(notes[0].AbsolutePath));
        editor.Text = "# apple\n\nMust not lose this conflict draft.";
        File.WriteAllText(notes[0].AbsolutePath, "# apple\n\nExternal edit.");
        Assert.False(Await(workspace.SaveAllAsync()));
        Await(workspace.CloseActiveTabAsync());
        Assert.True(workspace.HasUnsavedChanges);
        Assert.Equal("# apple\n\nMust not lose this conflict draft.", editor.Text);
        Assert.Equal("# apple\n\nExternal edit.", File.ReadAllText(notes[0].AbsolutePath));
        var copy = Descendants<Button>(workspace).Single(button => Equals(button.Content, "另存副本") && button.IsVisible);
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !workspace.HasUnsavedChanges);
        Assert.NotEqual(notes[0].AbsolutePath, workspace.Note?.AbsolutePath);
        Assert.Equal(editor.Text, File.ReadAllText(workspace.Note!.AbsolutePath));
    });

    [Fact]
    public void RefreshDuringEditingPreservesDraftAndReadOnlyTabsFollowChangedFiles() => Run((workspace, fixture, notes) =>
    {
        workspace.OpenNote(notes[0]);
        workspace.ToggleEditing();
        var editor = Descendants<TextBox>(workspace).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible);
        editor.Text = "unsaved local text";
        var external = notes[0] with { Markdown = "external text" };
        workspace.RefreshNotes([external, notes[1]]);
        Assert.Equal("unsaved local text", editor.Text);
        Assert.True(workspace.HasUnsavedChanges);
        workspace.OpenNote(notes[1]);
        Assert.Equal(notes[1].Id, workspace.Note?.Id);
        workspace.NextTab(-1);
        Assert.Equal("unsaved local text", editor.Text);
    });

    [Fact]
    public void FollowingLinkFromUnsavedReadingPreviewKeepsDraftInOriginalTab() => Run((workspace, fixture, notes) =>
    {
        workspace.OpenNote(notes[0]);
        workspace.ToggleEditing();
        var editor = Descendants<TextBox>(workspace).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible);
        editor.Text = "# apple\n\nUnsaved [[banana]] draft.";
        workspace.ToggleEditing();
        workspace.HandleLink(new PreviewLinkEventArgs("banana", true), notes[0]);
        Assert.Equal(notes[1].Id, workspace.Note?.Id);
        Assert.True(workspace.HasUnsavedChanges);
        workspace.NextTab(-1);
        workspace.ToggleEditing();
        Assert.Equal("# apple\n\nUnsaved [[banana]] draft.", editor.Text);
    });

    [Fact]
    public void IdleDraftAutosavesAndSessionRestoresIndependentTabPosition() => Run((workspace, fixture, notes) =>
    {
        workspace.OpenNote(notes[0]); workspace.ToggleEditing();
        var editor = Descendants<TextBox>(workspace).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible);
        editor.Text += "\n\nAutomatically saved manual content.";
        PumpUntil(() => !workspace.HasUnsavedChanges);
        Assert.EndsWith("Automatically saved manual content.", File.ReadAllText(notes[0].AbsolutePath));
        workspace.ToggleEditing();
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        Descendants<MarkdownPreview>(workspace.ActiveReader).Single().ScrollToVerticalOffset(240);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        workspace.OpenNote(notes[1], true);
        Assert.True(Await(workspace.SaveAllAsync()));
        workspace.Dispose();
        using var restored = new NoteWorkspace();
        restored.Configure(fixture.Catalog, Await(fixture.Catalog.ScanAsync()).Notes);
        Window.GetWindow(workspace).Content = restored;
        Assert.Equal(notes[1].Id, restored.Note?.Id);
        restored.NextTab(-1);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(80));
        Assert.Equal(notes[0].Id, restored.Note?.Id);
        Assert.InRange(restored.ActiveReader.VerticalOffset, 238, 242);
    });

    [Fact]
    public void CaptureSameSourcePreloadUsesCallerOffsetAndDoesNotReplaceDirtyDraft() => Run((workspace, fixture, notes) =>
    {
        workspace.OpenNote(notes[0]);
        var capture = new MarkdownPreview { Markdown = notes[0].Markdown };
        var popupOwner = new Window { Content = capture, Width = 600, Height = 400, ShowInTaskbar = false };
        try
        {
            popupOwner.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
            capture.ScrollToVerticalOffset(280); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
            workspace.HandleLink(new PreviewLinkEventArgs("banana", true), notes[0], capture);
            workspace.GoBack(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
            Assert.InRange(workspace.ActiveReader.VerticalOffset, 278, 282);
            workspace.ToggleEditing();
            var editor = Descendants<TextBox>(workspace).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible);
            editor.Text = "keep my local draft";
            workspace.HandleLink(new PreviewLinkEventArgs("banana", true), notes[0], capture);
            Assert.True(workspace.HasUnsavedChanges);
            workspace.NextTab(-1);
            Assert.Equal("keep my local draft", editor.Text);
        }
        finally { popupOwner.Close(); capture.Dispose(); }
    });

    private static void Run(Action<NoteWorkspace, LibraryFixture, IReadOnlyList<LibraryNote>> action) => MarkdownPreviewTests.OnSta(() =>
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var fixture = new LibraryFixture();
        fixture.Write("apple.md", "# apple\n\n" + string.Join("\n\n", Enumerable.Repeat("Long selectable reading text.", 100)));
        fixture.Write("banana.md", "# banana\n\nSecond note.");
        var notes = Await(fixture.Catalog.ScanAsync()).Notes;
        using var workspace = new NoteWorkspace();
        workspace.Configure(fixture.Catalog, notes);
        var owner = new Window { Width = 720, Height = 500, Content = workspace, ShowInTaskbar = false };
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try { owner.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50)); action(workspace, fixture, notes); }
        finally { owner.Close(); SynchronizationContext.SetSynchronizationContext(previousContext); }
    });

    private static T Await<T>(Task<T> task) { PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition)
    {
        var stop = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < stop) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(15));
        Assert.True(condition(), "Workspace operation timed out.");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
}
