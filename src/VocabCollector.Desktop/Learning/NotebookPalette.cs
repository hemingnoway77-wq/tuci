using System.Windows;
using System.Windows.Media;

namespace VocabCollector.Desktop.Learning;

public static class NotebookPalette
{
    public static ResourceDictionary Create(bool dark)
    {
        var colors = dark ? new[] { "#1E1E1E", "#262626", "#303030", "#DADADA", "#A6A6A6", "#383838", "#A68AF9", "#343434", "#423867" }
            : new[] { "#FFFFFF", "#F6F6F6", "#EBEBEB", "#222222", "#6C6C6C", "#DEDEDE", "#7852C7", "#E8E8E8", "#E9E0FA" };
        var keys = new[] { "NotebookBackground", "NotebookSurface", "NotebookRaised", "NotebookText", "NotebookMuted", "NotebookBorder", "NotebookAccent", "NotebookHover", "NotebookSelection" };
        var result = new ResourceDictionary();
        for (var index = 0; index < keys.Length; index++) result[keys[index]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[index]));
        foreach (var (target, source) in new[] { ("Paper",0),("Sheet",0),("ShellBackground",0),("Ink",3),("InkSecondary",4),("InkTertiary",4),("Line",5),("LineSoft",5),("Rule",5),("Accent",6),("AccentInk",0),("AccentSoft",2),("Margin",6),("Hover",7),("Selection",8) }) result[target] = result[keys[source]];
        result["Error"] = new SolidColorBrush(dark ? Color.FromRgb(239, 138, 138) : Color.FromRgb(175, 40, 40));
        return result;
    }
}
