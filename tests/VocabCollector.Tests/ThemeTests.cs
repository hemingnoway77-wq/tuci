using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

public class ThemeTests
{
    private static readonly string[] OriginalColors =
    [
        "15563244", "49333F", "563244", "78616D", "80FFFFFF", "85717C", "866A79", "8B717F",
        "8CFFFFFF", "8D2850", "917544", "965A73", "9A3F63", "A15C78", "A34A6D", "AC597A",
        "AD5778", "AD8B9A", "B194A2", "B33960", "B46383", "B49BA8", "B7557B", "B88D9F",
        "BC9AA9", "BD416D", "BF4775", "C3A5B3", "C88DA5", "C97B9A", "CF7397", "D67599",
        "D68CA8", "D9A5BA", "DE6F98", "DEB7C8", "E9BAC8", "EAA7C0", "EDD2DD", "F0DCE4",
        "F1CFDD", "F2CFDE", "F3ABC5", "FBD1DF", "FBE6EE", "FBE8EF", "FBE8F0", "FBEAF0",
        "FBEAF1", "FCE8EF", "FFE28A", "FFE3ED", "FFF0F5", "FFF2CF", "FFF3F2", "FFF5D9",
        "FFF7FA", "FFF8FB", "FFF9EB", "FFFCF9", "FFFCFC", "FFFFFF", "00FFFFFF", "000000"
    ];

    [Fact]
    public void PinkPalettePreservesEveryOriginalColorAndBrush()
    {
        var palette = ThemePalette.Create(AppTheme.Pink);
        foreach (var hex in OriginalColors)
        {
            var color = (Color)ColorConverter.ConvertFromString("#" + hex);
            Assert.Equal(color, Assert.IsType<Color>(palette["ThemeColor" + hex]));
            Assert.Equal(color, Assert.IsType<SolidColorBrush>(palette["ThemeBrush" + hex]).Color);
        }
        Assert.Equal(Color.FromRgb(189, 65, 109), Assert.IsType<SolidColorBrush>(palette["Rose"]).Color);
    }

    [Fact]
    public void NeutralPaletteCoversSameKeysWithWarmReadableHierarchy()
    {
        var neutral = ThemePalette.Create(AppTheme.WarmNeutral);
        var pink = ThemePalette.Create(AppTheme.Pink);
        Assert.Equal(pink.Keys.Cast<string>().Order(), neutral.Keys.Cast<string>().Order());
        foreach (DictionaryEntry entry in neutral)
        {
            if (entry.Value is not Color color) continue;
            Assert.True(color.R >= color.G && color.G >= color.B, entry.Key.ToString());
        }

        var paper = Assert.IsType<SolidColorBrush>(neutral["Paper"]).Color;
        var ink = Assert.IsType<SolidColorBrush>(neutral["Ink"]).Color;
        var muted = Assert.IsType<SolidColorBrush>(neutral["Muted"]).Color;
        var primary = Assert.IsType<SolidColorBrush>(neutral["Rose"]).Color;
        Assert.True(Contrast(paper, ink) >= 10, "Body text must remain easy to read.");
        Assert.True(Contrast(paper, muted) >= 4.5, "Secondary text must remain readable.");
        Assert.True(Contrast(Colors.White, primary) >= 4.5, "Primary button label must remain readable.");
        Assert.IsType<LinearGradientBrush>(neutral["ShellBackground"]);
    }

    [Fact]
    public void InvalidEnumFallsBackToNeutralPalette()
    {
        var palette = ThemePalette.Create((AppTheme)123);
        Assert.Equal(Assert.IsType<Color>(ThemePalette.Create(AppTheme.WarmNeutral)["ThemeColorBD416D"]),
            Assert.IsType<Color>(palette["ThemeColorBD416D"]));
    }

    [Fact]
    public void ApplyingThemeUpdatesExistingDynamicControlsAndPreservesOtherResources() => MarkdownPreviewTests.OnSta(() =>
    {
        var resources = new ResourceDictionary { ["UnrelatedSetting"] = "keep" };
        var manager = new ThemeManager(resources);
        var control = new TextBlock { Resources = resources, Text = "keep input" };
        control.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var initial = Assert.IsType<SolidColorBrush>(control.Foreground).Color;

        manager.Apply(AppTheme.Pink);

        Assert.Equal(AppTheme.Pink, manager.CurrentTheme);
        Assert.NotEqual(initial, Assert.IsType<SolidColorBrush>(control.Foreground).Color);
        Assert.Equal(Color.FromRgb(73, 51, 63), Assert.IsType<SolidColorBrush>(control.Foreground).Color);
        Assert.Equal("keep input", control.Text);
        Assert.Equal("keep", resources["UnrelatedSetting"]);
        manager.Apply((AppTheme)123);
        Assert.Equal(AppTheme.WarmNeutral, manager.CurrentTheme);
        Assert.Equal(initial, Assert.IsType<SolidColorBrush>(control.Foreground).Color);
    });

    [Fact]
    public void ManagerLoadsAndSavesWithoutApiSettings() => MarkdownPreviewTests.OnSta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "vocab-theme-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppearanceSettingsStore(directory);
            var manager = new ThemeManager(new ResourceDictionary(), store);
            Assert.True(manager.SetThemeAsync(AppTheme.Pink).GetAwaiter().GetResult());
            var restarted = new ThemeManager(new ResourceDictionary(), new AppearanceSettingsStore(directory));
            restarted.LoadAsync().GetAwaiter().GetResult();
            Assert.Equal(AppTheme.Pink, restarted.CurrentTheme);
            Assert.False(File.Exists(Path.Combine(directory, "settings.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    [Fact]
    public void FailedSaveKeepsImmediateVisualChangeAndPreviousPreference() => MarkdownPreviewTests.OnSta(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "vocab-theme-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppearanceSettingsStore(directory);
            store.SaveAsync(AppTheme.WarmNeutral).GetAwaiter().GetResult();
            var manager = new ThemeManager(new ResourceDictionary(), store);
            using (var locked = new FileStream(Path.Combine(directory, "appearance.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var save = manager.SetThemeAsync(AppTheme.Pink);
                Assert.Equal(AppTheme.Pink, manager.CurrentTheme);
                Assert.False(save.GetAwaiter().GetResult());
            }
            Assert.Equal(AppTheme.WarmNeutral, store.LoadAsync().GetAwaiter().GetResult());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    private static double Contrast(Color left, Color right)
    {
        static double Channel(byte value) { var normalized = value / 255d; return normalized <= .04045 ? normalized / 12.92 : Math.Pow((normalized + .055) / 1.055, 2.4); }
        static double Luminance(Color value) => .2126 * Channel(value.R) + .7152 * Channel(value.G) + .0722 * Channel(value.B);
        var a = Luminance(left);
        var b = Luminance(right);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
