using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VocabCollector.Desktop;

/// <summary>Paints the native Windows 11 caption with the current paper palette.</summary>
public static class TitleBarTheme
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static bool Apply(Window window)
    {
        if (!IsSupported) return false;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        if (window.TryFindResource("Paper") is not SolidColorBrush paper ||
            window.TryFindResource("Ink") is not SolidColorBrush ink ||
            window.TryFindResource("Line") is not SolidColorBrush line)
            return false;

        // Force light caption buttons so they stay visible on the paper-coloured caption.
        var light = 0;
        Set(handle, UseImmersiveDarkMode, ref light);

        var applied = SetColor(handle, CaptionColor, paper.Color);
        applied &= SetColor(handle, TextColor, ink.Color);
        applied &= SetColor(handle, BorderColor, line.Color);
        return applied;
    }

    private static bool SetColor(IntPtr hwnd, int attribute, Color color)
    {
        var value = color.R | (color.G << 8) | (color.B << 16);
        return Set(hwnd, attribute, ref value);
    }

    private static bool Set(IntPtr hwnd, int attribute, ref int value)
    {
        try { return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
