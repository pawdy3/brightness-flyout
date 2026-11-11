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

    public static bool IsSuppressed => _nativeOsdElement != IntPtr.Zero;

    public static void Suppress()
    {
        if (_nativeOsdElement != IntPtr.Zero)
        {
            return; // already hidden
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

            NativeMethods.ShowWindow(hwndInputClass, NativeMethods.SW_RESTORE);
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
            return; // OSD window not found (nothing to suppress right now)
        }

        _nativeOsdElement = hwndXamlIsland;
        _originalExStyle = NativeMethods.GetWindowLongPtr(_nativeOsdElement, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(_nativeOsdElement, NativeMethods.GWL_EXSTYLE,
            _originalExStyle | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT);
        NativeMethods.SetWindowPos(_nativeOsdElement, IntPtr.Zero, -99999, -99999, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        NativeMethods.ShowWindow(_nativeOsdElement, NativeMethods.SW_MINIMIZE);
    }

    public static void Restore()
    {
        if (_nativeOsdElement == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowLongPtr(_nativeOsdElement, NativeMethods.GWL_EXSTYLE, _originalExStyle);
        NativeMethods.SetWindowPos(_nativeOsdElement, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        NativeMethods.ShowWindow(_nativeOsdElement, NativeMethods.SW_RESTORE);
        _nativeOsdElement = IntPtr.Zero;
    }
}
