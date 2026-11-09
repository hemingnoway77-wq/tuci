using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Core.Library;
using VocabCollector.Desktop.Learning;

namespace VocabCollector.Tests;

public sealed class NotebookDeleteTests
{
    [Fact]
    public void VisibleDeleteActionFollowsTheCurrentNoteAndCancelKeepsTheFile() => Run((shell, fixture, catalog, notes, owner) =>
    {
        var button = DeleteButton(shell);
        Assert.True(button.IsVisible); Assert.False(button.IsEnabled);
        shell.Documents.OpenNote(notes[0]); shell.UpdateLayout();
        Assert.True(button.IsEnabled);
        Assert.Equal("删除当前笔记", AutomationProperties.GetName(button));
        Dialog(owner, "删除笔记", () => button.RaiseEvent(new(Button.ClickEvent)), popup =>
        {
            Assert.Contains(Find<TextBlock>(popup), text => text.Text.Contains(notes[0].RelativePath));
            Find<Button>(popup).Single(item => Equals(item.Content, "取消")).RaiseEvent(new(Button.ClickEvent));
        });
        Assert.True(File.Exists(notes[0].AbsolutePath));
        Assert.Empty(Await(catalog.GetTrashAsync())); Assert.Equal(notes[0].Id, shell.Documents.Note?.Id);
        shell.Documents.NewTab(); Assert.False(button.IsEnabled);
    });

    [Fact]
    public void DeleteSavesDraftClosesAllMatchingTabsAndHistoriesAndCanRestoreFromTheVisibleTrash() => Run((shell, fixture, catalog, notes, owner) =>
    {
        var workspace = shell.Documents;
        workspace.OpenNote(notes[0]); workspace.OpenNote(notes[1]);
        Assert.True(workspace.ActiveReader.CanGoBack);
        workspace.ToggleEditing();
        var otherDraft = "# banana\n\nAnother tab's newly saved content must stay visible.";
        Find<TextBox>(workspace).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible).Text = otherDraft;
        workspace.ToggleEditing();
        workspace.OpenNote(notes[0], true); Await(workspace.CloseActiveTabAsync());
        workspace.OpenNote(notes[0], true); workspace.OpenNote(notes[0], true); workspace.ToggleEditing();
        var draft = "# apple\n\nA draft that must be preserved in the trash.";
        Find<TextBox>(workspace).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible).Text = draft;
        ConfirmDelete(shell, owner);
        Until(() => !File.Exists(notes[0].AbsolutePath) && workspace.IsEnabled);
        Assert.Equal(notes[1].Id, workspace.Note?.Id);
        Assert.Equal(otherDraft, workspace.Note?.Markdown);
        Assert.Equal(otherDraft, workspace.ActiveReader.Note?.Markdown);
        Assert.False(workspace.ActiveReader.CanGoBack); workspace.GoBack(); Assert.Equal(notes[1].Id, workspace.Note?.Id);
        workspace.ReopenClosedTab(); Assert.Equal(notes[1].Id, workspace.Note?.Id);
        workspace.NextTab(1); Assert.Equal(notes[1].Id, workspace.Note?.Id);
        Assert.DoesNotContain(shell.Tree, entry => entry.Note?.Id == notes[0].Id);
        var item = Assert.Single(Await(catalog.GetTrashAsync()));
        Assert.Equal(draft, File.ReadAllText(Path.Combine(catalog.LocalStateDirectory, "trash", item.Id, "note.md")));
        Dialog(owner, "回收站 · 选择笔记恢复", () => ((Button)shell.FindName("NotebookTrashButton")).RaiseEvent(new(Button.ClickEvent)), popup =>
        {
            popup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(popup), 0, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        });
        Until(() => File.Exists(notes[0].AbsolutePath) && workspace.Note?.Id == notes[0].Id && workspace.IsEnabled);
        Assert.Equal(draft, File.ReadAllText(notes[0].AbsolutePath));
        Assert.Equal(notes[0].Id, Await(catalog.ScanAsync()).Notes.Single(note => note.RelativePath == notes[0].RelativePath).Id);
        Assert.Empty(Await(catalog.GetTrashAsync()));
    });

    [Fact]
    public void ConflictingDirtyDraftStopsDeletionAndRetainsBothVersions() => Run((shell, fixture, catalog, notes, owner) =>
    {
        shell.Documents.OpenNote(notes[0]); shell.Documents.ToggleEditing();
        var editor = Find<TextBox>(shell.Documents).Single(box => box.Name == "WorkspaceEditor" && box.IsVisible);
        editor.Text = "My unsaved local draft.";
        File.WriteAllText(notes[0].AbsolutePath, "A different external edit.");
        ConfirmDelete(shell, owner);
        Until(() => shell.Documents.IsEnabled);
        Assert.True(shell.Documents.HasUnsavedChanges); Assert.Equal("My unsaved local draft.", editor.Text);
        Assert.Equal("A different external edit.", File.ReadAllText(notes[0].AbsolutePath));
        Assert.Empty(Await(catalog.GetTrashAsync())); Assert.Equal(notes[0].Id, shell.Documents.Note?.Id);
    });

    [Fact]
    public void PendingDeletionLocksEditingAndKeepsAnotherNoteSelectedWhileTheTargetIsRemoved() => Run((shell, fixture, catalog, notes, owner) =>
    {
        shell.Documents.OpenNote(notes[0]);
        using (var held = new FileStream(Path.Combine(fixture.WordRoot, ".vocabcollector.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            ConfirmDelete(shell, owner);
            Until(() => !shell.Documents.IsEnabled);
            Assert.True(File.Exists(notes[0].AbsolutePath));
            // The file tree can select a different note while the disk lock is held.
            shell.Documents.OpenNote(notes[1], true);
        }
        Until(() => !File.Exists(notes[0].AbsolutePath) && shell.Documents.IsEnabled);
        Assert.Equal(notes[1].Id, shell.Documents.Note?.Id);
        Assert.Equal(notes[1].Markdown, File.ReadAllText(notes[1].AbsolutePath));
        Assert.Single(Await(catalog.GetTrashAsync()));
    });

    private static Button DeleteButton(LibraryShell shell) => Find<Button>(shell).Single(button => button.Name == "DeleteCurrentNoteButton");
    private static void ConfirmDelete(LibraryShell shell, Window owner) => Dialog(owner, "删除笔记", () => DeleteButton(shell).RaiseEvent(new(Button.ClickEvent)),
        popup => Find<Button>(popup).Single(button => button.Name == "NotebookConfirmButton").RaiseEvent(new(Button.ClickEvent)));
    private static void Dialog(Window owner, string title, Action trigger, Action<Window> respond)
    {
        Exception? failure = null; var handled = false; var deadline = DateTime.UtcNow.AddSeconds(8);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var popup = owner.OwnedWindows.Cast<Window>().FirstOrDefault(window => window.Title == title && window.IsLoaded);
            if (popup is null && DateTime.UtcNow < deadline) return;
            timer.Stop(); handled = true;
            try { Assert.NotNull(popup); respond(popup); }
            catch (Exception error) { failure = error; foreach (Window window in owner.OwnedWindows) window.Close(); }
        };
        try { timer.Start(); trigger(); Until(() => handled); }
        finally { timer.Stop(); }
        Assert.True(handled, "Expected the actual notebook dialog to open.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Run(Action<LibraryShell, LibraryFixture, LibraryCatalog, IReadOnlyList<LibraryNote>, Window> action) => MarkdownPreviewTests.OnSta(() =>
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var fixture = new LibraryFixture(); fixture.Write("apple.md", "# apple\n\nOriginal."); fixture.Write("banana.md", "# banana\n\nSecond note.");
        var catalog = fixture.Catalog; var notes = Await(catalog.ScanAsync()).Notes.OrderBy(note => note.RelativePath).ToArray();
        using var shell = new LibraryShell(); shell.Configure(catalog, notes, fixture.WordRoot);
        var owner = new Window { Content = shell, Width = 1100, Height = 650, ShowInTaskbar = false };
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try { owner.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(40)); action(shell, fixture, catalog, notes, owner); }
        finally { owner.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
    });
    private static T Await<T>(Task<T> task) { Until(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < deadline) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(predicate(), "Notebook deletion operation timed out.");
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
}
