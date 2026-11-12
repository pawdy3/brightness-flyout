using System.Runtime.InteropServices;
using BrightFlyout.Native;

namespace BrightFlyout.Services;

public enum BrightnessDirection
{
    Down = -1,
    Up = 1
}

/// <summary>
/// Detects brightness key presses through two complementary paths:
///
/// 1. A low-level keyboard hook (WH_KEYBOARD_LL) watching configurable virtual key
///    codes. Key presses are swallowed so the shell never sees them.
///
/// 2. Raw Input on the HID consumer page (0x0C), which is where keyboards report the
///    standardized "Display Brightness Increment/Decrement" usages (0x006F / 0x0070).
///    These usages never surface as virtual keys, so the hook alone cannot see them.
///    Raw Input is notification-only, which is fine: desktop Windows does not act on
///    these usages by itself.
/// </summary>
internal sealed class KeyboardHookService : IDisposable
{
    private readonly SettingsService _settings;

    private IntPtr _hookHandle;
    private IntPtr _rawInputHwnd;
    private bool _disposed;

    // Keep delegates rooted for the lifetime of the hook/subclass.
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private NativeMethods.SubclassProc? _subclassProc;

    private readonly Dictionary<IntPtr, byte[]> _preparsedDataCache = new();

    // Auto-repeat state for held keys (raw input path sends press/release only).
    private BrightnessDirection? _heldDirection;
    private readonly System.Threading.Timer _repeatTimer;
    private DateTime _pressStartUtc;

    /// <summary>Fired on a thread-pool thread whenever a brightness step is requested.</summary>
    public event Action<BrightnessDirection>? BrightnessStepRequested;

    public KeyboardHookService(SettingsService settings)
    {
        _settings = settings;
        _repeatTimer = new System.Threading.Timer(OnRepeatTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Install(IntPtr rawInputTargetHwnd)
    {
        _rawInputHwnd = rawInputTargetHwnd;

        _hookProc = LowLevelKeyboardProc;
        _hookHandle = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_KEYBOARD_LL, _hookProc, NativeMethods.GetModuleHandleW(null), 0);

        if (_hookHandle == IntPtr.Zero)
        {
            Log.Write($"SetWindowsHookEx failed, win32={Marshal.GetLastWin32Error()}");
        }
        else
        {
            Log.Write($"Keyboard hook installed (0x{_hookHandle.ToInt64():X})");
        }

        AttachRawInput();
        SetRawInputEnabled(_settings.EnableRawInput);
    }

    /// <summary>Enables/disables Raw Input listening at runtime (no restart needed).</summary>
    public void SetRawInputEnabled(bool enabled)
    {
        if (_rawInputHwnd == IntPtr.Zero)
        {
            return;
        }

        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_CONSUMER,
                usUsage = NativeMethods.HID_USAGE_CONSUMER_CONTROL,
                dwFlags = enabled ? NativeMethods.RIDEV_INPUTSINK : NativeMethods.RIDEV_REMOVE,
                hwndTarget = enabled ? _rawInputHwnd : IntPtr.Zero
            }
        };

        NativeMethods.RegisterRawInputDevices(devices, 1, Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());
    }

    #region WH_KEYBOARD_LL

    private IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN
                or NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                var downVk = SettingsService.ParseVkCode(_settings.BrightnessDownVk, -1);
                var upVk = SettingsService.ParseVkCode(_settings.BrightnessUpVk, -1);
                bool isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;

                if (data.vkCode == downVk || data.vkCode == upVk)
                {
                    // Swallow the key-up too: passing it through alone would leak a
                    // key release for a press the shell never saw.
                    if (isDown)
                    {
                        Dispatch(data.vkCode == downVk ? BrightnessDirection.Down : BrightnessDirection.Up);
                    }

                    return (IntPtr)1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    #endregion

    #region Raw Input (HID consumer control 0x006F / 0x0070)

    private void AttachRawInput()
    {
        // Subclass once; WM_INPUT only arrives while the raw-input registration is active.
        _subclassProc = RawInputSubclassProc;
        NativeMethods.SetWindowSubclass(_rawInputHwnd, _subclassProc, (UIntPtr)1, UIntPtr.Zero);
    }

    private IntPtr RawInputSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_INPUT)
        {
            HandleRawInput(lParam);
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
        uint size = 0;

        if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, buffer, ref size, headerSize) != size)
            {
                return;
            }

            var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(buffer);

            // RAWHID (dwSizeHid, dwCount) immediately follows the header; bRawData follows RAWHID.
            int dwSizeHid = Marshal.ReadInt32(buffer, (int)headerSize);
            int dwCount = Marshal.ReadInt32(buffer, (int)headerSize + 4);
            int reportLength = dwSizeHid * dwCount;
            if (reportLength <= 0 || reportLength > 1024)
            {
                return;
            }

            var report = new byte[reportLength];
            Marshal.Copy(buffer + (int)headerSize + 8, report, 0, reportLength);

            if (!TryGetPreparsedData(header.hDevice, out var preparsed))
            {
                return;
            }

            var usages = new ushort[32];
            int usageCount = usages.Length;
            int status = NativeMethods.HidP_GetUsages(
                NativeMethods.HIDP_INPUT, NativeMethods.HID_USAGE_PAGE_CONSUMER, 0,
                usages, ref usageCount, preparsed, report, reportLength);
            if (status != NativeMethods.HIDP_STATUS_SUCCESS)
            {
                return;
            }

            bool up = Contains(usages, usageCount, NativeMethods.HID_USAGE_BRIGHTNESS_INCREMENT);
            bool down = Contains(usages, usageCount, NativeMethods.HID_USAGE_BRIGHTNESS_DECREMENT);

            if (up)
            {
                OnPress(BrightnessDirection.Up);
            }
            else if (down)
            {
                OnPress(BrightnessDirection.Down);
            }
            else if (usageCount == 0)
            {
                OnRelease();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool Contains(ushort[] usages, int count, ushort usage)
    {
        for (int i = 0; i < count; i++)
        {
            if (usages[i] == usage)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetPreparsedData(IntPtr hDevice, out byte[] preparsed)
    {
        lock (_preparsedDataCache)
        {
            if (_preparsedDataCache.TryGetValue(hDevice, out preparsed!))
            {
                return true;
            }

            uint size = 0;
            NativeMethods.GetRawInputDeviceInfoW(hDevice, NativeMethods.RIDI_PREPARSEDDATA, null, ref size);
            if (size == 0)
            {
                preparsed = [];
                return false;
            }

            var data = new byte[size];
            if (NativeMethods.GetRawInputDeviceInfoW(hDevice, NativeMethods.RIDI_PREPARSEDDATA, data, ref size) != size)
            {
                preparsed = [];
                return false;
            }

            _preparsedDataCache[hDevice] = data;
            preparsed = data;
            return true;
        }
    }

    #endregion

    #region Press handling / auto-repeat

    private void OnPress(BrightnessDirection direction)
    {
        _pressStartUtc = DateTime.UtcNow;

        if (_heldDirection != direction)
        {
            _heldDirection = direction;
            Dispatch(direction);
            // HID re-trigger: emulate the OS key-repeat behavior while held.
            _repeatTimer.Change(450, 80);
        }
        // else: same usage re-reported by the device; the repeat timer already covers it.
    }

    private void OnRelease()
    {
        _heldDirection = null;
        _repeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnRepeatTick(object? state)
    {
        if (_heldDirection is { } direction && DateTime.UtcNow - _pressStartUtc < TimeSpan.FromSeconds(10))
        {
            Dispatch(direction);
        }
        else
        {
            _repeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void Dispatch(BrightnessDirection direction) =>
        ThreadPool.QueueUserWorkItem(_ => BrightnessStepRequested?.Invoke(direction));

    #endregion

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _repeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _repeatTimer.Dispose();

        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        if (_subclassProc is not null && _rawInputHwnd != IntPtr.Zero)
        {
            NativeMethods.RemoveWindowSubclass(_rawInputHwnd, _subclassProc, (UIntPtr)1);
            _subclassProc = null;
        }
    }
}
