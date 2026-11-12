using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BrightFlyout.Services.Displays;
using BrightFlyout.Views;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace BrightFlyout.Services;

/// <summary>
/// Orchestrates the app: keyboard hook events -> per-display brightness change ->
/// custom OSD + transient suppression of the native Windows OSD.
/// </summary>
internal sealed class BrightnessController : IDisposable
{
    private readonly SettingsService _settings;
    private readonly OsdManager _osd;
    private readonly KeyboardHookService _hook;
    private TrayIconService? _tray;
    private readonly WmiBrightnessWatcher _wmiWatcher = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _suppressWatchdog;
    private readonly DispatcherQueueTimer _restoreTimer;
    private readonly DispatcherQueueTimer _hotplugTimer;
    private readonly DispatcherQueueTimer _pollTimer;
    private string _lastRefreshFingerprint = string.Empty;

    // Excluded-panel revert state (see OnWmiBrightnessChanged).
    private readonly DispatcherQueueTimer _wmiRevertTimer;
    private int? _wmiRevertingTo;
    private int? _lastWmiEventValue;

    /// <summary>Raised (on the UI thread) whenever the target display's brightness changes.</summary>
    public event Action<int>? BrightnessChanged;

    public DisplayManager Displays { get; }

    public BrightnessController(SettingsService settings, OsdManager osd)
    {
        _settings = settings;
        _osd = osd;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Displays = new DisplayManager(settings);
        Displays.Changed += OnDisplaysChanged;

        _hook = new KeyboardHookService(settings);
        _hook.BrightnessStepRequested += OnBrightnessStepRequested;
        _osd.AutoHidden += OnOsdAutoHidden;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        _wmiWatcher.BrightnessChanged += OnWmiBrightnessChanged;

        // Explorer can create the native OSD island a moment *after* the key press,
        // so a single suppress attempt can miss it. While our OSD is visible, keep
        // retrying until the native one is actually hidden.
        _suppressWatchdog = _dispatcherQueue.CreateTimer();
        _suppressWatchdog.Interval = TimeSpan.FromMilliseconds(150);
        _suppressWatchdog.Tick += (_, _) =>
        {
            // Cheap and idempotent: the shell can recreate its OSD host between ticks.
            NativeOsdSuppressor.Suppress();
        };

        // Bring the native OSD back only after our flyout has fully disappeared,
        // otherwise both are on screen at once.
        _restoreTimer = _dispatcherQueue.CreateTimer();
        _restoreTimer.IsRepeating = false;
        _restoreTimer.Interval = TimeSpan.FromMilliseconds(400);
        _restoreTimer.Tick += (_, _) => NativeOsdSuppressor.Restore();

        // Monitor plug/unplug arrives as a burst of WM_DISPLAYCHANGE messages;
        // coalesce into a single re-enumeration.
        _hotplugTimer = _dispatcherQueue.CreateTimer();
        _hotplugTimer.IsRepeating = false;
        _hotplugTimer.Interval = TimeSpan.FromMilliseconds(800);
        _hotplugTimer.Tick += (_, _) => _ = RefreshDisplaysAsync();

        // Events are primary (WM_DISPLAYCHANGE, monitor device-interface arrival,
        // display power transitions); this is just a slow backstop for changes
        // that raise none of them (e.g. a DDC link dying silently).
        _pollTimer = _dispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(60);
        _pollTimer.Tick += (_, _) => _ = RefreshDisplaysAsync();

        // When the internal panel is excluded from control, Windows still changes
        // it on laptop key presses. The vendor driver ramps smoothly and fires an
        // event per intermediate step, so reverting immediately per event fights
        // the ramp. Revert once, after the press burst ends.
        _wmiRevertTimer = _dispatcherQueue.CreateTimer();
        _wmiRevertTimer.IsRepeating = false;
        _wmiRevertTimer.Interval = TimeSpan.FromMilliseconds(400);
        _wmiRevertTimer.Tick += (_, _) =>
        {
            var wmiDisplay = Displays.Displays.FirstOrDefault(d => d.Controller is WmiDisplayController);
            if (wmiDisplay is null || (_lastWmiEventValue ?? wmiDisplay.Brightness) == wmiDisplay.Brightness)
            {
                return;
            }

            var baseline = wmiDisplay.Brightness;
            Log.Write($"Reverting Windows-made change on excluded panel -> {baseline}%");
            _wmiRevertingTo = baseline;
            _lastWmiEventValue = baseline;
            wmiDisplay.Controller.SetBrightness(baseline);
        };
    }

    // WM_DISPLAYCHANGE / monitor device-interface arrival arrive via the tray
    // window (process-lifetime, unlike OSD windows, which are recreated when the
    // display topology changes). Runs on the UI thread already.
    private void OnDisplayChangeBroadcast()
    {
        _hotplugTimer.Stop();
        _hotplugTimer.Start();
    }

    private void OnOsdAutoHidden()
    {
        _suppressWatchdog.Stop();
        _restoreTimer.Stop();
        _restoreTimer.Start();
    }

    public void Start(TrayIconService? tray)
    {
        _tray = tray;
        if (tray is null)
        {
            // Hook-only mode: raw input and hotplug detection need a window.
            Log.Write("Tray window unavailable: raw input and display-change detection disabled");
            _hook.Install(default);
        }
        else
        {
            _hook.Install(tray.Hwnd);
            tray.RawInputMessage += _hook.ProcessRawInputMessage;
            tray.DisplayChangeBroadcast += OnDisplayChangeBroadcast;
        }

        _pollTimer.Start();

        _ = RefreshDisplaysAsync();
    }

    public async Task RefreshDisplaysAsync()
    {
        try
        {
            await Displays.RefreshAsync();
            // Log only on change: the poll timer would otherwise spam the log.
            var fingerprint = $"{Displays.Displays.Count}|{Displays.Target?.Name ?? "none"}";
            if (fingerprint != _lastRefreshFingerprint)
            {
                _lastRefreshFingerprint = fingerprint;
                Log.Write($"Displays refreshed: {Displays.Displays.Count} found, target={Displays.Target?.Name ?? "none"}");
            }
        }
        catch (Exception ex)
        {
            Log.Write("Display refresh failed", ex);
        }

        // The WMI event watcher is only meaningful with an internal panel present.
        if (Displays.Displays.Any(d => d.Controller is WmiDisplayController))
        {
            _wmiWatcher.Start();
        }

        if (Displays.Target is { } target)
        {
            BrightnessChanged?.Invoke(target.Brightness);
        }
    }

    private void OnDisplaysChanged()
    {
        _osd.RefreshDisplays();
        if (Displays.Target is { } target)
        {
            BrightnessChanged?.Invoke(target.Brightness);
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsService.EnableRawInput))
        {
            _hook.SetRawInputEnabled(_settings.EnableRawInput);
        }
        else if (e.PropertyName is nameof(SettingsService.DefaultMonitorId)
            && _settings.DefaultMonitorId is { Length: > 0 } id)
        {
            // The configured default wins over the "last touched" target,
            // effective immediately (flyout selection follows).
            Displays.SetTarget(id);
        }
    }

    private void OnBrightnessStepRequested(BrightnessDirection direction)
    {
        var enqueued = _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var target = Displays.Target;
                if (target is null)
                {
                    Log.Write("Step ignored: no target display");
                    return;
                }

                // Re-read before stepping: another device or app may have changed
                // the display since we last looked (cached values go stale).
                await target.RefreshAsync();
                var value = Math.Clamp(target.Brightness + _settings.BrightnessStep * (int)direction, 0, 100);
                ApplyBrightness(value);
            }
            catch (Exception ex)
            {
                Log.Write("Brightness step failed", ex);
            }
        });

        if (!enqueued)
        {
            Log.Write("DispatcherQueue.TryEnqueue returned false");
        }
    }

    /// <summary>Laptop path: Windows itself changed the internal panel brightness
    /// (mobile systems consume the HID usages before any hook). Mirror the change.</summary>
    private void OnWmiBrightnessChanged(int value)
    {
        Log.Write($"WMI brightness event -> {value}%");
        _dispatcherQueue.TryEnqueue(() =>
        {
            var wmiDisplay = Displays.Displays.FirstOrDefault(d => d.Controller is WmiDisplayController);
            if (wmiDisplay is null)
            {
                return;
            }

            // A revert write makes the vendor driver ramp smoothly back to the
            // pinned value, firing an event per intermediate step. Swallow those
            // until the ramp completes - they are not key presses.
            if (_wmiRevertingTo is { } revertingTo)
            {
                if (Math.Abs(value - revertingTo) <= 1)
                {
                    _wmiRevertingTo = null;
                    _lastWmiEventValue = value;
                    wmiDisplay.SetBrightnessSilently(value);
                }

                return;
            }

            if (!wmiDisplay.IsIncluded)
            {
                // The panel is excluded, but Windows changed it anyway (monitor.sys
                // handles the ACPI hotkey in kernel mode - there is no user-mode or
                // registry opt-out). Forward each event's delta to the actual target
                // display (native steps arrive as +-10, vendor smooth-hold as +-1,
                // so forward the delta, not a fixed step), and revert the panel once
                // the burst ends. The panel model stays pinned at the pre-press value.
                var delta = value - (_lastWmiEventValue ?? value);
                _lastWmiEventValue = value;
                if (delta == 0)
                {
                    return;
                }

                if (Displays.Target is { } target && target != wmiDisplay)
                {
                    ApplyBrightness(Math.Clamp(target.Brightness + delta, 0, 100));
                }

                _wmiRevertTimer.Stop();
                _wmiRevertTimer.Start();
                return;
            }

            if (Math.Abs(wmiDisplay.Brightness - value) <= 1)
            {
                return; // feedback from our own write, or nothing to do
            }

            wmiDisplay.SetBrightnessSilently(value);
            _lastWmiEventValue = value;
            // Implicit target: applies only while no configured default is available.
            Displays.SetAutomaticTarget(wmiDisplay.Id);

            if (_settings.SyncMonitors)
            {
                // Windows already wrote the internal panel; writing it back would
                // fight the vendor driver's smooth stepping (event storm).
                Displays.SetAllIncluded(value, exceptId: wmiDisplay.Id);
            }

            ApplyOsd(value);
            BrightnessChanged?.Invoke(value);
        });
    }

    /// <summary>Applies a brightness value honoring the sync toggle, then shows the OSD.</summary>
    private void ApplyBrightness(int value)
    {
        if (_settings.SyncMonitors)
        {
            Displays.SetAllIncluded(value);
        }
        else
        {
            Displays.Target?.SetBrightness(value);
        }

        Log.Write($"Brightness -> {value}% ({Displays.Target?.Name ?? "none"})");
        ApplyOsd(value);
        BrightnessChanged?.Invoke(value);
    }

    private void ApplyOsd(int value)
    {
        // Hide the native Windows OSD only for the short window in which it
        // could appear; restored on OSD auto-hide and on app exit.
        NativeOsdSuppressor.Suppress();
        _restoreTimer.Stop();
        _suppressWatchdog.Start();

        _osd.ShowBrightness(value);
    }

    /// <summary>Directly sets the target display's brightness (settings slider).</summary>
    public void SetTargetBrightness(int value, bool showOsd = false)
    {
        if (_settings.SyncMonitors)
        {
            Displays.SetAllIncluded(value);
        }
        else
        {
            Displays.Target?.SetBrightness(value);
        }

        if (showOsd)
        {
            _osd.ShowBrightness(value);
        }

        BrightnessChanged?.Invoke(value);
    }

    /// <summary>Preview the OSD without touching brightness or suppressing the native one.</summary>
    public void PreviewOsd()
    {
        _osd.ShowBrightness(Displays.Target?.Brightness ?? 65);
    }

    public void Dispose()
    {
        _osd.AutoHidden -= OnOsdAutoHidden;
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        if (_tray is not null)
        {
            _tray.RawInputMessage -= _hook.ProcessRawInputMessage;
            _tray.DisplayChangeBroadcast -= OnDisplayChangeBroadcast;
            _tray = null;
        }

        _suppressWatchdog.Stop();
        _restoreTimer.Stop();
        _hotplugTimer.Stop();
        _pollTimer.Stop();
        _wmiRevertTimer.Stop();

        _wmiWatcher.Dispose();
        _hook.Dispose();
        NativeOsdSuppressor.Restore();
    }
}
