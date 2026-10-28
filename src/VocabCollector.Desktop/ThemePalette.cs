using System.Windows;
using System.Windows.Media;

namespace VocabCollector.Desktop;

public enum AppTheme { WarmNeutral, Pink }

public static class ThemePalette
{
    // Explicit role-preserving pairs: original pink hex -> warm neutral hex.
    // Keeping the original hex in each key lets every existing surface share the palette.
    private static readonly (string Pink, string Neutral)[] Colors =
    [
        ("15563244", "154C443B"),
        ("49333F", "35312D"), // Body text.
        ("563244", "4C443B"),
        ("78616D", "625A51"),
        ("80FFFFFF", "80FFFCF7"),
        ("85717C", "756C62"),
        ("866A79", "71665B"),
        ("8B717F", "756C62"), // Secondary text retains normal-text contrast on paper.
        ("8CFFFFFF", "8CFFFCF7"),
        ("8D2850", "594331"),
        ("917544", "7A603D"),
        ("965A73", "78624E"),
        ("9A3F63", "6B5140"),
        ("A15C78", "7E6651"),
        ("A34A6D", "745640"),
        ("AC597A", "7A604A"),
        ("AD5778", "78604B"),
        ("AD8B9A", "8B7D6D"),
        ("B194A2", "8E8172"),
        ("B33960", "9F4938"), // Errors remain a distinct brick red.
        ("B46383", "8A6E55"),
        ("B49BA8", "958A7E"),
        ("B7557B", "84654D"),
        ("B88D9F", "8A7B6A"),
        ("BC9AA9", "918373"),
        ("BD416D", "6B5140"), // Primary action.
        ("BF4775", "70513E"), // Preview links and headings.
        ("C3A5B3", "AD9F8F"),
        ("C88DA5", "A18C74"),
        ("C97B9A", "987B5C"),
        ("CF7397", "9A7B5E"),
        ("D67599", "A48666"),
        ("D68CA8", "B09A80"),
        ("D9A5BA", "B9ADA0"), // Scrollbar thumb.
        ("DE6F98", "A98964"),
        ("DEB7C8", "C8BAA8"),
        ("E9BAC8", "D0BFAB"),
        ("EAA7C0", "C9B69C"),
        ("EDD2DD", "DDD3C5"),
        ("F0DCE4", "DDD4C8"), // Borders.
        ("F1CFDD", "DCD0BE"),
        ("F2CFDE", "D6C8B5"),
        ("F3ABC5", "D8C6AB"),
        ("FBD1DF", "E8DECD"),
        ("FBE6EE", "ECE5DB"),
        ("FBE8EF", "EFE7DC"),
        ("FBE8F0", "EDE5D9"),
        ("FBEAF0", "EAE1D4"), // Selected list row.
        ("FBEAF1", "ECE4D7"),
        ("FCE8EF", "EFE7DC"),
        ("FFE28A", "D8BD8F"),
        ("FFE3ED", "EEE8DF"),
        ("FFF0F5", "F4EEE5"),
        ("FFF2CF", "F2E9D7"),
        ("FFF3F2", "F5F1EA"),
        ("FFF5D9", "F3ECDC"),
        ("FFF7FA", "F7F3ED"),
        ("FFF8FB", "FAF7F1"),
        ("FFF9EB", "FFFAEF"),
        ("FFFCF9", "FFFCF7"), // Card paper.
        ("FFFCFC", "FFFDFA"), // Input surface.
        ("FFFFFF", "FFFFFF"),
        ("00FFFFFF", "00FFFFFF"),
        ("000000", "000000")
    ];

    public static ResourceDictionary Create(AppTheme theme)
    {
        var palette = new ResourceDictionary();
        foreach (var pair in Colors)
        {
            var color = (Color)ColorConverter.ConvertFromString("#" + (theme == AppTheme.Pink ? pair.Pink : pair.Neutral));
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            palette["ThemeColor" + pair.Pink] = color;
            palette["ThemeBrush" + pair.Pink] = brush;
        }

        palette["Ink"] = palette["ThemeBrush49333F"];
        palette["Muted"] = palette["ThemeBrush8B717F"];
        palette["Rose"] = palette["ThemeBrushBD416D"];
        palette["RoseSoft"] = palette["ThemeBrushFCE8EF"];
        palette["Line"] = palette["ThemeBrushF0DCE4"];
        palette["Paper"] = palette["ThemeBrushFFFCF9"];
        var shell = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            [
                new((Color)palette["ThemeColorFFE3ED"], 0),
                new((Color)palette["ThemeColorFFF3F2"], .5),
                new((Color)palette["ThemeColorFBE6EE"], 1)
            ]
        };
        shell.Freeze();
        palette["ShellBackground"] = shell;
        return palette;
    }
}
