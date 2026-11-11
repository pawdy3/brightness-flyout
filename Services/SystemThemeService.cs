using Windows.UI.ViewManagement;

namespace BrightFlyout.Services;

/// <summary>
/// Tracks the OS light/dark theme. WinUI 3 desktop apps do not reliably re-resolve
/// ElementTheme.Default when the user switches the Windows theme at runtime, so we
/// listen to UISettings.ColorValuesChanged and push an explicit theme to our windows.
/// </summary>
internal sealed class SystemThemeService
{
    private readonly UISettings _uiSettings = new();

    /// <summary>Raised (possibly on a background thread) when the OS theme changes.</summary>
    public event Action? Changed;

    public SystemThemeService()
    {
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        var isDark = IsDark;
        if (isDark != _lastIsDark)
        {
            _lastIsDark = isDark;
            Changed?.Invoke();
        }
    }

    private bool _lastIsDark;

    public bool IsDark
    {
        get
        {
            // In dark mode the system foreground color is white.
            var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
            return foreground is { R: > 127, G: > 127, B: > 127 };
        }
    }
}
