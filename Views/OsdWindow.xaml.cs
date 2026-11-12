using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BrightFlyout.Services;
using BrightFlyout.Services.Displays;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

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
    private const double BottomMarginDip = 24;

    // Expanded-panel layout: per-display rows, then the sync toggle row, padded.
    private const double RowHeightDip = 34;
    private const double RowSpacingDip = 4;
    private const double SyncRowHeightDip = 32;
    private const double ExpandedPaddingDip = 14;

    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly HWND _hwnd;
    private readonly AppWindow _appWindow;

    private readonly RectInt32 _workArea;
    private DisplayManager? _displays;
    private string _rowsFingerprint = string.Empty;
    private bool _isDark = true;
    private bool _acrylicAvailable = true;
    private bool _isExpanded;
    private bool _pointerInside;

    internal HWND Hwnd => _hwnd;

    /// <summary>Fired when the OSD auto-hides after its timeout.</summary>
    public event Action? AutoHidden;

    public unsafe OsdWindow(SettingsService settings, RectInt32 workArea)
    {
        _settings = settings;
        _workArea = workArea;
        InitializeComponent();

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            // DesktopAcrylicBackdrop is unavailable on some Windows 10 builds;
            // ApplyAppearance falls back to a solid panel background.
            _acrylicAvailable = false;
        }

        _hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this));
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
        PInvoke.SetWindowSubclass(_hwnd, &FrameSubclassProc, 2, 0);

        // Keep it out of Alt+Tab and never let it take focus.
        var exStyle = (uint)PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            (int)(exStyle | (uint)(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE)));

        // Rounded window corners on Windows 11.
        var cornerPreference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
            &cornerPreference, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));

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

            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
            AutoHidden?.Invoke();
        };

        _settings.PropertyChanged += OnSettingsChanged;
        ApplyAppearance();

        // WinUI shows new windows by default; keep the OSD hidden until needed.
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);

        Closed += (_, _) => PInvoke.RemoveWindowSubclass(_hwnd, &FrameSubclassProc, 2);
    }

    /// <summary>Wires the display list into the expanded view. Called once after startup.</summary>
    public void BindDisplays(DisplayManager displays)
    {
        _displays = displays;
        SyncToggle.IsOn = _settings.SyncMonitors;
        RebuildDisplayRows();
        UpdateExpandButtonVisibility();
    }

    // Rows are DisplayRowControl UserControls added to ItemsControl.Items in code:
    // passing managed collections via ItemsSource crosses the WinRT ABI and breaks
    // NativeAOT, while adding UI elements is safe. Row content stays declarative.
    private void RebuildDisplayRows()
    {
        var fingerprint = _displays is null
            ? string.Empty
            : string.Join("|", _displays.IncludedDisplays.Select(d => d.Id));
        if (fingerprint == _rowsFingerprint)
        {
            return;
        }

        _rowsFingerprint = fingerprint;
        DisplayItems.Items.Clear();
        if (_displays is null)
        {
            return;
        }

        foreach (var display in _displays.IncludedDisplays)
        {
            DisplayItems.Items.Add(new DisplayRowControl
            {
                Display = display,
                SlidersEnabled = !_settings.SyncMonitors
            });
        }
    }

    private void UpdateRowSliderStates()
    {
        foreach (var row in DisplayItems.Items.OfType<DisplayRowControl>())
        {
            row.SlidersEnabled = !_settings.SyncMonitors;
        }
    }

    /// <summary>Called when the display list or a display's value changed.</summary>
    public void RefreshDisplays()
    {
        RebuildDisplayRows();
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
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNA);

        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(_settings.OsdDurationMs);
        _hideTimer.Start();
    }

    public void UpdateValue(int value)
    {
        value = Math.Clamp(value, 0, 100);
        FillScale.ScaleX = value / 100.0;
        PercentText.Text = $"{value}%";
        UpdateThumbPosition();
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

    private void OnSyncToggled(object sender, RoutedEventArgs e)
    {
        var on = SyncToggle.IsOn;
        if (_settings.SyncMonitors != on)
        {
            _settings.SyncMonitors = on;
        }

        UpdateRowSliderStates();
        if (on)
        {
            _displays?.SynchronizeAll();
        }
    }

    /// <summary>Keeps the sync toggle in sync when changed from elsewhere.</summary>
    public void SyncToggleState()
    {
        SyncToggle.IsOn = _settings.SyncMonitors;
        UpdateRowSliderStates();
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

    #region Clickable bar (optional)

    private bool _barDragging;
    private bool _pointerOverTrack;

    private void OnTrackPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_settings.ClickableFlyoutBar)
        {
            return;
        }

        _barDragging = true;
        TrackArea.CapturePointer(e.Pointer);
        SetBarFromPointer(e);
        UpdateThumbVisibility();
        e.Handled = true;
    }

    private void OnTrackPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_barDragging)
        {
            SetBarFromPointer(e);
        }
    }

    private void OnTrackPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _barDragging = false;
        UpdateThumbVisibility();
    }

    private void OnTrackPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOverTrack = true;
        UpdateThumbVisibility();
    }

    private void OnTrackPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOverTrack = false;
        UpdateThumbVisibility();
    }

    private void UpdateThumbVisibility()
    {
        // The dot mirrors the expanded sliders' thumbs; show it only when the bar
        // is interactive (setting on) and hovered or dragged.
        TrackThumb.Visibility = _settings.ClickableFlyoutBar && (_pointerOverTrack || _barDragging)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnTrackAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdateThumbPosition();

    private void UpdateThumbPosition()
    {
        var travel = TrackArea.ActualWidth - TrackThumb.Width;
        if (travel > 0)
        {
            TrackThumb.Margin = new Thickness(FillScale.ScaleX * travel, 0, 0, 0);
        }
    }

    private void SetBarFromPointer(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var fraction = Math.Clamp(
            e.GetCurrentPoint(TrackArea).Position.X / TrackArea.ActualWidth, 0, 1);
        var value = (int)Math.Round(fraction * 100);

        if (_settings.SyncMonitors)
        {
            _displays?.SetAllIncluded(value);
        }
        else
        {
            _displays?.Target?.SetBrightness(value);
        }

        UpdateValue(value);
    }

    #endregion

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT FrameSubclassProc(HWND hWnd, uint uMsg, WPARAM wParam, LPARAM lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == PInvoke.WM_NCCALCSIZE && wParam.Value != 0)
        {
            // wParam == TRUE: returning 0 makes the client area cover the whole
            // window, leaving DWM no non-client frame to paint.
            return (LRESULT)0;
        }

        return PInvoke.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ResizeAndReposition()
    {
        double scale = PInvoke.GetDpiForWindow(_hwnd) / 96.0;
        int width = (int)Math.Round(WidthDip * scale);

        double heightDip = CollapsedHeightDip;
        if (_isExpanded)
        {
            int rows = _displays?.Displays.Count ?? 0;
            heightDip += rows * (RowHeightDip + RowSpacingDip) + SyncRowHeightDip + ExpandedPaddingDip;
        }

        int height = (int)Math.Round(heightDip * scale);

        int x = _workArea.X + (_workArea.Width - width) / 2;
        int y = _workArea.Y + _workArea.Height - height - (int)Math.Round(BottomMarginDip * scale);
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
            case nameof(SettingsService.SyncMonitors):
                DispatcherQueue.TryEnqueue(SyncToggleState);
                break;
        }
    }

    public void ApplyAppearance()
    {
        // No stroke at all: any border brush (even near-black) reads as a light
        // outline against the acrylic, and the native OSD has no visible border.
        Panel.BorderThickness = new Thickness(0);
        Panel.BorderBrush = new SolidColorBrush(Colors.Transparent);

        TrackBar.Background = new SolidColorBrush(
            _isDark ? Color.FromArgb(0x26, 255, 255, 255) : Color.FromArgb(0x26, 0, 0, 0));

        // Without an acrylic backdrop the panel must be opaque; with acrylic the
        // default background is transparent so the backdrop shows through.
        var fallbackBackground = _acrylicAvailable
            ? Colors.Transparent
            : _isDark ? Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F) : Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9);

        Panel.Background = _settings.BackgroundStyle == OsdBackgroundStyle.SolidColor
            ? new SolidColorBrush(ColorHex.ParseOrDefault(_settings.CustomBackgroundColor, fallbackBackground))
            : new SolidColorBrush(fallbackBackground);

        FillBar.Background = new SolidColorBrush(
            _settings.FillStyle == OsdFillStyle.CustomColor
                ? ColorHex.ParseOrDefault(_settings.CustomFillColor, SystemAccent)
                : SystemAccent);

        PercentText.Visibility = _settings.ShowPercentText ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Color SystemAccent =>
        (Color)Application.Current.Resources["SystemAccentColor"];
}
