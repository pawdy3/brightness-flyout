using BrightFlyout.Native;

namespace BrightFlyout.Services;

/// <summary>
/// Minimal, dependency-free tray icon: a message-only window + Shell_NotifyIcon
/// (uID-based registration, no GUID table involvement) + a native popup menu.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const int TrayIconId = 1;
    private static readonly int WmTrayIcon = NativeMethods.WM_USER + 1;

    private const int CmdSettings = 1;
    private const int CmdExit = 2;

    // One instance per process; the WndProc must stay rooted while the window lives.
    private static TrayIconService? _instance;
    private static NativeMethods.WndProc? _wndProcDelegate;
    private static ushort _classAtom;
    private static uint _wmTaskbarCreated;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private string _tooltip;
    private bool _iconAdded;

    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIconService(string tooltip, IntPtr hIcon)
    {
        _tooltip = tooltip;
        _hIcon = hIcon;
        _instance = this;

        _hwnd = CreateMessageWindow();
        AddIcon();
    }

    public void SetToolTip(string tooltip)
    {
        _tooltip = tooltip;
        if (_iconAdded)
        {
            Modify(NativeMethods.NIF_TIP);
        }
    }

    /// <summary>Shows a balloon/toast notification (once per call).</summary>
    public void ShowNotification(string title, string text)
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateData(NativeMethods.NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NativeMethods.NIIF_INFO;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
    }

    #region Shell_NotifyIcon plumbing

    private NativeMethods.NOTIFYICONDATAW CreateData(int flags)
    {
        var data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = flags,
            uCallbackMessage = WmTrayIcon,
            hIcon = _hIcon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
        return data;
    }

    private void AddIcon()
    {
        var data = CreateData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        if (NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data))
        {
            data.uVersion = 4; // NOTIFYICON_VERSION_4
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
            _iconAdded = true;
        }
        else
        {
            Log.Write($"Shell_NotifyIcon(NIM_ADD) failed, win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
    }

    private void Modify(int flags)
    {
        var data = CreateData(flags);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
    }

    private void RemoveIcon()
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateData(0);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
        _iconAdded = false;
    }

    #endregion

    #region Message window

    private IntPtr CreateMessageWindow()
    {
        const string className = "BrightFlyoutTrayWindow";

        if (_classAtom == 0)
        {
            _wndProcDelegate = StaticWndProc;
            _wmTaskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = NativeMethods.GetModuleHandleW(null),
                lpszClassName = className,
            };

            _classAtom = NativeMethods.RegisterClassExW(ref wc);
            if (_classAtom == 0)
            {
                Log.Write($"RegisterClassEx failed, win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
        }

        var hwnd = NativeMethods.CreateWindowExW(0, className, className, 0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Log.Write($"Tray message window creation failed, win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        return hwnd;
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTrayIcon)
        {
            // With NOTIFYICON_VERSION_4, lParam = LOWORD(mouse message) +
            // HIWORD(icon id) - mask off the icon id.
            _instance?.OnTrayMessage((int)(lParam.ToInt64() & 0xFFFF));
            return IntPtr.Zero;
        }

        if (msg == _wmTaskbarCreated && _wmTaskbarCreated != 0)
        {
            // Explorer restarted: re-register the icon.
            _instance?.AddIcon();
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnTrayMessage(int mouseMessage)
    {
        switch (mouseMessage)
        {
            case NativeMethods.WM_LBUTTONDBLCLK:
                SettingsRequested?.Invoke();
                break;
            case NativeMethods.WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CmdSettings, "Settings");
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CmdExit, "Exit");

        // Required ritual for popup menus owned by message-only windows.
        NativeMethods.SetForegroundWindow(_hwnd);
        NativeMethods.GetCursorPos(out var cursor);
        int command = NativeMethods.TrackPopupMenu(menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY | NativeMethods.TPM_RIGHTBUTTON,
            cursor.X, cursor.Y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        OnMenuCommand(command);
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

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
        }

        NativeMethods.DestroyIcon(_hIcon);
        _instance = null;
    }
}
