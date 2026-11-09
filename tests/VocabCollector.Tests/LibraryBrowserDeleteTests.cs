using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Core.Storage;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;
using VocabCollector.Tests.Fakes;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class LibraryBrowserDeleteTests
{
    [Fact]
    public void MainWindowLibraryTabUsesVisibleDeleteAndRecoveryAndRefreshesTheActualLearningPage() => OnUi(() =>
    {
        using var fixture = new LibraryFixture(); fixture.Write("apple.md", "# apple\n\n**释义**：\n\n> 苹果");
        var settings = new AppSettings(new("", "", ""), new(fixture.Root, "英语单词", false));
        var model = new MainViewModel(new CaptureService(new FakeWordAnalyzer(), new VaultStore(), TimeProvider.System), settings);
        var themes = new ThemeManager(Application.Current.Resources, new AppearanceSettingsStore(Path.Combine(fixture.Base, "appearance")));
        var main = new MainWindow(model, new WindowsSettingsStore(Path.Combine(fixture.Base, "config")), new WindowsNavigator(),
            themeManager: themes, learningStateDirectory: Path.Combine(fixture.Base, "learning"), prepareLocalData: () => Task.CompletedTask,
            settingsDirectory: Path.Combine(fixture.Base, "settings"), requireAccount: false);
        try
        {
            main.Show(); Await(main.PrepareForUseAsync());
            ((RadioButton)main.FindName("LibraryTab")).IsChecked = true;
            var host = (Grid)main.FindName("PageHost");
            Until(() => host.Children.OfType<LearningPage>().Any(page => page.AllNotes.Count == 1 && !page.IsBusy));
            var page = host.Children.OfType<LearningPage>().Single(); var browser = page.BrowserControl;
            Assert.Same(browser.Reader, page.ReaderControl);
            var delete = (Button)browser.FindName("BrowserDeleteNoteButton"); var trash = (Button)browser.FindName("BrowserTrashButton");
            Assert.True(delete.IsVisible); Assert.False(delete.IsEnabled); Assert.True(trash.IsVisible);
            var note = Assert.Single(page.AllNotes); page.OpenNote(note);
            Assert.True(delete.IsEnabled); Assert.Equal("删除当前笔记", AutomationProperties.GetName(delete));
            Await(page.ShowRoamingAsync()); Assert.True(page.HasExposure);
            var encounters = page.ActivityDays.Sum(day => day.Count);
            page.OpenRoamingPreview(); page.ShowLibrary();
            Respond(main, "删除笔记", () => Click(browser, "BrowserDeleteNoteButton"), popup =>
            {
                Assert.Equal(((SolidColorBrush)browser.FindResource("Sheet")).Color, ((SolidColorBrush)popup.Background).Color);
                Assert.Equal(((SolidColorBrush)browser.FindResource("Ink")).Color, ((SolidColorBrush)popup.Foreground).Color);
                Assert.NotNull(popup.FindResource("NotebookAction"));
                Assert.Contains(Find<TextBlock>(popup), text => text.Text.Contains(note.RelativePath));
                Find<Button>(popup).Single(button => button.Name == "NotebookConfirmButton").RaiseEvent(new(Button.ClickEvent));
            });
            Until(() => !File.Exists(note.AbsolutePath) && browser.IsEnabled && page.AllNotes.Count == 0);
            Assert.Empty(page.VisibleNotes); Assert.Null(page.SelectedNote); Assert.Null(browser.Reader.Note);
            Assert.False(page.HasExposure); Assert.False(page.IsRoamingPreviewOpen); Assert.False(delete.IsEnabled);
            Assert.Equal(encounters, page.ActivityDays.Sum(day => day.Count));
            using (var locked = new FileStream(Path.Combine(fixture.WordRoot, ".vocabcollector.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Respond(main, "回收站 · 选择笔记恢复", () => Click(browser, "BrowserTrashButton"), popup =>
                {
                    Assert.Equal(((SolidColorBrush)browser.FindResource("Sheet")).Color, ((SolidColorBrush)popup.Background).Color);
                    popup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(popup), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                });
                ((RadioButton)main.FindName("CaptureTab")).IsChecked = true;
            }
            Until(() => File.Exists(note.AbsolutePath) && browser.IsEnabled && page.AllNotes.Count == 1);
            Assert.True(((RadioButton)main.FindName("CaptureTab")).IsChecked);
            Assert.Equal(note.Id, Assert.Single(page.AllNotes).Id); Assert.Equal(note.Id, browser.Reader.Note?.Id);
            Assert.Equal(note.Markdown, File.ReadAllText(note.AbsolutePath)); Assert.True(delete.IsEnabled);
            Assert.Equal(encounters, page.ActivityDays.Sum(day => day.Count));
        }
        finally { main.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualBrowserCancelOrAnExternalChangeNeverDeletesUnseenContent(bool externalChange) => Run((browser, fixture, catalog, notes, owner) =>
    {
        var note = notes[0]; browser.OpenNote(note); var reloads = 0; browser.ReloadRequested += (_, _) => reloads++;
        Respond(owner, "删除笔记", () => Click(browser, "BrowserDeleteNoteButton"), popup =>
        {
            if (externalChange) File.WriteAllText(note.AbsolutePath, "external content written while confirmation was open");
            Find<Button>(popup).Single(button => externalChange ? button.Name == "NotebookConfirmButton" : Equals(button.Content, "取消")).RaiseEvent(new(Button.ClickEvent));
        });
        Until(() => browser.IsEnabled);
        Assert.Equal(externalChange ? "external content written while confirmation was open" : note.Markdown, File.ReadAllText(note.AbsolutePath));
        Assert.Empty(Await(catalog.GetTrashAsync())); Assert.Equal(2, browser.VisibleNotes.Count);
        if (externalChange) Assert.True(reloads > 0);
    });

    [Fact]
    public void PendingActualBrowserDeleteRemovesOnlyItsCapturedNoteAndPrunesBackAndForwardHistory() => Run((browser, fixture, catalog, notes, owner) =>
    {
        browser.OpenNote(notes[0]); browser.OpenNote(notes[1]); browser.OpenNote(notes[0]);
        using (var locked = new FileStream(Path.Combine(fixture.WordRoot, ".vocabcollector.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Respond(owner, "删除笔记", () => Click(browser, "BrowserDeleteNoteButton"), popup =>
                Find<Button>(popup).Single(button => button.Name == "NotebookConfirmButton").RaiseEvent(new(Button.ClickEvent)));
            Assert.False(browser.IsEnabled);
            browser.Reader.OpenNote(notes[1]);
        }
        Until(() => !File.Exists(notes[0].AbsolutePath) && browser.IsEnabled && browser.VisibleNotes.Count == 1);
        Assert.Equal(notes[1].Id, browser.Reader.Note?.Id);
        for (var i = 0; i < 4; i++) { browser.Reader.GoBack(); Assert.NotEqual(notes[0].Id, browser.Reader.Note?.Id); }
        for (var i = 0; i < 4; i++) { browser.Reader.GoForward(); Assert.NotEqual(notes[0].Id, browser.Reader.Note?.Id); }
        Assert.Equal(notes[1].Markdown, File.ReadAllText(notes[1].AbsolutePath)); Assert.Single(Await(catalog.GetTrashAsync()));
    });

    private static void Run(Action<LibraryBrowser, LibraryFixture, LibraryCatalog, IReadOnlyList<LibraryNote>, Window> action) => OnUi(() =>
    {
        using var fixture = new LibraryFixture(); fixture.Write("apple.md", "# apple\n\nFirst."); fixture.Write("banana.md", "# banana\n\nSecond.");
        var catalog = fixture.Catalog; var notes = Await(catalog.ScanAsync()).Notes.OrderBy(note => note.RelativePath).ToArray();
        using var browser = new LibraryBrowser(); browser.Configure(catalog, notes, fixture.WordRoot);
        var owner = new Window { Content = browser, Width = 1000, Height = 650, ShowInTaskbar = false };
        try { owner.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(40)); action(browser, fixture, catalog, notes, owner); }
        finally { owner.Close(); }
    });
    private static void Respond(Window owner, string title, Action trigger, Action<Window> respond)
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
        try { timer.Start(); trigger(); Until(() => handled); } finally { timer.Stop(); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Click(LibraryBrowser browser, string name) => ((Button)browser.FindName(name)).RaiseEvent(new(Button.ClickEvent));
    private static void OnUi(Action action) => ((Lazy<Dispatcher>)typeof(ReadingPageTests).GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(action);
    private static T Await<T>(Task<T> task) { Until(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < deadline) MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(predicate(), "The actual library browser operation timed out.");
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }
}
