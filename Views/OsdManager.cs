using System;
using System.Collections.Generic;
using System.Linq;
using BrightFlyout.Services;
using BrightFlyout.Services.Displays;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace BrightFlyout.Views;

/// <summary>
/// Owns the OSD window(s). Placement "Primary" keeps a single flyout on the primary
/// display; "AllDisplays" shows one flyout per connected display. The first window
/// also serves as the raw-input message target.
/// </summary>
public sealed class OsdManager
{
    private readonly SettingsService _settings;
    private readonly List<OsdWindow> _windows = [];
    private DisplayManager? _displays;
    private ElementTheme _theme = ElementTheme.Default;
    private int _hiddenCount;

    /// <summary>Fired when every visible OSD window has auto-hidden.</summary>
    public event Action? AutoHidden;

    public OsdManager(SettingsService settings)
    {
        _settings = settings;
        EnsureWindows();
    }

    /// <summary>HWND of the first OSD window (raw-input message target).</summary>
    internal Windows.Win32.Foundation.HWND Hwnd => _windows[0].Hwnd;

    /// <summary>DispatcherQueue of the first OSD window.</summary>
    public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue => _windows[0].DispatcherQueue;

    public void BindDisplays(DisplayManager displays)
    {
        _displays = displays;
        foreach (var window in _windows)
        {
            window.BindDisplays(displays);
        }
    }

    public void SetTheme(ElementTheme theme)
    {
        _theme = theme;
        foreach (var window in _windows)
        {
            window.SetTheme(theme);
        }
    }

    public void RefreshDisplays()
    {
        foreach (var window in _windows)
        {
            window.RefreshDisplays();
        }
    }

    public void ShowBrightness(int value)
    {
        EnsureWindows();

        _hiddenCount = 0;
        foreach (var window in _windows)
        {
            window.ShowBrightness(value);
        }
    }

    private void EnsureWindows()
    {
        bool all = _settings.OsdPlacement == OsdPlacement.AllDisplays;

        List<Windows.Graphics.RectInt32> areas;
        try
        {
            areas = all
                ? DisplayArea.FindAll().Select(a => a.WorkArea).ToList()
                : [DisplayArea.Primary.WorkArea];
        }
        catch (Exception ex)
        {
            // DisplayArea can fail on Windows 10; fall back to the primary work area.
            Log.Write("DisplayArea unavailable, using primary work area fallback", ex);
            areas = [GetPrimaryWorkArea()];
            all = false;
        }

        if (_windows.Count == areas.Count)
        {
            return;
        }

        foreach (var window in _windows)
        {
            window.AutoHidden -= OnWindowAutoHidden;
            window.Close();
        }

        _windows.Clear();

        foreach (var area in areas)
        {
            var window = new OsdWindow(_settings, area);
            if (_displays is not null)
            {
                window.BindDisplays(_displays);
            }

            window.SetTheme(_theme);
            window.AutoHidden += OnWindowAutoHidden;
            _windows.Add(window);
        }
    }

    private void OnWindowAutoHidden()
    {
        _hiddenCount++;
        if (_hiddenCount >= _windows.Count)
        {
            AutoHidden?.Invoke();
        }
    }

    private static unsafe Windows.Graphics.RectInt32 GetPrimaryWorkArea()
    {
        Windows.Win32.Foundation.RECT workArea;
        Windows.Win32.PInvoke.SystemParametersInfo(
            Windows.Win32.UI.WindowsAndMessaging.SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETWORKAREA,
            0, &workArea, default);
        return new Windows.Graphics.RectInt32(
            workArea.left, workArea.top, workArea.right - workArea.left, workArea.bottom - workArea.top);
    }
}
