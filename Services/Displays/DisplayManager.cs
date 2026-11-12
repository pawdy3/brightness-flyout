using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrightFlyout.ViewModels;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// Owns the set of controllable displays, a simple union of two sources: every
/// physical monitor that answers a DDC/CI probe (VCP 0x10) gets a
/// <see cref="DdcCiDisplayController"/>, and every WMI brightness instance
/// (internal laptop panels) gets a <see cref="WmiDisplayController"/>.
/// I2C probes are retried because they flake under load, and a display is only
/// dropped after several consecutive enumeration misses.
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

    // True when the target was chosen implicitly (WMI mirror while the default
    // display was away) rather than by the user; such a target returns to the
    // default when it becomes available again.
    private bool _targetIsAutomatic;

    public DisplayViewModel? Target =>
        IncludedDisplays.FirstOrDefault(d => d.Id == TargetId)
        ?? IncludedDisplays.FirstOrDefault(d => d.Id == _settings.DefaultMonitorId)
        ?? IncludedDisplays.FirstOrDefault();

    public void SetTarget(string id, bool automatic = false)
    {
        if (TargetId == id)
        {
            _targetIsAutomatic = automatic;
            return;
        }

        if (Displays.Any(d => d.Id == id))
        {
            TargetId = id;
            _targetIsAutomatic = automatic;
            UpdateTargetFlags();
            Changed?.Invoke();
        }
    }

    /// <summary>Implicit target choice (WMI mirror). Only applies when the configured
    /// default display is not available - an explicit default always wins.</summary>
    public void SetAutomaticTarget(string id)
    {
        if (_settings.DefaultMonitorId.Length > 0
            && IncludedDisplays.Any(d => d.Id == _settings.DefaultMonitorId))
        {
            return;
        }

        SetTarget(id, automatic: true);
    }

    /// <summary>Re-enumerates displays and refreshes cached brightness.</summary>
    public async Task RefreshAsync()
    {
        var controllers = await Task.Run(EnumerateControllers);
        SyncCollection(controllers);
        await Task.WhenAll(Displays.Select(d => d.RefreshAsync()));
        RestoreDefaultTargetIfBack();
        UpdateTargetFlags();
        Changed?.Invoke();
    }

    /// <summary>An automatically chosen target (WMI mirror while the default display
    /// was away) returns to the configured default once it is available again.</summary>
    private void RestoreDefaultTargetIfBack()
    {
        if (_targetIsAutomatic
            && _settings.DefaultMonitorId.Length > 0
            && TargetId != _settings.DefaultMonitorId
            && IncludedDisplays.Any(d => d.Id == _settings.DefaultMonitorId))
        {
            SetTarget(_settings.DefaultMonitorId);
        }
    }

    // Enumeration runs on every poll tick; only log when the outcome changes.
    private string _lastEnumerationReport = string.Empty;

    private List<IDisplayController> EnumerateControllers()
    {
        // Integration-test hook: in-memory displays, no hardware touched.
        if (Environment.GetEnvironmentVariable("BRIGHTFLYOUT_SIMULATE_DISPLAYS") is { Length: > 0 })
        {
            return SimulatedDisplayController.CreateSet();
        }

        var controllers = new List<IDisplayController>();
        var silent = new List<string>();

        var physical = DdcCiDisplayController.EnumerateAll();
        try
        {
            foreach (var (monitor, deviceName) in physical)
            {
                var description = monitor.szPhysicalMonitorDescription.ToString();
                // DDC over I2C is flaky (especially via HDMI): retry the probe twice.
                bool ddcWorks = ProbeDdc(monitor.hPhysicalMonitor);
                for (var retry = 0; retry < 2 && !ddcWorks; retry++)
                {
                    Thread.Sleep(150);
                    ddcWorks = ProbeDdc(monitor.hPhysicalMonitor);
                }

                if (ddcWorks)
                {
                    controllers.Add(new DdcCiDisplayController(deviceName, description));
                }
                else
                {
                    silent.Add($"Display '{description}' ({deviceName}) does not answer DDC/CI");
                }
            }
        }
        finally
        {
            DdcCiDisplayController.DestroyAll(physical);
        }

        var wmiInstances = WmiDisplayController.EnumerateInstanceNames();
        foreach (var instance in wmiInstances)
        {
            controllers.Add(new WmiDisplayController(instance));
        }

        var report = $"Enumeration: {controllers.OfType<DdcCiDisplayController>().Count()} DDC/CI + {wmiInstances.Count} WMI display(s)"
            + (silent.Count > 0 ? "\n" + string.Join("\n", silent) : string.Empty);
        if (report != _lastEnumerationReport)
        {
            _lastEnumerationReport = report;
            Log.Write(report);
        }

        return controllers;
    }

    private static unsafe bool ProbeDdc(Windows.Win32.Foundation.HANDLE hPhysicalMonitor)
    {
        // dxva2 rejects null out-pointers with ERROR_INVALID_PARAMETER.
        uint current, maximum;
        return Windows.Win32.PInvoke.GetVCPFeatureAndVCPFeatureReply(
            hPhysicalMonitor, Native.Win32.VcpCodeLuminance,
            null, &current, &maximum) != 0;
    }

    // I2C probes flake under load (e.g. while a DDC write is in flight); a display
    // is dropped only after this many consecutive enumeration misses.
    private const int MissesBeforeRemoval = 3;
    private readonly Dictionary<string, int> _enumerationMisses = [];

    private void SyncCollection(List<IDisplayController> controllers)
    {
        for (int i = Displays.Count - 1; i >= 0; i--)
        {
            var display = Displays[i];
            if (controllers.Any(c => c.Id == display.Id))
            {
                _enumerationMisses.Remove(display.Id);
                continue;
            }

            var misses = _enumerationMisses.TryGetValue(display.Id, out var m) ? m + 1 : 1;
            if (misses >= MissesBeforeRemoval)
            {
                _enumerationMisses.Remove(display.Id);
                Log.Write($"Display lost after {misses} failed enumerations: {display.Name}");
                Displays.RemoveAt(i);
            }
            else
            {
                _enumerationMisses[display.Id] = misses;
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
    public void SetAllIncluded(int value, string? exceptId = null)
    {
        foreach (var display in IncludedDisplays)
        {
            if (display.Id == exceptId)
            {
                continue;
            }

            display.SetBrightness(value);
        }

        Changed?.Invoke();
    }
}
