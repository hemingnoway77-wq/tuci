using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VocabCollector.Core;
using VocabCollector.Desktop;
using VocabCollector.Desktop.Controls;
using Xunit.Abstractions;

namespace VocabCollector.Tests;

[Collection("ReadingPage STA")]
public sealed class SettingsWindowLayoutTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(900, 700, false)]
    [InlineData(900, 700, true)]
    [InlineData(780, 600, false)]
    [InlineData(780, 600, true)]
    public void FirstOpenKeepsTheFolderPickerFullyInsideItsViewport(double width, double height, bool longPath) => OnUi(() =>
    {
        var path = longPath ? @"C:\Users\reader\" + string.Join("\\", Enumerable.Repeat("a-long-existing-library-folder", 14)) : "";
        var draft = new AppSettings(new("https://api.example.invalid/" + new string('a', 120), new string('k', longPath ? 128 : 64), "fake-" + new string('m', 96)), new(path, "英语单词", false));
        var window = new SettingsWindow(draft, new NoSettingsWrites()) { Width = width, Height = height };
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); icon.Freeze(); window.Icon = icon;
        try
        {
            window.Show();
            Assert.IsType<RadioButton>(window.FindName("LibraryTab")).IsChecked = true;
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(80)); window.UpdateLayout();
            var browse = Assert.IsType<Button>(window.FindName("BrowseButton"));
            var vault = Assert.IsType<TextBox>(window.FindName("VaultBox"));
            var viewport = Ancestors(browse).OfType<ScrollContentPresenter>().First();
            var bounds = browse.TransformToAncestor(viewport).TransformBounds(new Rect(browse.RenderSize));
            var fieldBounds = vault.TransformToAncestor(viewport).TransformBounds(new Rect(vault.RenderSize));
            var row = Assert.IsType<DockPanel>(window.FindName("VaultPathRow"));
            output.WriteLine($"{width}x{height}, longPath={longPath}, fakeKeyLength={draft.Api.ApiKey.Length}: viewport={viewport.ActualWidth:0.00}, rowActual={row.ActualWidth}, rowDesired={row.DesiredSize.Width}, browse={bounds}, desired={browse.DesiredSize}, vaultMin={vault.MinWidth}, vault={fieldBounds}");
            UiRegressionSnapshot.Save(window, $"settings-{width:0}x{height:0}-{(longPath ? "long-path" : "initial")}.png");
            Assert.True(bounds.Left >= -.5 && bounds.Right <= viewport.ActualWidth + .5,
                $"Folder picker is horizontally clipped: {bounds}; viewport width {viewport.ActualWidth}.");
            Assert.True(bounds.Top >= -.5 && bounds.Bottom <= viewport.ActualHeight + .5);
            Assert.True(browse.ActualWidth + browse.Margin.Left + browse.Margin.Right >= browse.DesiredSize.Width - .5);
            Assert.True(fieldBounds.Right <= bounds.Left + .5, "The path field must not overlap the picker.");
            Assert.True(vault.ActualWidth >= 60, "The path remains usable beside the complete picker.");
            Assert.Equal(path, vault.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CategoryChangesPreserveDraftAndAlignTheVerticalIndicator() => OnUi(() =>
    {
        var window = new SettingsWindow(AppSettings.Default, new NoSettingsWrites());
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4); icon.Freeze(); window.Icon = icon;
        try
        {
            window.Show(); MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(80)); window.UpdateLayout();
            var account = Assert.IsType<RadioButton>(window.FindName("AccountTab"));
            var library = Assert.IsType<RadioButton>(window.FindName("LibraryTab"));
            var api = Assert.IsType<RadioButton>(window.FindName("ApiTab"));
            var updates = Assert.IsType<RadioButton>(window.FindName("UpdateTab"));
            var navigation = Assert.IsType<Grid>(window.FindName("CategoryNavigation"));
            var indicator = Assert.IsType<PageTabIndicator>(window.FindName("CategoryIndicator"));
            var vault = Assert.IsType<TextBox>(window.FindName("VaultBox"));
            var key = Assert.IsType<PasswordBox>(window.FindName("KeyBox"));
            double Center(RadioButton tab) => tab.TranslatePoint(new Point(0, tab.ActualHeight / 2), navigation).Y;
            double IndicatorCenter() => (indicator.LeftEdge + indicator.RightEdge) / 2;

            Assert.Equal(Orientation.Vertical, indicator.Orientation);
            Assert.True(account.IsChecked == true);
            Assert.InRange(Math.Abs(IndicatorCenter() - Center(account)), 0, .5);

            library.IsChecked = true;
            window.UpdateLayout();
            Assert.True(vault.IsVisible);
            vault.Text = @"C:\reader\draft";
            api.IsChecked = true;
            window.UpdateLayout();
            Assert.True(key.IsVisible);
            key.Password = "draft-key";
            updates.IsChecked = true;
            Assert.False(key.IsVisible);
            MarkdownPreviewTests.Pump(TimeSpan.FromMilliseconds(310));
            Assert.InRange(Math.Abs(IndicatorCenter() - Center(updates)), 0, .5);
            Assert.False(indicator.HasAnimatedProperties);

            library.IsChecked = true;
            Assert.Equal(@"C:\reader\draft", vault.Text);
            api.IsChecked = true;
            Assert.Equal("draft-key", key.Password);
        }
        finally { window.Close(); }
    });

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    { for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent)) yield return parent; }
    private static void OnUi(Action test) => ((Lazy<Dispatcher>)typeof(ReadingPageTests)
        .GetField("Ui", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Value.Invoke(test);
    private sealed class NoSettingsWrites : ISettingsStore
    {
        public Task<SettingsLoadResult> LoadAsync(CancellationToken ct) => throw new InvalidOperationException("No settings reads in layout checks.");
        public Task SaveAsync(AppSettings settings, CancellationToken ct) => throw new InvalidOperationException("No settings writes in layout checks.");
    }
}

internal static class UiRegressionSnapshot
{
    public static void Save(Window window, string file)
    {
        var content = (FrameworkElement)window.Content;
        window.UpdateLayout(); var margin = content.Margin;
        var width = content.ActualWidth + margin.Left + margin.Right;
        var height = content.ActualHeight + margin.Top + margin.Bottom;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.artifacts/ui-fixes")); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, file)); encoder.Save(output);
    }
}
