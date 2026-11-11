using System.ComponentModel;
using BrightFlyout.Native;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace BrightFlyout.Services;

/// <summary>
/// Single source of truth for the app's effective theme. Combines the user's
/// setting (System/Light/Dark) with the OS theme, tracks OS theme switches at
/// runtime (WinUI 3 doesn't re-resolve ElementTheme.Default for us), and provides
/// shared helpers for the parts of window chrome that don't follow RequestedTheme
/// (DWM frame dark mode, WinUI-rendered caption buttons).
/// </summary>
public sealed class ThemeService
{
    private readonly SettingsService _settings;
    private readonly UISettings _uiSettings = new();
    private bool _lastSystemIsDark;

    /// <summary>Raised (possibly on a background thread) when the effective theme changes.</summary>
    public event Action? EffectiveThemeChanged;

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        _lastSystemIsDark = IsSystemDark;
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public ElementTheme EffectiveTheme => _settings.Theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => IsSystemDark ? ElementTheme.Dark : ElementTheme.Light
    };

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsService.Theme))
        {
            EffectiveThemeChanged?.Invoke();
        }
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        var isDark = IsSystemDark;
        if (isDark != _lastSystemIsDark)
        {
            _lastSystemIsDark = isDark;
            EffectiveThemeChanged?.Invoke();
        }
    }

    private bool IsSystemDark
    {
        get
        {
            // In dark mode the system foreground color is white.
            var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
            return foreground is { R: > 127, G: > 127, B: > 127 };
        }
    }

    /// <summary>Applies immersive dark mode to the window frame (non-client area).</summary>
    public static void ApplyDarkModeFrame(IntPtr hwnd, bool isDark)
    {
        int useDark = isDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
    }

    /// <summary>
    /// Themes the min/max/close buttons. They are rendered by WinUI (not DWM) when
    /// ExtendsContentIntoTitleBar is set, so they don't follow RequestedTheme.
    /// </summary>
    public static void ApplyCaptionButtonColors(AppWindow appWindow, bool isDark)
    {
        var titleBar = appWindow.TitleBar;
        if (isDark)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedForegroundColor = Colors.White;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x19, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x2D, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            titleBar.ButtonForegroundColor = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B);
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonPressedForegroundColor = Colors.Black;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x19, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x2D, 0x00, 0x00, 0x00);
        }
    }
}
