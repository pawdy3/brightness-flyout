using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WmiLight;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// Internal laptop panel controlled through WMI (root\wmi: WmiMonitorBrightness /
/// WmiMonitorBrightnessMethods) via WmiLight (NativeAOT-compatible, unlike
/// System.Management). Used for displays that don't speak DDC/CI.
/// </summary>
internal sealed class WmiDisplayController : IDisplayController
{
    private const string RootWmi = @"root\wmi";

    private readonly string _instanceName;

    private readonly object _writeGate = new();
    private bool _writerRunning;
    private int _pendingValue = -1;
    private int _lastWrittenValue = -1;

    public WmiDisplayController(string instanceName)
    {
        _instanceName = instanceName;
    }

    public string Id => $"wmi|{_instanceName}";
    public string Name => "Internal display";
    public string Technology => "WMI (internal panel)";
    public bool IsAvailable { get; private set; } = true;
    public int? CurrentBrightness { get; private set; }

    // WBEM_E_NOT_SUPPORTED / WBEM_E_INVALID_CLASS: WmiMonitorBrightness simply does
    // not exist on machines without an internal panel (desktops) - not an error.
    private const int WbemENotSupported = unchecked((int)0x8004100C);
    private const int WbemEInvalidClass = unchecked((int)0x80041010);

    /// <summary>Enumerates all internal panels that expose WMI brightness control.</summary>
    public static List<string> EnumerateInstanceNames()
    {
        try
        {
            using var connection = new WmiConnection(RootWmi);
            return connection.CreateQuery("SELECT InstanceName FROM WmiMonitorBrightness")
                .Select(o => o["InstanceName"]?.ToString() ?? string.Empty)
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch (System.Runtime.InteropServices.COMException ex)
            when (ex.HResult is WbemENotSupported or WbemEInvalidClass)
        {
            return [];
        }
        catch (Exception ex)
        {
            Log.Write("WMI brightness enumeration failed", ex);
            return [];
        }
    }

    public Task<int?> RefreshBrightnessAsync() => Task.Run(() =>
    {
        try
        {
            using var connection = new WmiConnection(RootWmi);
            var brightness = connection.CreateQuery(
                    $"SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE InstanceName = '{EscapeWmi(_instanceName)}'")
                .Select(o => Convert.ToInt32(o["CurrentBrightness"]))
                .FirstOrDefault(-1);

            if (brightness >= 0)
            {
                IsAvailable = true;
                CurrentBrightness = Math.Clamp(brightness, 0, 100);
            }
            else
            {
                IsAvailable = false;
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Log.Write($"WMI brightness read failed ({_instanceName})", ex);
        }

        return CurrentBrightness;
    });

    public void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        CurrentBrightness = percent;

        // Coalesce: immediate per-event reverts during a key hold would otherwise
        // queue dozens of identical WMI writes.
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
        try
        {
            using var connection = new WmiConnection(RootWmi);
            var methods = connection.CreateQuery(
                    $"SELECT * FROM WmiMonitorBrightnessMethods WHERE InstanceName = '{EscapeWmi(_instanceName)}'")
                .FirstOrDefault();
            if (methods is null)
            {
                IsAvailable = false;
                return;
            }

            // WmiSetBrightness(timeoutSeconds, brightnessPercent)
            // NOTE: WBEM's in-params Put rejects VT_UI4 for the CIM uint32
            // Timeout (WBEM_E_TYPE_MISMATCH) - it must go in as VT_I4 (int).
            // Brightness is CIM uint8, which accepts VT_UI1 (byte).
            using var method = methods.GetMethod("WmiSetBrightness");
            using var parameters = method.CreateInParameters();
            parameters.SetPropertyValue("Timeout", 1);
            parameters.SetPropertyValue("Brightness", (byte)percent);
            methods.ExecuteMethod<uint>(method, parameters, out _);
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Log.Write($"WMI brightness write failed ({_instanceName})", ex);
        }
    }

    /// <summary>Diagnostic hook (BRIGHTFLYOUT_WMI_SELFTEST, or the Settings button):
    /// proves the WMI path works on any machine, even without an internal panel.
    /// Returns and logs a one-line summary.</summary>
    public static string SelfTest()
    {
        string summary;
        try
        {
            var instanceCount = EnumerateInstanceNames().Count;

            using var connection = new WmiConnection(); // default namespace: root\cimv2
            var caption = connection.CreateQuery("SELECT Caption FROM Win32_OperatingSystem")
                .Select(o => o["Caption"]?.ToString())
                .FirstOrDefault();

            summary = $"OK: {instanceCount} brightness instance(s); OS query -> '{caption ?? "(null)"}'";
        }
        catch (Exception ex)
        {
            summary = $"FAILED: {ex.Message}";
        }

        Log.Write($"WMI self-test: {summary}");
        return summary;
    }

    private static string EscapeWmi(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
