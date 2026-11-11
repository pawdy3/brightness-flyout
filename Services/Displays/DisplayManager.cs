using System.Collections.ObjectModel;
using BrightFlyout.ViewModels;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// Owns the set of controllable displays. Each physical monitor is probed for DDC/CI
/// (VCP 0x10); displays that answer get a <see cref="DdcCiDisplayController"/>,
/// displays that don't (typically internal laptop panels) are paired with WMI
/// brightness instances in enumeration order.
/// </summary>
public sealed class DisplayManager
{
    private readonly SettingsService _settings;

    public ObservableCollection<DisplayViewModel> Displays { get; } = new();

    /// <summary>Raised on the UI thread when the display list or a value changes.</summary>
    public event Action? Changed;

    public DisplayManager(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>The display targeted by the keyboard. "Last touched" in multi-display
    /// mode; falls back to the configured default, then to the first display.</summary>
    public string? TargetId { get; private set; }

    public DisplayViewModel? Target =>
        Displays.FirstOrDefault(d => d.Id == TargetId)
        ?? Displays.FirstOrDefault(d => d.Id == _settings.DefaultMonitorId)
        ?? Displays.FirstOrDefault();

    public void SetTarget(string id)
    {
        if (TargetId != id && Displays.Any(d => d.Id == id))
        {
            TargetId = id;
            UpdateTargetFlags();
            Changed?.Invoke();
        }
    }

    /// <summary>Re-enumerates displays and refreshes cached brightness.</summary>
    public async Task RefreshAsync()
    {
        var controllers = await Task.Run(EnumerateControllers);
        SyncCollection(controllers);
        await Task.WhenAll(Displays.Select(d => d.RefreshAsync()));
        UpdateTargetFlags();
        Changed?.Invoke();
    }

    private static List<IDisplayController> EnumerateControllers()
    {
        var controllers = new List<IDisplayController>();
        var wmiInstances = WmiDisplayController.EnumerateInstanceNames();
        int wmiIndex = 0;

        var physical = DdcCiDisplayController.EnumerateAll();
        try
        {
            foreach (var (monitor, deviceName) in physical)
            {
                var description = monitor.szPhysicalMonitorDescription;
                bool ddcWorks = Native.NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    monitor.hPhysicalMonitor, Native.NativeMethods.VCP_CODE_LUMINANCE,
                    IntPtr.Zero, out _, out _);

                if (ddcWorks || wmiIndex >= wmiInstances.Count)
                {
                    controllers.Add(new DdcCiDisplayController(deviceName, description));
                }
                else
                {
                    // No DDC/CI: assume internal panel, pair with the next WMI instance.
                    controllers.Add(new WmiDisplayController(wmiInstances[wmiIndex++]));
                }
            }
        }
        finally
        {
            DdcCiDisplayController.DestroyAll(physical);
        }

        // Leftover WMI instances that couldn't be paired: still expose them.
        while (wmiIndex < wmiInstances.Count)
        {
            controllers.Add(new WmiDisplayController(wmiInstances[wmiIndex++]));
        }

        return controllers;
    }

    private void SyncCollection(List<IDisplayController> controllers)
    {
        for (int i = Displays.Count - 1; i >= 0; i--)
        {
            if (controllers.All(c => c.Id != Displays[i].Id))
            {
                Displays.RemoveAt(i);
            }
        }

        foreach (var controller in controllers)
        {
            if (Displays.All(d => d.Id != controller.Id))
            {
                Displays.Add(new DisplayViewModel(controller, OnDisplaySetRequested));
            }
        }
    }

    private void UpdateTargetFlags()
    {
        var target = Target;
        foreach (var display in Displays)
        {
            display.IsTarget = display == target;
        }
    }

    private void OnDisplaySetRequested(DisplayViewModel display, int value)
    {
        // A slider was moved: this display becomes the keyboard target.
        SetTarget(display.Id);
        Changed?.Invoke();
    }

    /// <summary>Sets every display to the target's current brightness.</summary>
    public void SynchronizeAll()
    {
        var target = Target;
        if (target is null)
        {
            return;
        }

        foreach (var display in Displays)
        {
            display.SetBrightness(target.Brightness);
        }
    }
}
