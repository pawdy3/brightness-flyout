using System.Collections.ObjectModel;
using BrightFlyout.ViewModels;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// Owns the set of controllable displays. Each physical monitor is probed for DDC/CI
/// (VCP 0x10); displays that answer get a <see cref="DdcCiDisplayController"/>.
/// Displays that don't answer are paired with WMI brightness instances (internal
/// laptop panels) in enumeration order; displays that neither answer DDC/CI nor have
/// a WMI counterpart are phantom/stale entries and are filtered out.
/// </summary>
public sealed class DisplayManager
{
    private readonly SettingsService _settings;

    /// <summary>All detected controllable displays (shown in Settings).</summary>
    public ObservableCollection<DisplayViewModel> Displays { get; } = new();

    /// <summary>Displays included in control (shown in the flyout).</summary>
    public ObservableCollection<DisplayViewModel> IncludedDisplays { get; } = new();

    /// <summary>Raised on the UI thread when the display list or a value changes.</summary>
    public event Action? Changed;

    public DisplayManager(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>The display targeted by the keyboard. "Last touched" in multi-display
    /// mode; falls back to the configured default, then to the first included display.</summary>
    public string? TargetId { get; private set; }

    public DisplayViewModel? Target =>
        IncludedDisplays.FirstOrDefault(d => d.Id == TargetId)
        ?? IncludedDisplays.FirstOrDefault(d => d.Id == _settings.DefaultMonitorId)
        ?? IncludedDisplays.FirstOrDefault();

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

    private List<IDisplayController> EnumerateControllers()
    {
        // Integration-test hook: in-memory displays, no hardware touched.
        if (Environment.GetEnvironmentVariable("BRIGHTFLYOUT_SIMULATE_DISPLAYS") is { Length: > 0 })
        {
            return SimulatedDisplayController.CreateSet();
        }

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

                if (ddcWorks)
                {
                    controllers.Add(new DdcCiDisplayController(deviceName, description));
                }
                else if (wmiIndex < wmiInstances.Count)
                {
                    // No DDC/CI: assume internal panel, pair with the next WMI instance.
                    controllers.Add(new WmiDisplayController(wmiInstances[wmiIndex++]));
                }
                else
                {
                    // Neither transport works: phantom/stale monitor entry (common on
                    // laptops, e.g. duplicate "Generic PnP Monitor"). Don't list it.
                    Log.Write($"Skipping unresponsive display '{description}' ({deviceName})");
                }
            }
        }
        finally
        {
            DdcCiDisplayController.DestroyAll(physical);
        }

        // Some internal panels are not exposed as physical monitors at all, so
        // nothing above pairs with their WMI instance. Leftover instances are
        // internal panels: add them directly.
        int pairedCount = wmiIndex;
        while (wmiIndex < wmiInstances.Count)
        {
            controllers.Add(new WmiDisplayController(wmiInstances[wmiIndex++]));
        }

        if (wmiInstances.Count > 0)
        {
            Log.Write($"WMI brightness instances: {wmiInstances.Count} ({pairedCount} paired to a physical monitor, {wmiInstances.Count - pairedCount} added directly)");
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
                Displays.Add(new DisplayViewModel(controller, OnDisplaySetRequested, OnDisplayInclusionChanged)
                {
                    IsIncluded = !_settings.ExcludedMonitorIds.Contains(controller.Id)
                });
            }
        }

        SyncIncludedList();
    }

    private void SyncIncludedList()
    {
        for (int i = IncludedDisplays.Count - 1; i >= 0; i--)
        {
            var d = IncludedDisplays[i];
            if (!Displays.Contains(d) || !d.IsIncluded)
            {
                IncludedDisplays.RemoveAt(i);
            }
        }

        foreach (var d in Displays.Where(d => d.IsIncluded && !IncludedDisplays.Contains(d)))
        {
            IncludedDisplays.Add(d);
        }
    }

    private void OnDisplayInclusionChanged(DisplayViewModel display, bool included)
    {
        var excluded = _settings.ExcludedMonitorIds;
        if (included)
        {
            excluded.RemoveAll(id => id == display.Id);
        }
        else if (!excluded.Contains(display.Id))
        {
            excluded.Add(display.Id);
        }

        _settings.Save();
        SyncIncludedList();
        UpdateTargetFlags();
        Changed?.Invoke();
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

    /// <summary>Sets every included display to the target's current brightness.</summary>
    public void SynchronizeAll()
    {
        var target = Target;
        if (target is null)
        {
            return;
        }

        SetAllIncluded(target.Brightness);
    }

    /// <summary>Applies a brightness value to all included displays.</summary>
    public void SetAllIncluded(int value)
    {
        foreach (var display in IncludedDisplays)
        {
            display.SetBrightness(value);
        }

        Changed?.Invoke();
    }
}
