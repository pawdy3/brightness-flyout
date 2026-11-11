using System.ComponentModel;
using BrightFlyout.Native;
using BrightFlyout.Services;
using BrightFlyout.Services.Displays;
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
/// In multi-display mode it expands to one slider per display.
/// </summary>
public sealed partial class OsdWindow : Window
{
    private const double WidthDip = 360;
    private const double CollapsedHeightDip = 48;
    private const double RowHeightDip = 34;
    private const double BottomMarginDip = 24;

    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    private DisplayManager? _displays;
    private bool _isDark = true;
    private bool _isExpanded;
    private bool _pointerInside;
    private NativeMethods.SubclassProc? _frameSubclassProc;

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

        // The OverlappedPresenter leaves WS_DLGFRAME on the window (and enforces it,
        // reverting SetWindowLongPtr changes). DWM draws that as a bright ring around
        // the flyout. The reliable fix is to remove the non-client area entirely via
        // WM_NCCALCSIZE so the XAML content covers the whole window.
        _frameSubclassProc = FrameSubclassProc;
        NativeMethods.SetWindowSubclass(_hwnd, _frameSubclassProc, (UIntPtr)2, UIntPtr.Zero);

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
            if (_pointerInside)
            {
                // Don't yank the flyout away while the user is dragging a slider.
                _hideTimer.Start();
                return;
            }

            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            AutoHidden?.Invoke();
        };

        _settings.PropertyChanged += OnSettingsChanged;
        ApplyAppearance();

        // WinUI shows new windows by default; keep the OSD hidden until needed.
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    /// <summary>Wires the display list into the expanded view. Called once after startup.</summary>
    public void BindDisplays(DisplayManager displays)
    {
        _displays = displays;
        DisplayItems.ItemsSource = displays.Displays;
        UpdateExpandButtonVisibility();
    }

    /// <summary>Called when the display list or a display's value changed.</summary>
    public void RefreshDisplays()
    {
        UpdateExpandButtonVisibility();

        if (_isExpanded)
        {
            ResizeAndReposition();
        }

        if (_displays?.Target is { } target)
        {
            UpdateValue(target.Brightness);
        }
    }

    /// <summary>Pushes an explicit theme (system theme tracked by App).</summary>
    public void SetTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;
        _isDark = theme == ElementTheme.Dark;
        ApplyAppearance();
    }

    public void ShowBrightness(int value)
    {
        UpdateValue(value);

        ResizeAndReposition();
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNA);

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(_settings.OsdDurationMs);
        _hideTimer.Start();
    }

    public void UpdateValue(int value)
    {
        value = Math.Clamp(value, 0, 100);
        FillScale.ScaleX = value / 100.0;
        PercentText.Text = $"{value}%";
    }


    #region Expand / collapse

    private void OnExpandButtonClick(object sender, RoutedEventArgs e)
    {
        SetExpanded(!_isExpanded);
    }

    private void SetExpanded(bool expanded)
    {
        _isExpanded = expanded;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandIcon.Glyph = expanded ? "\uE70E" : "\uE70D"; // chevron up / down
        ResizeAndReposition();

        if (expanded)
        {
            // Give the user time to reach the sliders.
            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(_settings.OsdDurationMs, 4000));
            _hideTimer.Start();
        }
    }

    private void UpdateExpandButtonVisibility()
    {
        var visible = _settings.MultiMonitorEnabled && (_displays?.Displays.Count ?? 0) > 0;
        ExpandButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible && _isExpanded)
        {
            SetExpanded(false);
        }
    }

    private void OnSyncClick(object sender, RoutedEventArgs e)
    {
        _displays?.SynchronizeAll();
    }

    #endregion

    #region Pointer-aware auto-hide

    private void OnRootPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerInside = true;
    }

    private void OnRootPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerInside = false;
    }

    #endregion

    private IntPtr FrameSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            // wParam == TRUE: returning 0 makes the client area cover the whole
            // window, leaving DWM no non-client frame to paint.
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ResizeAndReposition()
    {
        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        int width = (int)Math.Round(WidthDip * scale);

        double heightDip = CollapsedHeightDip;
        if (_isExpanded)
        {
            int rows = _displays?.Displays.Count ?? 0;
            // rows + spacing + sync button + its margin + panel padding
            heightDip += rows * (RowHeightDip + 4) + 32 + 4 + 10;
        }

        int height = (int)Math.Round(heightDip * scale);

        var workArea = DisplayArea.Primary.WorkArea;
        int x = workArea.X + (workArea.Width - width) / 2;
        int y = workArea.Y + workArea.Height - height - (int)Math.Round(BottomMarginDip * scale);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsService.BackgroundStyle):
            case nameof(SettingsService.CustomBackgroundColor):
            case nameof(SettingsService.FillStyle):
            case nameof(SettingsService.CustomFillColor):
            case nameof(SettingsService.ShowPercentText):
                DispatcherQueue.TryEnqueue(ApplyAppearance);
                break;
            case nameof(SettingsService.MultiMonitorEnabled):
                DispatcherQueue.TryEnqueue(UpdateExpandButtonVisibility);
                break;
        }
    }

    public void ApplyAppearance()
    {
        // No stroke at all: any border brush (even near-black) reads as a light
        // outline against the acrylic, and the native OSD has no visible border.
        Panel.BorderThickness = new Thickness(0);
        Panel.BorderBrush = new SolidColorBrush(Colors.Transparent);

        var track = _isDark ? Color.FromArgb(0x26, 255, 255, 255) : Color.FromArgb(0x26, 0, 0, 0);
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];

        TrackBar.Background = new SolidColorBrush(track);

        Panel.Background = _settings.BackgroundStyle == OsdBackgroundStyle.SolidColor
            && TryParseColor(_settings.CustomBackgroundColor, out var background)
                ? new SolidColorBrush(background)
                : new SolidColorBrush(Colors.Transparent);

        FillBar.Background = _settings.FillStyle == OsdFillStyle.CustomColor
            && TryParseColor(_settings.CustomFillColor, out var fill)
                ? new SolidColorBrush(fill)
                : new SolidColorBrush(accent);

        PercentText.Visibility = _settings.ShowPercentText ? Visibility.Visible : Visibility.Collapsed;
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
