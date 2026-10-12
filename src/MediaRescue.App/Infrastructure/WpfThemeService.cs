using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace MediaRescue.App.Infrastructure;

/// <summary>Swaps the Light/Dark palette at runtime; "System" follows the Windows app theme.</summary>
public sealed class WpfThemeService : IThemeService
{
    private static bool _isDark;
    private string _requested = "System";

    public WpfThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General || _requested != "System") return;
            Application.Current?.Dispatcher.InvokeAsync(() => Apply(_requested));
        };
    }

    public static bool IsDark => _isDark;

    public void Apply(string theme)
    {
        _requested = theme;
        var dark = theme == "Dark" || (theme != "Light" && SystemUsesDarkTheme());
        _isDark = dark;
        var app = Application.Current;
        if (app == null) return;
        var uri = new Uri($"pack://application:,,,/MediaRescue.App;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute);
        var dict = new ResourceDictionary { Source = uri };
        if (app.Resources.MergedDictionaries.Count > 0) app.Resources.MergedDictionaries[0] = dict;
        else app.Resources.MergedDictionaries.Add(dict);
        foreach (Window w in app.Windows) ApplyTitleBar(w);
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Makes the native title bar follow the theme (Windows 10 2004+ / 11).</summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int value = _isDark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
        }
        catch (Exception) { /* cosmetic only */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
