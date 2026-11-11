using System.ComponentModel;
using BrightFlyout.Native;
using BrightFlyout.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace BrightFlyout.Views;

/// <summary>
/// Custom on-screen-display replicating the Windows 11 brightness flyout:
/// a borderless, always-on-top, non-activatable, acrylic-backed pill anchored
/// above the taskbar at the bottom-center of the primary display.
/// </summary>
public sealed partial class OsdWindow : Window
{
    private const double WidthDip = 360;
    private const double HeightDip = 48;
    private const double BottomMarginDip = 24;

    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    public IntPtr Hwnd => _hwnd;

    /// <summary>Fired when the OSD auto-hides after its timeout.</summary>
    public event Action? AutoHidden;

    public OsdWindow(SettingsService settings)
    {
        _settings = settings;
        InitializeComponent();

        SystemBackdrop = new DesktopAcrylicBackdrop();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        _appWindow.SetPresenter(presenter);

        // Keep it out of Alt+Tab and never let it take focus.
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

        // Rounded window corners on Windows 11.
        int cornerPreference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference, sizeof(int));

        ResizeAndReposition();

        _hideTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) =>
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            AutoHidden?.Invoke();
        };

        _settings.PropertyChanged += OnSettingsChanged;
        ApplyAppearance();

        // WinUI shows new windows by default; keep the OSD hidden until needed.
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    public void ShowBrightness(int value)
    {
        FillScale.ScaleX = Math.Clamp(value, 0, 100) / 100.0;

        ResizeAndReposition();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNA);

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(_settings.OsdDurationMs);
        _hideTimer.Start();
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    private void ResizeAndReposition()
    {
        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        int width = (int)Math.Round(WidthDip * scale);
        int height = (int)Math.Round(HeightDip * scale);

        var hMonitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfoW(hMonitor, ref info);

        int x = info.rcWork.Left + (info.rcWork.Width - width) / 2;
        int y = info.rcWork.Bottom - height - (int)Math.Round(BottomMarginDip * scale);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsService.Theme):
            case nameof(SettingsService.BackgroundStyle):
            case nameof(SettingsService.CustomBackgroundColor):
            case nameof(SettingsService.FillStyle):
            case nameof(SettingsService.CustomFillColor):
                DispatcherQueue.TryEnqueue(ApplyAppearance);
                break;
        }
    }

    public void ApplyAppearance()
    {
        Root.RequestedTheme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        var isDark = Root.RequestedTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
        };

        // Chrome approximating the native Win11 flyout resources.
        var stroke = isDark ? Color.FromArgb(0x17, 255, 255, 255) : Color.FromArgb(0x12, 0, 0, 0);
        var track = isDark ? Color.FromArgb(0x26, 255, 255, 255) : Color.FromArgb(0x26, 0, 0, 0);
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];

        Panel.BorderBrush = new SolidColorBrush(stroke);
        TrackBar.Background = new SolidColorBrush(track);

        Panel.Background = _settings.BackgroundStyle == OsdBackgroundStyle.SolidColor
            && TryParseColor(_settings.CustomBackgroundColor, out var background)
                ? new SolidColorBrush(background)
                : new SolidColorBrush(Colors.Transparent);

        FillBar.Background = _settings.FillStyle == OsdFillStyle.CustomColor
            && TryParseColor(_settings.CustomFillColor, out var fill)
                ? new SolidColorBrush(fill)
                : new SolidColorBrush(accent);
    }

    private static bool TryParseColor(string text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim().TrimStart('#');
        try
        {
            if (text.Length == 8) // AARRGGBB
            {
                var value = Convert.ToUInt32(text, 16);
                color = Color.FromArgb(
                    (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
                return true;
            }

            if (text.Length == 6) // RRGGBB
            {
                var value = Convert.ToUInt32(text, 16);
                color = Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
                return true;
            }
        }
        catch
        {
            // fall through
        }

        return false;
    }
}
