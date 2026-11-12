using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

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
internal static unsafe class NativeOsdSuppressor
{
    private static HWND _nativeOsdElement;
    private static int _originalExStyle;
    private static readonly HashSet<HWND> _legacyOsdElements = [];
    private static bool _loggedLegacySearch;

    // Out-of-context WinEvent hook, armed while suppression is active. Polling
    // alone hides the OSD up to a watchdog interval too late (visible flash);
    // the show-event fires before the window gets to paint.
    private static HWINEVENTHOOK _winEventHook;

    public static bool IsSuppressed => !_nativeOsdElement.IsNull || _legacyOsdElements.Count > 0;

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

        ArmShowEventHook();
    }

    private static void ArmShowEventHook()
    {
        if (!_winEventHook.IsNull)
        {
            return;
        }

        _winEventHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_SHOW, PInvoke.EVENT_OBJECT_SHOW,
            default(HMODULE), &OnWinEvent, 0, 0, PInvoke.WINEVENT_OUTOFCONTEXT);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(HWINEVENTHOOK hWinEventHook, uint eventType, HWND hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || hwnd.IsNull)
        {
            return;
        }

        // Win11: explorer re-shows the island itself.
        if (hwnd == _nativeOsdElement)
        {
            ApplyWin11Suppression();
            return;
        }

        // Win10: the same NativeHWNDHost is re-shown on every key press.
        if (_legacyOsdElements.Contains(hwnd))
        {
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
            return;
        }

        Span<char> className = stackalloc char[64];
        var length = PInvoke.GetClassName(hwnd, className);
        if (length == 0
            || !className[..length].SequenceEqual("NativeHWNDHost")
            || PInvoke.FindWindowEx(hwnd, default, "DirectUIHWND", null).IsNull)
        {
            return;
        }

        if (_legacyOsdElements.Add(hwnd))
        {
            Log.Write($"Legacy OSD host hidden: 0x{(nint)hwnd.Value:X} (show event)");
        }

        PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
    }

    /// <summary>Windows 10 and earlier: the OSD is a DirectUI popup hosted in a
    /// "NativeHWNDHost" window (the gpkgpk/HideVolumeOSD approach). There may be
    /// several such hosts; hide all that contain a DirectUIHWND child.</summary>
    private static void SuppressLegacyOsd()
    {
        HWND hwnd = default;
        while (!(hwnd = PInvoke.FindWindowEx(default, hwnd, "NativeHWNDHost", null)).IsNull)
        {
            if (PInvoke.FindWindowEx(hwnd, default, "DirectUIHWND", null).IsNull)
            {
                continue;
            }

            // Windows 10 keeps the OSD window alive and re-shows the SAME hwnd on
            // every key press - re-hide known hosts on every tick, not just once.
            if (_legacyOsdElements.Add(hwnd))
            {
                Log.Write($"Legacy OSD host hidden: 0x{(nint)hwnd.Value:X}");
            }

            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
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
        if (!_nativeOsdElement.IsNull)
        {
            // Explorer can re-show the island between watchdog ticks; re-apply.
            ApplyWin11Suppression();
            return true;
        }

        // Walk the XAML islands; FindWindowEx must be given the previous handle as
        // hwndChildAfter, otherwise it returns the same first island forever.
        HWND hwndXamlIsland = default;
        HWND hwndOsd = default;
        while (!(hwndXamlIsland = PInvoke.FindWindowEx(default, hwndXamlIsland, "XamlExplorerHostIslandWindow", null)).IsNull)
        {
            hwndOsd = PInvoke.FindWindowEx(hwndXamlIsland, default,
                "Windows.UI.Composition.DesktopWindowContentBridge", "DesktopWindowXamlSource");
            if (hwndOsd.IsNull)
            {
                continue;
            }

            var hwndInputClass = PInvoke.FindWindowEx(hwndOsd, default,
                "Windows.UI.Input.InputSite.WindowClass", null);
            if (hwndInputClass.IsNull)
            {
                hwndOsd = default;
                continue;
            }

            PInvoke.ShowWindow(hwndInputClass, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            if (PInvoke.GetWindowRect(hwndInputClass, out var rect))
            {
                if (rect is { top: 0, left: 0, bottom: 0, right: 0 })
                {
                    hwndOsd = default;
                }
                else
                {
                    break;
                }
            }
        }

        if (hwndOsd.IsNull)
        {
            return false; // OSD window not found (nothing to suppress right now)
        }

        _nativeOsdElement = hwndXamlIsland;
        _originalExStyle = PInvoke.GetWindowLong(_nativeOsdElement, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ApplyWin11Suppression();
        Log.Write($"Win11 OSD island hidden: 0x{(nint)_nativeOsdElement.Value:X}");
        return true;
    }

    private static void ApplyWin11Suppression()
    {
        PInvoke.SetWindowLong(_nativeOsdElement, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            (int)((uint)_originalExStyle | (uint)(WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT)));
        PInvoke.SetWindowPos(_nativeOsdElement, default, -99999, -99999, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        // Minimize without activating: plain SW_MINIMIZE would make Windows move
        // foreground focus to whatever is next in the Z-order.
        PInvoke.ShowWindow(_nativeOsdElement, SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE);
    }

    public static void Restore()
    {
        if (!_winEventHook.IsNull)
        {
            PInvoke.UnhookWinEvent(_winEventHook);
            _winEventHook = default;
        }

        foreach (var hwnd in _legacyOsdElements)
        {
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        }

        _legacyOsdElements.Clear();

        if (_nativeOsdElement.IsNull)
        {
            return;
        }

        PInvoke.SetWindowLong(_nativeOsdElement, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, _originalExStyle);
        PInvoke.SetWindowPos(_nativeOsdElement, default, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        // Restore without activating: SW_RESTORE would steal foreground focus.
        PInvoke.ShowWindow(_nativeOsdElement, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        _nativeOsdElement = default;
    }
}
