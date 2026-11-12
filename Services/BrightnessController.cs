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
    // Unmanaged callbacks (window subclass) cannot capture; the controller is a singleton.
    private static BrightnessController? _current;

    private readonly SettingsService _settings;
    private readonly OsdManager _osd;
    private readonly KeyboardHookService _hook;
    private readonly WmiBrightnessWatcher _wmiWatcher = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _suppressWatchdog;
    private readonly DispatcherQueueTimer _restoreTimer;
    private readonly DispatcherQueueTimer _hotplugTimer;

    // Excluded-panel revert state (see OnWmiBrightnessChanged).
    private int? _wmiRevertingTo;

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
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT DisplayChangeSubclassProc(HWND hWnd, uint uMsg, WPARAM wParam, LPARAM lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == PInvoke.WM_DISPLAYCHANGE && _current is { } self)
        {
            self._dispatcherQueue.TryEnqueue(() =>
            {
                self._hotplugTimer.Stop();
                self._hotplugTimer.Start();
            });
        }

        return PInvoke.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void OnOsdAutoHidden()
    {
        _suppressWatchdog.Stop();
        _restoreTimer.Stop();
        _restoreTimer.Start();
    }

    public unsafe void Start()
    {
        _current = this;
        _hook.Install(_osd.Hwnd);

        // The OSD window is always alive (hidden), so it receives display-change
        // broadcasts even with no visible UI.
        PInvoke.SetWindowSubclass(_osd.Hwnd, &DisplayChangeSubclassProc, 3, 0);

        _ = RefreshDisplaysAsync();
    }

    public async Task RefreshDisplaysAsync()
    {
        try
        {
            await Displays.RefreshAsync();
            Log.Write($"Displays refreshed: {Displays.Displays.Count} found, target={(Displays.Target?.Name ?? "none")}");
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
        var enqueued = _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var target = Displays.Target;
                if (target is null)
                {
                    Log.Write("Step ignored: no target display");
                    return;
                }

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

            // Feedback echo of our own revert write: swallow it.
            if (_wmiRevertingTo is { } revertingTo && Math.Abs(value - revertingTo) <= 1)
            {
                _wmiRevertingTo = null;
                wmiDisplay.SetBrightnessSilently(value);
                return;
            }
            _wmiRevertingTo = null;

            if (!wmiDisplay.IsIncluded)
            {
                // The panel is excluded, but Windows changed it anyway (monitor.sys
                // handles the ACPI hotkey in kernel mode - there is no user-mode or
                // registry opt-out). Revert immediately, before the backlight ramp
                // becomes visible, and forward the press direction to the actual
                // target display instead. The panel model stays pinned at the
                // pre-press value.
                var direction = Math.Sign(value - wmiDisplay.Brightness);
                if (direction == 0)
                {
                    return;
                }

                Log.Write($"Reverting Windows-made change on excluded panel -> {wmiDisplay.Brightness}%");
                _wmiRevertingTo = wmiDisplay.Brightness;
                wmiDisplay.Controller.SetBrightness(wmiDisplay.Brightness);

                if (Displays.Target is { } target && target != wmiDisplay)
                {
                    var stepped = Math.Clamp(
                        target.Brightness + _settings.BrightnessStep * direction, 0, 100);
                    ApplyBrightness(stepped);
                }

                return;
            }

            if (Math.Abs(wmiDisplay.Brightness - value) <= 1)
            {
                return; // feedback from our own write, or nothing to do
            }

            wmiDisplay.SetBrightnessSilently(value);
            Displays.SetTarget(wmiDisplay.Id);

            if (_settings.SyncMonitors)
            {
                Displays.SetAllIncluded(value);
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

    public unsafe void Dispose()
    {
        _osd.AutoHidden -= OnOsdAutoHidden;
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _suppressWatchdog.Stop();
        _restoreTimer.Stop();
        _hotplugTimer.Stop();
        PInvoke.RemoveWindowSubclass(_osd.Hwnd, &DisplayChangeSubclassProc, 3);
        _current = null;

        _wmiWatcher.Dispose();
        _hook.Dispose();
        NativeOsdSuppressor.Restore();
    }
}
