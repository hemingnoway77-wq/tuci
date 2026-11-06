using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Reflection;
using VocabCollector.Core;
using VocabCollector.Core.Library;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Learning;
using VocabCollector.Desktop.Preview;

namespace VocabCollector.Tests;

public sealed class NoteReaderTests
{
    [Fact]
    public void SwitchingWarmToMonoRecolorsNestedLinksEmphasisAndQuoteRulesWithoutChangingSource() => Run((reader, owner, notes) =>
    {
        var themes = new ThemeManager(owner.Resources);
        var markdown = "# introduced\n\n**释义**：\n\n> v. 介绍\n> v. 引进\n\n#### 例句\n\n1. She **introduced** her [[second]].\n   - 中文翻译。";
        reader.DirectLinks = true; reader.OpenNote(notes[0] with { Markdown = markdown });
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        var preview = Descendants<MarkdownPreview>(reader).Single();
        var document = preview.Document;
        themes.Apply(AppTheme.Mono);
        foreach (var element in TextElements(document))
        {
            var color = Assert.IsType<SolidColorBrush>(element.Foreground).Color;
            Assert.InRange(Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)), 0, 6);
            if (element is Bold) Assert.Same(owner.Resources["Ink"], element.Foreground);
            if (element is Hyperlink) Assert.Same(owner.Resources["Accent"], element.Foreground);
        }
        var quote = document.Blocks.OfType<Section>().Single();
        Assert.Same(owner.Resources["Line"], quote.BorderBrush);
        Assert.Equal(markdown, reader.Note?.Markdown);
        Assert.Same(document, preview.Document);
    });

    private static IEnumerable<TextElement> TextElements(FlowDocument document)
    {
        for (var pointer = document.ContentStart; pointer is not null && pointer.CompareTo(document.ContentEnd) < 0; pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart && pointer.GetAdjacentElement(LogicalDirection.Forward) is TextElement element)
                yield return element;
    }
    [Fact]
    public void OrdinaryLocalHoverShowsFocusSafeCardAndHidesWhenLeavingPage() => Run((reader, owner, notes) =>
    {
        reader.DirectLinks = true; reader.OpenNote(notes[0]);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
        var preview = Descendants<MarkdownPreview>(reader).Single();
        preview.Focus(); var focused = System.Windows.Input.Keyboard.FocusedElement;
        var link = preview.Document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>()).First();
        MarkdownPreviewTests.MoveMouseOver(link);
        var card = Assert.IsType<Popup>(HoverPopup(reader));
        Assert.True(card.IsOpen);
        Assert.False(card.Focusable);
        Assert.False(card.IsHitTestVisible);
        Assert.Contains(Descendants<TextBlock>(card.Child), block => block.Text == "second");
        var surface = Assert.IsType<Border>(card.Child);
        Assert.Same(owner.FindResource("Sheet"), surface.Background);
        new ThemeManager(owner.Resources).Apply(AppTheme.Mono);
        Assert.Same(owner.FindResource("Sheet"), surface.Background);
        Assert.Same(owner.FindResource("Ink"), Descendants<TextBlock>(surface).Single(block => block.Text == "second").Foreground);
        Assert.Same(focused, System.Windows.Input.Keyboard.FocusedElement);
        reader.Visibility = Visibility.Collapsed;
        Assert.False(card.IsOpen);
        Assert.Null(HoverPopup(reader));
        reader.Visibility = Visibility.Visible;
        reader.OpenNote(notes[0] with { Markdown = "# first\n\n[second](https://example.com/second)" });
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30));
        var web = preview.Document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>()).Single();
        MarkdownPreviewTests.MoveMouseOver(web);
        Assert.Null(HoverPopup(reader));
    });

    private static Popup? HoverPopup(NoteReader reader) => (Popup?)typeof(NoteReader)
        .GetField("_hover", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader);
    [Fact]
    public void DirectReadingFollowsClientThemesWithoutRebuildingDocumentOrScalingFonts() => Run((reader, owner, notes) =>
    {
        // A separate workspace palette must not override the client's reading colors.
        owner.Resources["NotebookText"] = Brushes.Magenta;
        reader.DirectLinks = true;
        reader.OpenNote(notes[0]);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
        reader.DirectLinks = true;
        var preview = Descendants<MarkdownPreview>(reader).Single();
        var document = preview.Document;
        var heading = document.Blocks.OfType<System.Windows.Documents.Paragraph>()
            .Single(paragraph => MarkdownDocumentRenderer.GetHeadingLevel(paragraph) == 1);
        preview.SelectAll();
        var selection = preview.Selection.Text;
        var content = preview.RenderedText;
        var bodySize = document.FontSize;
        var headingSize = heading.FontSize;
        ResourceDictionary? previous = null;
        foreach (var theme in new[] { AppTheme.WarmNeutral, AppTheme.Mono, AppTheme.Pink })
        {
            if (previous is not null) owner.Resources.MergedDictionaries.Remove(previous);
            var palette = ThemePalette.Create(theme);
            owner.Resources.MergedDictionaries.Add(palette);
            previous = palette;
            preview.UpdateLayout();
            Assert.Same(palette["Ink"], heading.Foreground);
            Assert.Same(palette["Ink"], document.Foreground);
            Assert.Same(document, preview.Document);
            Assert.Equal(content, preview.RenderedText);
            Assert.Equal(selection, preview.Selection.Text);
            Assert.Equal(bodySize, document.FontSize);
            Assert.Equal(headingSize, heading.FontSize);
            Assert.Equal(notes[0].Markdown, reader.Note?.Markdown);
        }
    });
    [Fact]
    public void OutlineUsesRenderedHeadingsIncludingQuotesAndExcludesHiddenHtmlAndBoldText() => Run((reader, owner, notes) =>
    {
        const string start = "# Visible *one*\n\n> ## Quoted **two**\n\n<div>\n# Hidden html\n</div>\n\n**Ordinary bold line**\n\n";
        reader.OpenNote(notes[0] with { Markdown = start + string.Join("\n\n", Enumerable.Repeat("Long reading paragraph.", 80)) + "\n\n### Last heading\n" });
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        Assert.Equal(new[] { "Visible one", "Quoted two", "Last heading" }, reader.Outline.Select(heading => heading.Text));
        Assert.Equal(new[] { 1, 2, 3 }, reader.Outline.Select(heading => heading.Level));
        Assert.Equal(new[] { 0, 1, 2 }, reader.Outline.Select(heading => heading.Index));
        var preview = Descendants<MarkdownPreview>(reader).Single();
        // Decorative font weight must not become a navigation marker.
        var bold = preview.Document.Blocks.OfType<System.Windows.Documents.Paragraph>()
            .Single(paragraph => new System.Windows.Documents.TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains("Ordinary bold line"));
        bold.FontWeight = FontWeights.SemiBold;
        reader.NavigateHeading(2);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        Assert.True(preview.VerticalOffset > 400);
    });
    [Fact]
    public void RepeatedFindAdvancesAndHeadingIndexScrollsToLaterHeading() => Run((reader, owner, notes) =>
    {
        reader.DirectLinks = true;
        var markdown = "# First\n\nfind me\n\nfind me\n\n" + string.Join("\n\n", Enumerable.Repeat("Long paragraph.", 80)) + "\n\n## Last heading\n\nfind me";
        reader.OpenNote(notes[0] with { Markdown = markdown });
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(70));
        var preview = Descendants<MarkdownPreview>(reader).Single();
        Assert.True(reader.FindText("find me"));
        var first = preview.Selection.Start;
        Assert.True(reader.FindText("find me"));
        Assert.True(preview.Selection.Start.CompareTo(first) > 0);
        reader.NavigateHeading(1);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(70));
        Assert.True(preview.VerticalOffset > 400);
    });
    [Fact]
    public void DirectLinksNavigateAndControlLinksRequestNewTabWithoutChangingCurrent() => Run((reader, owner, notes) =>
    {
        reader.DirectLinks = true;
        reader.OpenNote(notes[0]);
        LibraryNote? newTab = null;
        reader.NewTabRequested += (_, note) => newTab = note;
        reader.HandleLink(new PreviewLinkEventArgs("second", true, modifiers: System.Windows.Input.ModifierKeys.Control), notes[0], owner);
        Assert.Equal(notes[1], newTab);
        Assert.Equal(notes[0], reader.Note);
        reader.HandleLink(new PreviewLinkEventArgs("second", true), notes[0], owner);
        Assert.Equal(notes[1], reader.Note);
        Assert.Empty(owner.OwnedWindows.Cast<Window>());
        reader.GoBack();
        Assert.True(reader.CanGoForward);
        reader.GoForward();
        Assert.Equal(notes[1], reader.Note);
    });
    [Fact]
    public void SingleLinkShowsCompactCardAndOnlyExplicitOpenNavigates() => Run((reader, owner, notes) =>
    {
        reader.OpenNote(notes[0]);
        reader.HandleLink(new PreviewLinkEventArgs("second", true), notes[0], owner);
        Assert.Equal(notes[0], reader.Note);
        var card = Assert.Single(owner.OwnedWindows.Cast<Window>());
        Assert.Same(owner, card.Owner);
        var cardText = string.Join(" ", Descendants<TextBlock>(card).Select(t => t.Text));
        Assert.Contains("second", cardText);
        Assert.Contains("/ˈsekənd/", cardText);
        Assert.Contains("第二。", cardText);
        Assert.DoesNotContain("FULL BODY MARKER", cardText);
        Assert.True(cardText.Length < 900, "The compact card must not expand into the full note.");
        var open = Descendants<Button>(card).Single(b => Equals(b.Content, "打开整篇"));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(notes[1], reader.Note);
        Assert.Empty(owner.OwnedWindows.Cast<Window>());
        Assert.True(reader.CanGoBack);
    });

    [Fact]
    public void DoubleLinkNavigatesOnceWithoutCreatingCardAndBackRestoresScroll() => Run((reader, owner, notes) =>
    {
        reader.OpenNote(notes[0]);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        var preview = Descendants<MarkdownPreview>(reader).Single();
        preview.ScrollToVerticalOffset(280);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
        var saved = preview.VerticalOffset;
        Assert.True(saved > 50);
        var changes = new List<LibraryNote>();
        reader.NoteChanged += (_, note) => changes.Add(note);
        reader.HandleLink(new PreviewLinkEventArgs("second", true, true), notes[0], owner);
        Assert.Single(changes);
        Assert.Empty(owner.OwnedWindows.Cast<Window>());
        reader.GoBack();
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(80));
        Assert.Equal(notes[0], reader.Note);
        Assert.InRange(preview.VerticalOffset, saved - 2, saved + 2);
        Assert.False(reader.CanGoBack);
    });

    [Fact]
    public void RefreshKeepsStableIdNavigationAndClampsScrollWhenOriginalNoteShrinks() => Run((reader, owner, notes) =>
    {
        reader.OpenNote(notes[0]);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        var preview = Descendants<MarkdownPreview>(reader).Single();
        preview.ScrollToVerticalOffset(400);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(30));
        reader.OpenNote(notes[1]);
        var renamed = new LibraryNote(notes[0].Id, "renamed.md", notes[0].AbsolutePath, "# renamed\n\nShort text.", notes[0].AddedAt);
        reader.RefreshNotes([renamed, notes[1]]);
        reader.GoBack();
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(80));
        Assert.Same(renamed, reader.Note);
        Assert.Contains("Short text.", preview.RenderedText);
        Assert.Equal(0, preview.VerticalOffset);
    });

    [Fact]
    public void MissingLinksReportMessageAndHttpLinksUseHostEvent() => Run((reader, owner, notes) =>
    {
        reader.OpenNote(notes[0]);
        string? message = null;
        string? web = null;
        reader.MessageChanged += (_, text) => message = text;
        reader.WebLinkRequested += (_, url) => web = url;
        reader.HandleLink(new PreviewLinkEventArgs("missing", true), notes[0], owner);
        Assert.Contains("missing", message);
        Assert.Equal(notes[0], reader.Note);
        Assert.Empty(owner.OwnedWindows.Cast<Window>());
        reader.HandleLink(new PreviewLinkEventArgs("https://example.com/article", false), notes[0], owner);
        Assert.Equal("https://example.com/article", web);
        web = null;
        reader.HandleLink(new PreviewLinkEventArgs("javascript:alert(1)", false), notes[0], owner);
        Assert.Null(web);
        Assert.Equal(notes[0], reader.Note);
    });

    [Fact]
    public void AmbiguousLinkShowsPathsAndSelectionOpensChosenNote() => Run((reader, owner, notes) =>
    {
        var sameA = new LibraryNote("a", "a/same.md", Path.Combine(Path.GetDirectoryName(notes[0].AbsolutePath)!, "a/same.md"), "# same\n\nfirst candidate", notes[0].AddedAt);
        var sameB = new LibraryNote("b", "b/same.md", Path.Combine(Path.GetDirectoryName(notes[0].AbsolutePath)!, "b/same.md"), "# same\n\nsecond candidate", notes[0].AddedAt);
        reader.RefreshNotes([.. notes, sameA, sameB]);
        reader.OpenNote(notes[0]);
        reader.HandleLink(new PreviewLinkEventArgs("same", true, true), notes[0], owner);
        Assert.Equal(notes[0], reader.Note);
        var chooser = Assert.Single(owner.OwnedWindows.Cast<Window>());
        var list = Descendants<ListBox>(chooser).Single();
        Assert.Contains("a/same.md", list.Items.Cast<ListBoxItem>().Select(i => i.Content));
        Assert.Contains("b/same.md", list.Items.Cast<ListBoxItem>().Select(i => i.Content));
        list.SelectedIndex = 1;
        Descendants<Button>(chooser).Single(b => Equals(b.Content, "打开整篇")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(sameB, reader.Note);
    });

    [Fact]
    public void DisposeClosesOwnedCardAndPreventsFurtherNavigation() => Run((reader, owner, notes) =>
    {
        reader.OpenNote(notes[0]);
        reader.HandleLink(new PreviewLinkEventArgs("second", true), notes[0], owner);
        Assert.Single(owner.OwnedWindows.Cast<Window>());
        reader.Dispose();
        Assert.Empty(owner.OwnedWindows.Cast<Window>());
        reader.HandleLink(new PreviewLinkEventArgs("second", true, true), notes[0], owner);
        Assert.Equal(notes[0], reader.Note);
    });

    [Fact]
    public void OpeningCurrentNoteFromItsOwnCardClosesCardWithoutAddingHistory() => Run((reader, owner, notes) =>
    {
        reader.OpenNote(notes[0]);
        reader.HandleLink(new PreviewLinkEventArgs("first", true), notes[0], owner);
        var card = Assert.Single(owner.OwnedWindows.Cast<Window>());
        Descendants<Button>(card).Single(b => Equals(b.Content, "打开整篇")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(owner.OwnedWindows.Cast<Window>());
        Assert.Equal(notes[0], reader.Note);
        Assert.False(reader.CanGoBack);
    });

    [Fact]
    public void EqualSnapshotRefreshKeepsOpenCardAndReadingSelection() => Run((reader, owner, notes) =>
    {
        reader.OpenNote(notes[0]);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        var preview = Descendants<MarkdownPreview>(reader).Single();
        preview.ScrollToVerticalOffset(240);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(40));
        preview.Selection.Select(preview.Document.ContentStart, preview.Document.ContentStart.GetPositionAtOffset(12)!);
        var selectedText = preview.Selection.Text;
        var document = preview.Document;
        var offset = preview.VerticalOffset;
        reader.HandleLink(new PreviewLinkEventArgs("second", true), notes[0], owner);
        var card = Assert.Single(owner.OwnedWindows.Cast<Window>());

        // A focus-triggered scan returns new note records even when disk content is unchanged.
        reader.RefreshNotes(notes.Select(note => note with { }).Reverse().ToArray());
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(650));

        Assert.Same(card, Assert.Single(owner.OwnedWindows.Cast<Window>()));
        Assert.True(card.IsVisible);
        Assert.Same(document, preview.Document);
        Assert.Equal(selectedText, preview.Selection.Text);
        Assert.InRange(preview.VerticalOffset, offset - 2, offset + 2);
        Assert.False(reader.CanGoBack);
    });

    [Fact]
    public void EqualSnapshotRefreshKeepsPendingMouseSingleClick() => Run((reader, owner, notes) =>
    {
        reader.OpenNote(notes[0]);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        var preview = Descendants<MarkdownPreview>(reader).Single();
        var link = preview.Document.Blocks.OfType<System.Windows.Documents.Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.OfType<System.Windows.Documents.Hyperlink>()).First();
        MarkdownPreviewTests.MouseClick(link, 1);
        reader.RefreshNotes(notes.Select(note => note with { }).ToArray());
        Assert.Empty(owner.OwnedWindows.Cast<Window>());
        MarkdownPreviewTests.Pump(MarkdownPreviewTests.ClickWait);
        Assert.Single(owner.OwnedWindows.Cast<Window>());
        Assert.Equal(notes[0], reader.Note);
    });

    [Fact]
    public void OpeningCurrentNoteFromCardRequestsNavigationExactlyOnce() => Run((reader, owner, notes) =>
    {
        reader.OpenNote(notes[0], remember: false);
        var navigations = 0;
        var changes = 0;
        reader.NavigationRequested += (_, _) => navigations++;
        reader.NoteChanged += (_, _) => changes++;
        reader.HandleLink(new PreviewLinkEventArgs("first", true), notes[0], owner);
        var card = Assert.Single(owner.OwnedWindows.Cast<Window>());
        Descendants<Button>(card).Single(b => Equals(b.Content, "打开整篇")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, navigations);
        Assert.Equal(0, changes);
        Assert.False(reader.CanGoBack);
    });

    [Fact]
    public void NavigationOnlyFollowsExplicitOpenAndActualBackNotPreloadOrRefresh() => Run((reader, owner, notes) =>
    {
        var navigations = 0;
        reader.NavigationRequested += (_, _) => navigations++;
        var catalogRoot = Path.GetDirectoryName(Path.GetDirectoryName(notes[0].AbsolutePath)!)!;
        reader.Configure(new LibraryCatalog(new VaultOptions(catalogRoot, "notes", false)), notes);
        reader.OpenNote(notes[0], remember: false);
        reader.RefreshNotes(notes.Select(note => note with { }).ToArray());
        var edited = notes[0] with { Markdown = notes[0].Markdown + "\n\nUpdated text." };
        reader.RefreshNotes([edited, notes[1]]);
        reader.GoBack();
        Assert.Equal(0, navigations);
        reader.OpenNote(edited);
        Assert.Equal(1, navigations);
        reader.OpenNote(notes[1]);
        Assert.Equal(2, navigations);
        reader.GoBack();
        Assert.Equal(3, navigations);
        reader.GoBack();
        Assert.Equal(3, navigations);
        Assert.Equal(edited, reader.Note);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreloadedSourceOffsetSurvivesImmediateNavigationAndBackWithoutPreloadNavigation(bool sourceAlreadyOpen) => Run((reader, owner, notes) =>
    {
        if (sourceAlreadyOpen)
        {
            reader.OpenNote(notes[0], remember: false);
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(60));
        }
        var navigations = 0;
        reader.NavigationRequested += (_, _) => navigations++;

        reader.OpenNote(notes[0], remember: false, initialOffset: 280);
        Assert.Equal(0, navigations);
        // No dispatcher/layout turn occurs between preloading the source and following its link.
        reader.OpenNote(notes[1]);
        Assert.Equal(1, navigations);
        reader.GoBack();
        Assert.Equal(2, navigations);
        MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(80));

        Assert.Equal(notes[0], reader.Note);
        Assert.InRange(Descendants<MarkdownPreview>(reader).Single().VerticalOffset, 278, 282);
        Assert.False(reader.CanGoBack);
    });

    private static void Run(Action<NoteReader, Window, LibraryNote[]> action) => MarkdownPreviewTests.OnSta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "note-reader-" + Guid.NewGuid().ToString("N"));
        var wordRoot = Path.Combine(root, "notes");
        Directory.CreateDirectory(wordRoot);
        var now = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        LibraryNote[] notes =
        [
            new("first", "first.md", Path.Combine(wordRoot, "first.md"), "# first\n\n" + string.Join("\n\n", Enumerable.Repeat("Selectable reading text with [[second]].", 100)), now),
            new("second", "second.md", Path.Combine(wordRoot, "second.md"), "# second\n\n**发音**：/ˈsekənd/\n\n**释义**：第二。\n\n" + new string('x', 1500) + " FULL BODY MARKER", now)
        ];
        using var reader = new NoteReader();
        reader.Configure(new LibraryCatalog(new VaultOptions(root, "notes", false)), notes);
        var owner = new Window { Content = reader, Width = 650, Height = 420, ShowInTaskbar = false };
        owner.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/兔词;component/Theme.xaml", UriKind.Relative) });
        try
        {
            owner.Show();
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(50));
            action(reader, owner, notes);
        }
        finally { owner.Close(); Directory.Delete(root, recursive: true); }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
