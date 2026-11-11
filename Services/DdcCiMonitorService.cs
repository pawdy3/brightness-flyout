using BrightFlyout.Native;

namespace BrightFlyout.Services;

/// <summary>
/// Controls the primary display's brightness over DDC/CI (VCP code 0x10, luminance).
/// Monitor handles are enumerated per operation so the service survives hot-plug,
/// sleep and display-mode changes. Writes are coalesced on a background worker so
/// rapid key presses never queue up a backlog of slow DDC transactions.
/// </summary>
internal sealed class DdcCiMonitorService
{
    private readonly object _writeGate = new();
    private bool _writerRunning;
    private int _pendingValue = -1;
    private int _lastWrittenValue = -1;

    /// <summary>Last known brightness (0-100), or null if not read yet.</summary>
    private int? _currentBrightness;

    public bool IsAvailable { get; private set; }

    public string MonitorName { get; private set; } = string.Empty;

    public int? CurrentBrightness => _currentBrightness;

    /// <summary>Reads the current brightness from the monitor. Safe to call from any thread.</summary>
    public int? ReadBrightness()
    {
        try
        {
            var monitors = GetPhysicalMonitors();
            if (monitors.Length == 0)
            {
                IsAvailable = false;
                return null;
            }

            try
            {
                var monitor = monitors[0];
                MonitorName = monitor.szPhysicalMonitorDescription;

                if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                        monitor.hPhysicalMonitor, NativeMethods.VCP_CODE_LUMINANCE,
                        IntPtr.Zero, out var current, out var maximum))
                {
                    IsAvailable = false;
                    return null;
                }

                var percent = maximum == 0 ? 0 : (int)Math.Round(current * 100.0 / maximum);
                IsAvailable = true;
                _currentBrightness = Math.Clamp(percent, 0, 100);
                return _currentBrightness;
            }
            finally
            {
                NativeMethods.DestroyPhysicalMonitors((uint)monitors.Length, monitors);
            }
        }
        catch
        {
            IsAvailable = false;
            return null;
        }
    }

    /// <summary>
    /// Applies a new brightness level (0-100). Returns the effective level.
    /// The actual DDC write happens asynchronously; intermediate values are coalesced.
    /// </summary>
    public int SetBrightness(int value)
    {
        value = Math.Clamp(value, 0, 100);
        _currentBrightness = value;

        lock (_writeGate)
        {
            _pendingValue = value;
            if (_writerRunning)
            {
                return value;
            }

            _writerRunning = true;
        }

        Task.Run(WriterLoop);
        return value;
    }

    /// <summary>Adjusts brightness by a signed step, reading the monitor first if needed.</summary>
    public int StepBrightness(int delta)
    {
        var current = _currentBrightness ?? ReadBrightness() ?? 50;
        return SetBrightness(current + delta);
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

            WriteBrightnessCore(value);
            _lastWrittenValue = value;
        }
    }

    private void WriteBrightnessCore(int percent)
    {
        try
        {
            var monitors = GetPhysicalMonitors();
            if (monitors.Length == 0)
            {
                IsAvailable = false;
                return;
            }

            try
            {
                var monitor = monitors[0];
                if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                        monitor.hPhysicalMonitor, NativeMethods.VCP_CODE_LUMINANCE,
                        IntPtr.Zero, out _, out var maximum) && maximum > 0)
                {
                    var raw = (uint)Math.Round(percent / 100.0 * maximum);
                    IsAvailable = NativeMethods.SetVCPFeature(
                        monitor.hPhysicalMonitor, NativeMethods.VCP_CODE_LUMINANCE, raw);
                }
                else
                {
                    // Some monitors accept 0-100 even when the capability read fails.
                    IsAvailable = NativeMethods.SetVCPFeature(
                        monitor.hPhysicalMonitor, NativeMethods.VCP_CODE_LUMINANCE, (uint)percent);
                }
            }
            finally
            {
                NativeMethods.DestroyPhysicalMonitors((uint)monitors.Length, monitors);
            }
        }
        catch
        {
            IsAvailable = false;
        }
    }

    private static NativeMethods.PHYSICAL_MONITOR[] GetPhysicalMonitors()
    {
        // Primary monitor: the virtual desktop origin (0,0) always lies on it.
        var hMonitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (hMonitor == IntPtr.Zero)
        {
            return [];
        }

        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
        {
            return [];
        }

        var monitors = new NativeMethods.PHYSICAL_MONITOR[count];
        return NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)
            ? monitors
            : [];
    }
}
