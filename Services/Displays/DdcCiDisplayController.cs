using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BrightFlyout.Native;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// DDC/CI display (external monitor) controlled via VCP code 0x10 (luminance)
/// through the Monitor Configuration API (dxva2). The physical monitor handle is
/// re-acquired for every operation and released immediately afterwards.
/// </summary>
internal sealed class DdcCiDisplayController : IDisplayController
{
    private readonly string _deviceName;
    private readonly string _description;

    private readonly object _writeGate = new();
    private bool _writerRunning;
    private int _pendingValue = -1;
    private int _lastWrittenValue = -1;

    public DdcCiDisplayController(string deviceName, string description)
    {
        _deviceName = deviceName;
        _description = description;
    }

    public string Id => $"ddcci|{_deviceName}|{_description}";
    public string Name => string.IsNullOrWhiteSpace(_description) ? _deviceName : _description;
    public string Technology => "DDC/CI";
    public bool IsAvailable { get; private set; }
    public int? CurrentBrightness { get; private set; }

    /// <summary>
    /// Enumerates all physical monitors of all logical displays.
    /// Caller owns the returned handles and must destroy them via
    /// <see cref="DestroyAll"/>. Blocks the calling thread (slow I2C).
    /// </summary>
    public static unsafe List<(PHYSICAL_MONITOR Monitor, string DeviceName)> EnumerateAll()
    {
        var result = new List<(PHYSICAL_MONITOR, string)>();
        var resultHandle = GCHandle.Alloc(result);
        try
        {
            PInvoke.EnumDisplayMonitors(
                default(HDC), null, &OnMonitorEnum, (LPARAM)(nint)resultHandle);
        }
        finally
        {
            resultHandle.Free();
        }

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe BOOL OnMonitorEnum(HMONITOR hMonitor, HDC hdcMonitor, RECT* lprcMonitor, LPARAM dwData)
    {
        var result = (List<(PHYSICAL_MONITOR, string)>)GCHandle.FromIntPtr(dwData.Value).Target!;

        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        PInvoke.GetMonitorInfo(hMonitor, (MONITORINFO*)&info);

        if (PInvoke.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) && count > 0)
        {
            var monitors = new PHYSICAL_MONITOR[count];
            if (PInvoke.GetPhysicalMonitorsFromHMONITOR(hMonitor, monitors))
            {
                foreach (var monitor in monitors)
                {
                    result.Add((monitor, info.szDevice.ToString()));
                }
            }
        }

        return true;
    }

    public static void DestroyAll(List<(PHYSICAL_MONITOR Monitor, string DeviceName)> monitors)
    {
        foreach (var group in monitors.GroupBy(m => m.DeviceName))
        {
            PInvoke.DestroyPhysicalMonitors(group.Select(g => g.Monitor).ToArray());
        }
    }

    /// <summary>Runs an operation against this display's physical monitor handle.</summary>
    private T? WithHandle<T>(Func<HANDLE, (bool Ok, T Value)> operation) where T : struct
    {
        var all = EnumerateAll();
        try
        {
            // NOTE: hPhysicalMonitor can legitimately be zero - zero is a valid
            // handle here, not a "no match" sentinel. Use an explicit loop.
            HANDLE handle = default;
            var found = false;
            foreach (var m in all)
            {
                if (m.DeviceName == _deviceName
                    && m.Monitor.szPhysicalMonitorDescription.Equals(_description))
                {
                    handle = m.Monitor.hPhysicalMonitor;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                IsAvailable = false;
                return default;
            }

            var (ok, value) = operation(handle);
            IsAvailable = ok;
            return ok ? value : default;
        }
        catch
        {
            IsAvailable = false;
            return default;
        }
        finally
        {
            DestroyAll(all);
        }
    }

    public unsafe Task<int?> RefreshBrightnessAsync() => Task.Run(() =>
    {
        var result = WithHandle<int>(handle =>
        {
            uint current, maximum;
            return PInvoke.GetVCPFeatureAndVCPFeatureReply(
                handle, Win32.VcpCodeLuminance, null, &current, &maximum) != 0
                ? (true, maximum == 0 ? 0 : (int)Math.Round(current * 100.0 / maximum))
                : (false, 0);
        });

        if (result.HasValue)
        {
            CurrentBrightness = Math.Clamp(result.Value, 0, 100);
        }

        return CurrentBrightness;
    });

    public void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        CurrentBrightness = percent;

        lock (_writeGate)
        {
            _pendingValue = percent;
            if (_writerRunning)
            {
                return;
            }

            _writerRunning = true;
        }

        Task.Run(WriterLoop);
    }

    private void WriterLoop()
    {
        while (true)
        {
            int value;
            lock (_writeGate)
            {
                if (_pendingValue == _lastWrittenValue)
                {
                    _writerRunning = false;
                    return;
                }

                value = _pendingValue;
            }

            WriteCore(value);
            _lastWrittenValue = value;
        }
    }

    private unsafe void WriteCore(int percent)
    {
        var result = WithHandle<int>(handle =>
        {
            uint current, maximum;
            if (PInvoke.GetVCPFeatureAndVCPFeatureReply(
                    handle, Win32.VcpCodeLuminance, null, &current, &maximum) != 0 && maximum > 0)
            {
                var raw = (uint)Math.Round(percent / 100.0 * maximum);
                return (PInvoke.SetVCPFeature(handle, Win32.VcpCodeLuminance, raw) != 0, 0);
            }

            // Some monitors accept 0-100 even when the capability read fails.
            return (PInvoke.SetVCPFeature(handle, Win32.VcpCodeLuminance, (uint)percent) != 0, 0);
        });

        if (!result.HasValue)
        {
            Log.Write($"DDC write of {percent} failed: no handle match for {Id}");
        }
    }
}
