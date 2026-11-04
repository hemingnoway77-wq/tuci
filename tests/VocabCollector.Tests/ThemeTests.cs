using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VocabCollector.Desktop;

namespace VocabCollector.Tests;

public class ThemeTests
{
    private static readonly string[] Roles =
    [
        "Paper", "Sheet", "Ink", "InkSecondary", "InkTertiary", "Line", "LineSoft", "Rule",
        "Accent", "AccentInk", "AccentSoft", "Margin", "Error", "Selection", "Hover", "ShellBackground"
    ];

    [Fact]
    public void EveryThemeExposesTheSameSemanticRoles()
    {
        var mono = ThemePalette.Create(AppTheme.Mono);
        foreach (var theme in new[] { AppTheme.WarmNeutral, AppTheme.Pink })
        {
            var other = ThemePalette.Create(theme);
            Assert.Equal(mono.Keys.Cast<string>().Order(), other.Keys.Cast<string>().Order());
        }
        foreach (var role in Roles) Assert.IsType<SolidColorBrush>(mono[role]);
    }

    [Fact]
    public void MonoPaletteIsNeutralAndItsShellIsFlat()
    {
        var mono = ThemePalette.Create(AppTheme.Mono);
        foreach (DictionaryEntry entry in mono)
        {
            if (entry.Key is not string key || key is "Error" || entry.Value is not SolidColorBrush brush) continue;
            var color = brush.Color;
            var spread = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
            Assert.True(spread <= 6, key + " should stay monochrome but spreads to " + spread);
        }

        Assert.IsType<SolidColorBrush>(mono["ShellBackground"]);
        Assert.Same(mono["Paper"], mono["ShellBackground"]);
    }

    [Theory]
    [InlineData(AppTheme.Mono)]
    [InlineData(AppTheme.WarmNeutral)]
    [InlineData(AppTheme.Pink)]
    public void EveryThemeKeepsReadableTextAndActionContrast(AppTheme theme)
    {
        var palette = ThemePalette.Create(theme);
        var sheet = Brush(palette, "Sheet");
        Assert.True(Contrast(sheet, Brush(palette, "Ink")) >= 10, theme + " body text must remain easy to read.");
        Assert.True(Contrast(sheet, Brush(palette, "InkSecondary")) >= 4.5, theme + " secondary text must remain readable.");
        Assert.True(Contrast(Brush(palette, "Paper"), Brush(palette, "InkSecondary")) >= 4.5, theme + " secondary text on the page must remain readable.");
        Assert.True(Contrast(Brush(palette, "AccentInk"), Brush(palette, "Accent")) >= 4.5, theme + " primary action label must remain readable.");
    }

    [Fact]
    public void InvalidEnumFallsBackToMonoPalette()
    {
        var invalid = ThemePalette.Create((AppTheme)123);
        foreach (var role in Roles)
            Assert.Equal(Assert.IsType<SolidColorBrush>(ThemePalette.Create(AppTheme.Mono)[role]).Color,
                Assert.IsType<SolidColorBrush>(invalid[role]).Color);
    }

    [Fact]
    public void ApplyingThemeUpdatesExistingDynamicControlsAndPreservesOtherResources() => MarkdownPreviewTests.OnSta(() =>
    {
        var resources = new ResourceDictionary { ["UnrelatedSetting"] = "keep" };
        var manager = new ThemeManager(resources);
        var control = new TextBlock { Resources = resources, Text = "keep input" };
        control.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var initial = Assert.IsType<SolidColorBrush>(control.Foreground).Color;
        Assert.Equal(AppTheme.WarmNeutral, manager.CurrentTheme);

        manager.Apply(AppTheme.Pink);

        Assert.Equal(AppTheme.Pink, manager.CurrentTheme);
        Assert.NotEqual(initial, Assert.IsType<SolidColorBrush>(control.Foreground).Color);
        Assert.Equal(Color.FromRgb(0x33, 0x24, 0x2B), Assert.IsType<SolidColorBrush>(control.Foreground).Color);
        Assert.Equal("keep input", control.Text);
        Assert.Equal("keep", resources["UnrelatedSetting"]);

        manager.Apply(AppTheme.Mono);
        Assert.Equal(Color.FromRgb(0x23, 0x23, 0x21), Assert.IsType<SolidColorBrush>(control.Foreground).Color);

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

    private static Color Brush(ResourceDictionary palette, string role) =>
        Assert.IsType<SolidColorBrush>(palette[role]).Color;

    private static double Contrast(Color left, Color right)
    {
        static double Channel(byte value) { var normalized = value / 255d; return normalized <= .04045 ? normalized / 12.92 : Math.Pow((normalized + .055) / 1.055, 2.4); }
        static double Luminance(Color value) => .2126 * Channel(value.R) + .7152 * Channel(value.G) + .0722 * Channel(value.B);
        var a = Luminance(left);
        var b = Luminance(right);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
