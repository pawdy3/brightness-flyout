using BrightFlyout.Native;

namespace BrightFlyout.Services;

/// <summary>
/// Temporarily hides the built-in Windows shell OSD (volume/brightness flyout) while
/// our own OSD is visible, then restores it. Based on the approach from FluentFlyout
/// (which in turn derives from gpkgpk/HideVolumeOSD): the Win11 OSD lives in an
/// explorer-hosted XAML island ("XamlExplorerHostIslandWindow").
///
/// Suppression is always transient - the native OSD is restored a moment after the
/// last key press and unconditionally on app shutdown.
/// </summary>
internal static class NativeOsdSuppressor
{
    private static IntPtr _nativeOsdElement = IntPtr.Zero;
    private static long _originalExStyle;
    private static readonly List<IntPtr> _legacyOsdElements = [];
    private static bool _loggedLegacySearch;

    public static bool IsSuppressed => _nativeOsdElement != IntPtr.Zero || _legacyOsdElements.Count > 0;

    public static void Suppress()
    {
        // Strictly OS-branched: no fallback mixing between the two host models.
        if (App.IsWindows11)
        {
            SuppressWindows11Osd();
        }
        else
        {
            SuppressLegacyOsd();
        }
    }

    /// <summary>Windows 10 and earlier: the OSD is a DirectUI popup hosted in a
    /// "NativeHWNDHost" window (the gpkgpk/HideVolumeOSD approach). There may be
    /// several such hosts; hide all that contain a DirectUIHWND child.</summary>
    private static void SuppressLegacyOsd()
    {
        IntPtr hwnd = IntPtr.Zero;
        while ((hwnd = NativeMethods.FindWindowExW(IntPtr.Zero, hwnd, "NativeHWNDHost", null)) != IntPtr.Zero)
        {
            if (_legacyOsdElements.Contains(hwnd)
                || NativeMethods.FindWindowExW(hwnd, IntPtr.Zero, "DirectUIHWND", null) == IntPtr.Zero)
            {
                continue;
            }

            _legacyOsdElements.Add(hwnd);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }

        if (_legacyOsdElements.Count == 0 && !_loggedLegacySearch)
        {
            _loggedLegacySearch = true;
            Log.Write("Legacy OSD host (NativeHWNDHost/DirectUIHWND) not found");
        }
    }

    /// <summary>Windows 11 22H2+: the OSD lives in an explorer-hosted XAML island.</summary>
    private static bool SuppressWindows11Osd()
    {
        if (_nativeOsdElement != IntPtr.Zero)
        {
            return true; // already hidden
        }


        // Walk the XAML islands; FindWindowEx must be given the previous handle as
        // hwndChildAfter, otherwise it returns the same first island forever.
        IntPtr hwndXamlIsland = IntPtr.Zero;
        IntPtr hwndOsd = IntPtr.Zero;
        while ((hwndXamlIsland = NativeMethods.FindWindowExW(IntPtr.Zero, hwndXamlIsland, "XamlExplorerHostIslandWindow", null)) != IntPtr.Zero)
        {
            hwndOsd = NativeMethods.FindWindowExW(hwndXamlIsland, IntPtr.Zero,
                "Windows.UI.Composition.DesktopWindowContentBridge", "DesktopWindowXamlSource");
            if (hwndOsd == IntPtr.Zero)
            {
                continue;
            }

            var hwndInputClass = NativeMethods.FindWindowExW(hwndOsd, IntPtr.Zero,
                "Windows.UI.Input.InputSite.WindowClass", null);
            if (hwndInputClass == IntPtr.Zero)
            {
                hwndOsd = IntPtr.Zero;
                continue;
            }

            NativeMethods.ShowWindow(hwndInputClass, NativeMethods.SW_SHOWNOACTIVATE);
            if (NativeMethods.GetWindowRect(hwndInputClass, out var rect))
            {
                if (rect is { Top: 0, Left: 0, Bottom: 0, Right: 0 })
                {
                    hwndOsd = IntPtr.Zero;
                }
                else
                {
                    break;
                }
            }
        }

        if (hwndOsd == IntPtr.Zero)
        {
            return false; // OSD window not found (nothing to suppress right now)
        }

        _nativeOsdElement = hwndXamlIsland;
        _originalExStyle = NativeMethods.GetWindowLongPtr(_nativeOsdElement, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(_nativeOsdElement, NativeMethods.GWL_EXSTYLE,
            _originalExStyle | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT);
        NativeMethods.SetWindowPos(_nativeOsdElement, IntPtr.Zero, -99999, -99999, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        // Minimize without activating: plain SW_MINIMIZE would make Windows move
        // foreground focus to whatever is next in the Z-order.
        NativeMethods.ShowWindow(_nativeOsdElement, NativeMethods.SW_SHOWMINNOACTIVE);
        return true;
    }

    public static void Restore()
    {
        foreach (var hwnd in _legacyOsdElements)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }

        _legacyOsdElements.Clear();

        if (_nativeOsdElement == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowLongPtr(_nativeOsdElement, NativeMethods.GWL_EXSTYLE, _originalExStyle);
        NativeMethods.SetWindowPos(_nativeOsdElement, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        // Restore without activating: SW_RESTORE would steal foreground focus.
        NativeMethods.ShowWindow(_nativeOsdElement, NativeMethods.SW_SHOWNOACTIVATE);
        _nativeOsdElement = IntPtr.Zero;
    }
}
