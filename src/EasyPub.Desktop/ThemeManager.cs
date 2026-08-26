using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EasyPub.Desktop;

internal static class ThemeManager
{
    public const string SystemTheme = "System";
    public const string LightTheme = "Light";
    public const string DarkTheme = "Dark";

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private static bool _currentDark;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(string? requestedTheme, Window? window = null)
    {
        var application = Application.Current;
        if (application is null) return;
        var useDark = ResolveDark(requestedTheme);
        _currentDark = useDark;
        var colors = useDark
            ? new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#121212",
                ["SurfaceBrush"] = "#1B1B1A",
                ["RaisedSurfaceBrush"] = "#232321",
                ["SubtleSurfaceBrush"] = "#20201F",
                ["SelectedSurfaceBrush"] = "#30302C",
                ["PrimaryTextBrush"] = "#F2EFE8",
                ["SecondaryTextBrush"] = "#AAA69E",
                ["DisabledTextBrush"] = "#77736D",
                ["BorderBrush"] = "#32322F",
                ["StrongBorderBrush"] = "#5A5852",
                ["PrimaryActionBrush"] = "#F2EFE8",
                ["PrimaryActionTextBrush"] = "#151515",
                ["SuccessBrush"] = "#77A66A",
                ["WarningBrush"] = "#D39A45",
                ["ErrorBrush"] = "#E1766D",
                ["WarningSurfaceBrush"] = "#30271B",
                ["InfoSurfaceBrush"] = "#242629",
            }
            : new Dictionary<string, string>
            {
                ["AppBackgroundBrush"] = "#F3F1EC",
                ["SurfaceBrush"] = "#FBFAF7",
                ["RaisedSurfaceBrush"] = "#FAF8F3",
                ["SubtleSurfaceBrush"] = "#F7F5F0",
                ["SelectedSurfaceBrush"] = "#E7E3DB",
                ["PrimaryTextBrush"] = "#151515",
                ["SecondaryTextBrush"] = "#706D67",
                ["DisabledTextBrush"] = "#AAA69E",
                ["BorderBrush"] = "#D9D6CF",
                ["StrongBorderBrush"] = "#BDB9B1",
                ["PrimaryActionBrush"] = "#151515",
                ["PrimaryActionTextBrush"] = "#FBFAF7",
                ["SuccessBrush"] = "#4F7A45",
                ["WarningBrush"] = "#A36A16",
                ["ErrorBrush"] = "#B42318",
                ["WarningSurfaceBrush"] = "#FFF7ED",
                ["InfoSurfaceBrush"] = "#F0F2F5",
            };

        foreach (var pair in colors)
            application.Resources[pair.Key] = Brush(pair.Value);

        var windows = application.Windows.Cast<Window>().ToArray();
        foreach (var openWindow in windows) ApplyToWindow(openWindow);
        if (window is not null && !windows.Contains(window)) ApplyToWindow(window);
    }

    public static void ApplyWindowChrome(Window window)
    {
        ApplyToWindow(window);
    }

    private static void ApplyToWindow(Window window)
    {
        if (Application.Current is null) return;
        window.Background = (Brush)Application.Current.Resources["AppBackgroundBrush"];
        window.Foreground = (Brush)Application.Current.Resources["PrimaryTextBrush"];
        if (window.Resources.Contains("AccentBrush"))
            window.Resources["AccentBrush"] = (Brush)Application.Current.Resources["PrimaryActionBrush"];
        if (window.Resources.Contains("TextBrush"))
            window.Resources["TextBrush"] = (Brush)Application.Current.Resources["PrimaryTextBrush"];
        if (window.Resources.Contains("MutedTextBrush"))
            window.Resources["MutedTextBrush"] = (Brush)Application.Current.Resources["SecondaryTextBrush"];
        if (window.Resources.Contains("LineBrush"))
            window.Resources["LineBrush"] = (Brush)Application.Current.Resources["BorderBrush"];

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized -= Window_SourceInitialized;
            window.SourceInitialized += Window_SourceInitialized;
            return;
        }

        SetChromeColor(handle, DwmwaUseImmersiveDarkMode, _currentDark ? 1 : 0);
        SetChromeColor(handle, DwmwaCaptionColor, ColorRef(_currentDark ? "#121212" : "#F3F1EC"));
        SetChromeColor(handle, DwmwaBorderColor, ColorRef(_currentDark ? "#32322F" : "#C9C5BD"));
        SetChromeColor(handle, DwmwaTextColor, ColorRef(_currentDark ? "#F2EFE8" : "#151515"));
    }

    private static void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.SourceInitialized -= Window_SourceInitialized;
            ApplyToWindow(window);
        }
    }

    private static void SetChromeColor(IntPtr handle, int attribute, int value)
    {
        try { _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static int ColorRef(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        return color.R | color.G << 8 | color.B << 16;
    }

    private static bool ResolveDark(string? requestedTheme)
    {
        if (string.Equals(requestedTheme, DarkTheme, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(requestedTheme, SystemTheme, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
