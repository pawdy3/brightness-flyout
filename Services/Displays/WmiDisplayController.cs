using System.Management;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// Internal laptop panel controlled through WMI (root\wmi: WmiMonitorBrightness /
/// WmiMonitorBrightnessMethods). Used for displays that don't speak DDC/CI.
/// </summary>
internal sealed class WmiDisplayController : IDisplayController
{
    private readonly string _instanceName;

    public WmiDisplayController(string instanceName)
    {
        _instanceName = instanceName;
    }

    public string Id => $"wmi|{_instanceName}";
    public string Name => "Internal display";
    public string Technology => "WMI (internal panel)";
    public bool IsAvailable { get; private set; } = true;
    public int? CurrentBrightness { get; private set; }

    /// <summary>Enumerates all internal panels that expose WMI brightness control.</summary>
    public static List<string> EnumerateInstanceNames()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT InstanceName FROM WmiMonitorBrightness");
            return searcher.Get()
                .Cast<ManagementObject>()
                .Select(o => o["InstanceName"]?.ToString() ?? string.Empty)
                .Where(n => n.Length > 0)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public Task<int?> RefreshBrightnessAsync() => Task.Run(() =>
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                $"SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE InstanceName = '{EscapeWql(_instanceName)}'");
            var brightness = searcher.Get()
                .Cast<ManagementObject>()
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
        catch
        {
            IsAvailable = false;
        }

        return CurrentBrightness;
    });

    public void SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        CurrentBrightness = percent;

        Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\wmi",
                    $"SELECT * FROM WmiMonitorBrightnessMethods WHERE InstanceName = '{EscapeWql(_instanceName)}'");
                var methods = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                // WmiSetBrightness(timeoutSeconds, brightnessPercent)
                methods?.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                IsAvailable = methods is not null;
            }
            catch
            {
                IsAvailable = false;
            }
        });
    }

    private static string EscapeWql(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
