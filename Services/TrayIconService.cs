using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BrightFlyout.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace BrightFlyout.Services;

/// <summary>
/// Minimal, dependency-free tray icon: a message-only window + Shell_NotifyIcon
/// (uID-based registration, no GUID table involvement) + a native popup menu.
/// </summary>
internal sealed unsafe class TrayIconService : IDisposable
{
    private const int TrayIconId = 1;
    private static readonly uint WmTrayIcon = PInvoke.WM_USER + 1;

    private const int CmdSettings = 1;
    private const int CmdExit = 2;

    // One instance per process; the (unmanaged) WndProc cannot capture state.
    private static TrayIconService? _instance;
    private static ushort _classAtom;
    private static uint _wmTaskbarCreated;

    private readonly HWND _hwnd;
    private readonly HICON _hIcon;
    private string _tooltip;
    private bool _iconAdded;
    private HDEVNOTIFY _monitorNotify;

    /// <summary>The message-only window backing the tray icon. Lives for the whole
    /// process (unlike OSD windows, which are recreated on topology changes), so it
    /// doubles as the durable target for raw input and broadcast messages.</summary>
    internal HWND Hwnd => _hwnd;

    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    /// <summary>Raised on the UI thread for WM_INPUT delivered to the tray window.</summary>
    public event Action<LPARAM>? RawInputMessage;

    /// <summary>Raised on the UI thread on WM_DISPLAYCHANGE or monitor
    /// device-interface arrival/removal (input-source switches included).</summary>
    public event Action? DisplayChangeBroadcast;

    public TrayIconService(string tooltip, HICON hIcon)
    {
        _tooltip = tooltip;
        _hIcon = hIcon;
        _instance = this;

        _hwnd = CreateMessageWindow();
        RegisterMonitorNotifications();
        AddIcon();
    }

    /// <summary>Monitor input-source switches and cable events surface as monitor
    /// device-interface arrival/removal - sometimes without a WM_DISPLAYCHANGE.</summary>
    private void RegisterMonitorNotifications()
    {
        if (_hwnd.IsNull)
        {
            return;
        }

        var filter = new DEV_BROADCAST_DEVICEINTERFACE_W
        {
            dbcc_size = (uint)Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE_W>(),
            dbcc_devicetype = (uint)DEV_BROADCAST_HDR_DEVICE_TYPE.DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = PInvoke.GUID_DEVINTERFACE_MONITOR,
        };

        _monitorNotify = PInvoke.RegisterDeviceNotification(_hwnd, &filter, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_monitorNotify.IsNull)
        {
            Log.Write($"RegisterDeviceNotification failed, win32={Marshal.GetLastWin32Error()}");
        }
    }

    private static bool IsMonitorDeviceChange(WPARAM wParam, LPARAM lParam)
    {
        var changeType = (uint)wParam.Value;
        if (changeType is not (PInvoke.DBT_DEVICEARRIVAL or PInvoke.DBT_DEVICEREMOVECOMPLETE) || lParam.Value == 0)
        {
            return false;
        }

        var header = (DEV_BROADCAST_HDR*)lParam.Value;
        return header->dbch_devicetype == DEV_BROADCAST_HDR_DEVICE_TYPE.DBT_DEVTYP_DEVICEINTERFACE
            && ((DEV_BROADCAST_DEVICEINTERFACE_W*)header)->dbcc_classguid == PInvoke.GUID_DEVINTERFACE_MONITOR;
    }

    public void SetToolTip(string tooltip)
    {
        _tooltip = tooltip;
        if (_iconAdded)
        {
            Modify(NOTIFY_ICON_DATA_FLAGS.NIF_TIP);
        }
    }

    /// <summary>Shows a balloon/toast notification (once per call).</summary>
    public void ShowNotification(string title, string text)
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateData(NOTIFY_ICON_DATA_FLAGS.NIF_INFO);
        Win32.WriteFixedString(data.szInfoTitle.AsSpan(), title);
        Win32.WriteFixedString(data.szInfo.AsSpan(), text);
        data.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    #region Shell_NotifyIcon plumbing

    private NOTIFYICONDATAW CreateData(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = flags,
            uCallbackMessage = WmTrayIcon,
            hIcon = _hIcon,
        };
        Win32.WriteFixedString(data.szTip.AsSpan(), _tooltip);
        return data;
    }

    private void AddIcon()
    {
        var data = CreateData(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP);
        if (PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
        {
            data.uVersion = 4; // NOTIFYICON_VERSION_4
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
            _iconAdded = true;
        }
        else
        {
            Log.Write($"Shell_NotifyIcon(NIM_ADD) failed, win32={Marshal.GetLastWin32Error()}");
        }
    }

    private void Modify(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = CreateData(flags);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    private void RemoveIcon()
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateData(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
        _iconAdded = false;
    }

    #endregion

    #region Message window

    private HWND CreateMessageWindow()
    {
        const string className = "BrightFlyoutTrayWindow";

        if (_classAtom == 0)
        {
            _wmTaskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
            var hInstance = new HINSTANCE(PInvoke.GetModuleHandle(default(PCWSTR)).Value);

            fixed (char* classNamePtr = className)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = &StaticWndProc,
                    hInstance = hInstance,
                    lpszClassName = new PCWSTR(classNamePtr),
                };

                _classAtom = PInvoke.RegisterClassEx(wc);
            }

            if (_classAtom == 0)
            {
                Log.Write($"RegisterClassEx failed, win32={Marshal.GetLastWin32Error()}");
            }
        }

        HWND hwnd;
        fixed (char* classNamePtr = className)
        {
            hwnd = PInvoke.CreateWindowEx(0, new PCWSTR(classNamePtr), new PCWSTR(classNamePtr), 0, 0, 0, 0, 0,
                HWND.HWND_MESSAGE, default, PInvoke.GetModuleHandle(default(PCWSTR)), null);
        }
        if (hwnd.IsNull)
        {
            Log.Write($"Tray message window creation failed, win32={Marshal.GetLastWin32Error()}");
        }

        return hwnd;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT StaticWndProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == WmTrayIcon)
        {
            // With NOTIFYICON_VERSION_4, lParam = LOWORD(mouse message) +
            // HIWORD(icon id) - mask off the icon id.
            _instance?.OnTrayMessage((int)(lParam.Value & 0xFFFF));
            return (LRESULT)0;
        }

        if (msg == PInvoke.WM_INPUT)
        {
            _instance?.RawInputMessage?.Invoke(lParam);
            return (LRESULT)0;
        }

        if (msg == PInvoke.WM_DISPLAYCHANGE)
        {
            _instance?.DisplayChangeBroadcast?.Invoke();
            return PInvoke.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        if (msg == PInvoke.WM_DEVICECHANGE && IsMonitorDeviceChange(wParam, lParam))
        {
            _instance?.DisplayChangeBroadcast?.Invoke();
            return PInvoke.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        if (msg == _wmTaskbarCreated && _wmTaskbarCreated != 0)
        {
            // Explorer restarted: re-register the icon.
            _instance?.AddIcon();
            return (LRESULT)0;
        }

        return PInvoke.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnTrayMessage(int mouseMessage)
    {
        switch ((uint)mouseMessage)
        {
            case PInvoke.WM_LBUTTONDBLCLK:
                SettingsRequested?.Invoke();
                break;
            case PInvoke.WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull)
        {
            return;
        }

        AppendMenuText(menu, MENU_ITEM_FLAGS.MF_STRING, CmdSettings, "Settings");
        AppendMenuText(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
        AppendMenuText(menu, MENU_ITEM_FLAGS.MF_STRING, CmdExit, "Exit");

        // Required ritual for popup menus owned by message-only windows.
        PInvoke.SetForegroundWindow(_hwnd);
        PInvoke.GetCursorPos(out var cursor);
        var command = PInvoke.TrackPopupMenu(menu,
            TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON,
            cursor.X, cursor.Y, 0, _hwnd, null);
        PInvoke.PostMessage(_hwnd, PInvoke.WM_NULL, default, default);
        PInvoke.DestroyMenu(menu);

        OnMenuCommand(command.Value);
    }

    private static void AppendMenuText(HMENU menu, MENU_ITEM_FLAGS flags, nuint id, string? text)
    {
        fixed (char* textPtr = text)
        {
            PInvoke.AppendMenu(menu, flags, id, new PCWSTR(textPtr));
        }
    }

    private void OnMenuCommand(int command)
    {
        switch (command)
        {
            case CmdSettings:
                SettingsRequested?.Invoke();
                break;
            case CmdExit:
                ExitRequested?.Invoke();
                break;
        }
    }

    #endregion

    public void Dispose()
    {
        RemoveIcon();

        if (!_monitorNotify.IsNull)
        {
            PInvoke.UnregisterDeviceNotification(_monitorNotify);
        }

        if (!_hwnd.IsNull)
        {
            PInvoke.DestroyWindow(_hwnd);
        }

        PInvoke.DestroyIcon(_hIcon);
        _instance = null;
    }
}
