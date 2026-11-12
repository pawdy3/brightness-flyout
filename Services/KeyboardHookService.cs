using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BrightFlyout.Native;
using Windows.Win32;
using Windows.Win32.Devices.HumanInterfaceDevice;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

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
internal sealed unsafe class KeyboardHookService : IDisposable
{
    // Unmanaged callbacks (hook/subclass) cannot capture; the service is a singleton.
    private static KeyboardHookService? _current;

    private readonly SettingsService _settings;

    private HHOOK _hookHandle;
    private HWND _rawInputHwnd;
    private bool _disposed;

    private readonly Dictionary<nint, byte[]> _preparsedDataCache = new();

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

    /// <summary>Installs the keyboard hook and (when <paramref name="rawInputTargetHwnd"/>
    /// is non-null) registers raw input against it. The target must be a window that
    /// lives for the whole process (the tray message window) and forwards WM_INPUT
    /// to <see cref="ProcessRawInputMessage"/>.</summary>
    public void Install(HWND rawInputTargetHwnd)
    {
        _rawInputHwnd = rawInputTargetHwnd;
        _current = this;

        _hookHandle = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &LowLevelKeyboardProc,
            new HINSTANCE(PInvoke.GetModuleHandle(default(PCWSTR)).Value), 0);

        if (_hookHandle.IsNull)
        {
            Log.Write($"SetWindowsHookEx failed, win32={Marshal.GetLastWin32Error()}");
        }
        else
        {
            Log.Write($"Keyboard hook installed (0x{(nint)_hookHandle.Value:X})");
        }

        SetRawInputEnabled(_settings.EnableRawInput);
    }

    /// <summary>Handles a WM_INPUT message forwarded from the raw-input target window.</summary>
    public void ProcessRawInputMessage(LPARAM lParam) => HandleRawInput(new HRAWINPUT(lParam.Value));

    /// <summary>Enables/disables Raw Input listening at runtime (no restart needed).</summary>
    public void SetRawInputEnabled(bool enabled)
    {
        if (_rawInputHwnd.IsNull)
        {
            return;
        }

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = Win32.HidUsagePageConsumer,
                usUsage = Win32.HidUsageConsumerControl,
                dwFlags = enabled ? RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK : RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE,
                hwndTarget = enabled ? _rawInputHwnd : default
            }
        };

        PInvoke.RegisterRawInputDevices(devices, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    #region WH_KEYBOARD_LL

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT LowLevelKeyboardProc(int nCode, WPARAM wParam, LPARAM lParam) =>
        _current?.HookProc(nCode, wParam, lParam) ?? PInvoke.CallNextHookEx(default, nCode, wParam, lParam);

    private LRESULT HookProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        if (nCode >= 0)
        {
            var msg = (uint)wParam.Value;
            if (msg is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN
                or PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP)
            {
                var data = *(KBDLLHOOKSTRUCT*)lParam.Value;
                var downVk = SettingsService.ParseVkCode(_settings.BrightnessDownVk, -1);
                var upVk = SettingsService.ParseVkCode(_settings.BrightnessUpVk, -1);
                bool isDown = msg is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;

                if ((int)data.vkCode == downVk || (int)data.vkCode == upVk)
                {
                    // Swallow the key-up too: passing it through alone would leak a
                    // key release for a press the shell never saw.
                    if (isDown)
                    {
                        Dispatch((int)data.vkCode == downVk ? BrightnessDirection.Down : BrightnessDirection.Up);
                    }

                    return (LRESULT)1;
                }
            }
        }

        return PInvoke.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    #endregion

    #region Raw Input (HID consumer control 0x006F / 0x0070)

    private void HandleRawInput(HRAWINPUT hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;

        if (PInvoke.GetRawInputData(hRawInput, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, null, &size, headerSize) != 0 || size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PInvoke.GetRawInputData(hRawInput, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, (void*)buffer, &size, headerSize) != size)
            {
                return;
            }

            var header = *(RAWINPUTHEADER*)buffer;

            // RAWHID (dwSizeHid, dwCount) immediately follows the header; bRawData follows RAWHID.
            int dwSizeHid = *(int*)((byte*)buffer + headerSize);
            int dwCount = *(int*)((byte*)buffer + headerSize + 4);
            int reportLength = dwSizeHid * dwCount;
            if (reportLength <= 0 || reportLength > 1024)
            {
                return;
            }

            var report = new byte[reportLength];
            Marshal.Copy((nint)((byte*)buffer + headerSize + 8), report, 0, reportLength);

            if (!TryGetPreparsedData(header.hDevice, out var preparsed))
            {
                return;
            }

            var usages = new ushort[32];
            uint usageCount = (uint)usages.Length;
            NTSTATUS status;
            fixed (byte* reportPtr = report)
            fixed (byte* preparsedPtr = preparsed)
            {
                status = PInvoke.HidP_GetUsages(
                    HIDP_REPORT_TYPE.HidP_Input, Win32.HidUsagePageConsumer, 0,
                    usages, ref usageCount, new PHIDP_PREPARSED_DATA((nint)preparsedPtr),
                    new PSTR(reportPtr), (uint)reportLength);
            }

            if (status.Value != Win32.HidpStatusSuccess)
            {
                return;
            }

            bool up = Contains(usages, (int)usageCount, Win32.HidUsageBrightnessIncrement);
            bool down = Contains(usages, (int)usageCount, Win32.HidUsageBrightnessDecrement);

            if (up || down)
            {
                Log.Write($"Raw input brightness key: {(up ? "up" : "down")}");
            }

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

    private bool TryGetPreparsedData(HANDLE hDevice, out byte[] preparsed)
    {
        lock (_preparsedDataCache)
        {
            if (_preparsedDataCache.TryGetValue((nint)hDevice.Value, out preparsed!))
            {
                return true;
            }

            uint size = 0;
            PInvoke.GetRawInputDeviceInfo(hDevice, RAW_INPUT_DEVICE_INFO_COMMAND.RIDI_PREPARSEDDATA, null, &size);
            if (size == 0)
            {
                preparsed = [];
                return false;
            }

            var data = new byte[size];
            uint returned = size;
            bool ok;
            fixed (byte* dataPtr = data)
            {
                ok = PInvoke.GetRawInputDeviceInfo(hDevice, RAW_INPUT_DEVICE_INFO_COMMAND.RIDI_PREPARSEDDATA, dataPtr, &returned) == size;
            }

            if (!ok)
            {
                preparsed = [];
                return false;
            }

            _preparsedDataCache[(nint)hDevice.Value] = data;
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
        _current = null;

        _repeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _repeatTimer.Dispose();

        if (!_hookHandle.IsNull)
        {
            PInvoke.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = default;
        }

        if (!_rawInputHwnd.IsNull)
        {
            SetRawInputEnabled(false);
            _rawInputHwnd = default;
        }
    }
}
