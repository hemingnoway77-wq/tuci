using System.Windows;
using System.Windows.Media;

namespace VocabCollector.Desktop;

public enum AppTheme { Mono, WarmNeutral, Pink }

/// <summary>Semantic palette shared by every surface. Themes differ only in these roles.</summary>
public static class ThemePalette
{
    private sealed record Palette(
        string Paper, string Sheet, string Ink, string InkSecondary, string InkTertiary,
        string Line, string LineSoft, string Rule, string Accent, string AccentInk,
        string AccentSoft, string Margin, string Error, string Selection, string Hover);

    private static readonly Palette Mono = new(
        Paper: "F1F1EF", Sheet: "FCFCFA", Ink: "232321", InkSecondary: "5B5B58", InkTertiary: "6F6F6B",
        Line: "E0E0DD", LineSoft: "ECECE9", Rule: "232321", Accent: "2A2A28", AccentInk: "FCFCFA",
        AccentSoft: "E9E9E6", Margin: "C9C9C4", Error: "8F3B32", Selection: "D9D9D4", Hover: "E8E8E5");

    private static readonly Palette Warm = new(
        Paper: "F1E9DA", Sheet: "FFFCF4", Ink: "2E2A23", InkSecondary: "6B6252", InkTertiary: "7D7260",
        Line: "E4D9C3", LineSoft: "F0E7D6", Rule: "2E2A23", Accent: "9A4E56", AccentInk: "FFFCF4",
        AccentSoft: "F0E2E2", Margin: "D8A7A0", Error: "A03A2C", Selection: "EADFCB", Hover: "EDE3D3");

    private static readonly Palette Rose = new(
        Paper: "FAF0F2", Sheet: "FFFBF9", Ink: "33242B", InkSecondary: "6E5560", InkTertiary: "806270",
        Line: "EBD8DE", LineSoft: "F4E7EB", Rule: "33242B", Accent: "A8445C", AccentInk: "FFFBFA",
        AccentSoft: "F3E1E7", Margin: "D9A7B0", Error: "A02B3F", Selection: "F0DCE3", Hover: "F5E7EC");

    public static ResourceDictionary Create(AppTheme theme)
    {
        var palette = theme switch
        {
            AppTheme.Pink => Rose,
            AppTheme.WarmNeutral => Warm,
            _ => Mono
        };
        var resources = new ResourceDictionary();
        Add(resources, "Paper", palette.Paper);
        Add(resources, "Sheet", palette.Sheet);
        Add(resources, "Ink", palette.Ink);
        Add(resources, "InkSecondary", palette.InkSecondary);
        Add(resources, "InkTertiary", palette.InkTertiary);
        Add(resources, "Line", palette.Line);
        Add(resources, "LineSoft", palette.LineSoft);
        Add(resources, "Rule", palette.Rule);
        Add(resources, "Accent", palette.Accent);
        Add(resources, "AccentInk", palette.AccentInk);
        Add(resources, "AccentSoft", palette.AccentSoft);
        Add(resources, "Margin", palette.Margin);
        Add(resources, "Error", palette.Error);
        Add(resources, "Selection", palette.Selection);
        Add(resources, "Hover", palette.Hover);
        // The shell is a single flat sheet; the key is kept for window backgrounds and tests.
        resources["ShellBackground"] = resources["Paper"];
        return resources;
    }

    private static void Add(ResourceDictionary resources, string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString("#" + hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
