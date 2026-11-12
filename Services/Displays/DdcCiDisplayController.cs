using BrightFlyout.Native;

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
    public static List<(NativeMethods.PHYSICAL_MONITOR Monitor, string DeviceName)> EnumerateAll()
    {
        var result = new List<(NativeMethods.PHYSICAL_MONITOR, string)>();

        NativeMethods.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            NativeMethods.GetMonitorInfoW(hMonitor, ref info);

            if (NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) && count > 0)
            {
                var monitors = new NativeMethods.PHYSICAL_MONITOR[count];
                if (NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                {
                    foreach (var monitor in monitors)
                    {
                        result.Add((monitor, info.szDevice ?? string.Empty));
                    }
                }
            }

            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return result;
    }

    public static void DestroyAll(List<(NativeMethods.PHYSICAL_MONITOR Monitor, string DeviceName)> monitors)
    {
        foreach (var group in monitors.GroupBy(m => m.DeviceName))
        {
            var arr = group.Select(g => g.Monitor).ToArray();
            NativeMethods.DestroyPhysicalMonitors((uint)arr.Length, arr);
        }
    }

    /// <summary>Runs an operation against this display's physical monitor handle.</summary>
    private T? WithHandle<T>(Func<IntPtr, (bool Ok, T Value)> operation) where T : struct
    {
        var all = EnumerateAll();
        try
        {
            // NOTE: hPhysicalMonitor can legitimately be zero on some systems -
            // zero is a valid handle here, not a "no match" sentinel.
            var match = all.FirstOrDefault(m =>
                m.DeviceName == _deviceName &&
                m.Monitor.szPhysicalMonitorDescription == _description);
            bool found = all.Any(m =>
                m.DeviceName == _deviceName &&
                m.Monitor.szPhysicalMonitorDescription == _description);
            if (!found)
            {
                IsAvailable = false;
                return default;
            }

            var (ok, value) = operation(match.Monitor.hPhysicalMonitor);
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

    public Task<int?> RefreshBrightnessAsync() => Task.Run(() =>
    {
        var result = WithHandle<int>(handle =>
            NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                handle, NativeMethods.VCP_CODE_LUMINANCE, IntPtr.Zero, out var current, out var maximum)
                ? (true, maximum == 0 ? 0 : (int)Math.Round(current * 100.0 / maximum))
                : (false, 0));

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

    private void WriteCore(int percent)
    {
        var result = WithHandle<int>(handle =>
        {
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    handle, NativeMethods.VCP_CODE_LUMINANCE, IntPtr.Zero, out _, out var maximum) && maximum > 0)
            {
                var raw = (uint)Math.Round(percent / 100.0 * maximum);
                return (NativeMethods.SetVCPFeature(handle, NativeMethods.VCP_CODE_LUMINANCE, raw), 0);
            }

            // Some monitors accept 0-100 even when the capability read fails.
            return (NativeMethods.SetVCPFeature(handle, NativeMethods.VCP_CODE_LUMINANCE, (uint)percent), 0);
        });

        if (!result.HasValue)
        {
            Log.Write($"DDC write of {percent} failed: no handle match for {Id}");
        }
    }
}
