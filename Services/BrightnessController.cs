using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BrightFlyout.Services.Displays;
using BrightFlyout.Views;
using Microsoft.UI.Dispatching;

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
    private readonly DispatcherQueueTimer _boundaryTimer;
    private string _lastRefreshFingerprint = string.Empty;

    // Non-target-panel revert state (see OnPanelBrightnessChanged).
    private int? _wmiRevertingTo;
    private int? _lastPanelEventValue;
    private long _lastProcessedEventTick;

    // Boundary-press detection (see the region below): set when a native-OSD
    // show is pending classification, cleared when a panel brightness event is
    // attributed to that show.
    private bool _boundaryCheckPending;

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
        _wmiWatcher.BrightnessChanged += OnWmiBrightnessEvent;

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
        // otherwise both are on screen at once. Win10's legacy host needs a bit
        // more: restoring re-shows its stale content, which reads as a flash of a
        // second OSD when the handoff is too quick.
        _restoreTimer = _dispatcherQueue.CreateTimer();
        _restoreTimer.IsRepeating = false;
        _restoreTimer.Interval = TimeSpan.FromMilliseconds(App.IsWindows11 ? 400 : 600);
        _restoreTimer.Tick += (_, _) => NativeOsdSuppressor.Restore();

        // One-shot check for boundary presses (panel pinned at 0/100: Windows
        // swallows the key without a brightness change, but still shows its OSD).
        _boundaryTimer = _dispatcherQueue.CreateTimer();
        _boundaryTimer.IsRepeating = false;
        _boundaryTimer.Interval = TimeSpan.FromMilliseconds(250);
        _boundaryTimer.Tick += async (_, _) =>
        {
            try
            {
                await HandleBoundaryPressAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Boundary press check failed", ex);
            }
        };

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
            tray.BrightnessSettingChanged += OnPowerBrightnessEvent;
        }

        NativeOsdSuppressor.NativeOsdShown += OnNativeOsdShown;
        NativeOsdSuppressor.EnsureWatching();

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
    /// (mobile systems consume the HID usages before any hook). Mirror the change.
    /// Fed by two sources: the WMI brightness event, and the GUID_DISPLAY_BRIGHTNESS
    /// power-setting broadcast (some drivers deliver WMI events unreliably).</summary>
    private void OnWmiBrightnessEvent(int value) => OnPanelBrightnessChanged(value, "WMI");

    private void OnPowerBrightnessEvent(int value) => OnPanelBrightnessChanged(value, "power");

    private void OnPanelBrightnessChanged(int value, string source)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // A panel change attributed to a pending boundary check: the native
            // OSD that triggered the check was a real brightness change, not a
            // boundary press. (FIFO on this queue: a key's event runs before the
            // boundary timer tick it would otherwise trip.)
            _boundaryCheckPending = false;

            // Most changes arrive on BOTH channels (WMI event + power broadcast);
            // the second report carries the same value. Dropping it matters: it
            // would otherwise be mistaken for a real change and corrupt the
            // revert echo-swallow state. (Compared against the last *processed*
            // event, so a genuine repeat of the same value seconds later still
            // goes through.)
            var now = Environment.TickCount64;
            if (_lastPanelEventValue == value && now - _lastProcessedEventTick < 500)
            {
                return;
            }

            _lastProcessedEventTick = now;
            Log.Write($"Panel brightness event ({source}) -> {value}%");
            var wmiDisplay = Displays.Displays.FirstOrDefault(d => d.Controller is WmiDisplayController);
            if (wmiDisplay is null)
            {
                return;
            }

            if (_wmiRevertingTo is { } revertingTo)
            {
                if (Math.Abs(value - revertingTo) <= 1)
                {
                    // Feedback echo of our own revert write: swallow it.
                    _wmiRevertingTo = null;
                    _lastPanelEventValue = value;
                    wmiDisplay.SetBrightnessSilently(value);
                    return;
                }

                _wmiRevertingTo = null; // echo lost, or a real change overtook it
            }

            var target = Displays.Target;

            if (_settings.RevertNonTargetPanelChanges
                && !ReferenceEquals(target, wmiDisplay)
                && !_settings.SyncMonitors)
            {
                // Windows changed the panel, but the keyboard targets another
                // display - the key press should never have touched this panel
                // (monitor.sys handles the ACPI hotkey in kernel mode; there is no
                // user-mode opt-out). This holds whether the panel is excluded
                // entirely or merely included-but-not-targeted. Forward each
                // event's delta to the actual target (native steps arrive as +-10,
                // vendor smooth-hold as +-1, so forward the delta, not a fixed
                // step) and revert the panel immediately - per event, which users
                // find far less confusing than a post-burst rollback. The panel
                // model stays pinned at the pre-press value.
                var delta = value - (_lastPanelEventValue ?? value);
                _lastPanelEventValue = value;
                if (delta == 0)
                {
                    return;
                }

                if (target is not null)
                {
                    ApplyBrightness(Math.Clamp(target.Brightness + delta, 0, 100));
                }

                var baseline = wmiDisplay.Brightness;
                Log.Write($"Reverting Windows-made change on non-target panel -> {baseline}%");
                _wmiRevertingTo = baseline;
                wmiDisplay.Controller.SetBrightness(baseline);
                return;
            }

            // Accept the change: the panel is the target, sync is on, or the
            // non-target revert is disabled (the default - vendor drivers deliver
            // these events unreliably, so a mis-tracking revert path would do
            // more harm than letting Windows own the panel).
            if (Math.Abs(wmiDisplay.Brightness - value) <= 1)
            {
                return; // feedback from our own write, or nothing to do
            }

            wmiDisplay.SetBrightnessSilently(value);
            _lastPanelEventValue = value;
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

    #region Boundary presses (panel pinned at 0/100)

    // When the panel is already at 0 or 100, Windows swallows the matching key
    // press without changing anything - no brightness event fires, so the mirror
    // path never runs. The one observable signal left is the native OSD, which
    // Windows shows anyway. A show not accompanied by a brightness event is a
    // boundary press; step the target in the clamped direction.
    //
    // Race-free matching, no wall-clock windows: the show and any accompanying
    // brightness event are both marshalled onto this same DispatcherQueue, and
    // the timer tick that decides is queued behind them - so by tick time the
    // event handler has either run (clearing _boundaryCheckPending) or there
    // genuinely was no event.

    private void OnNativeOsdShown()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var panel = Displays.Displays.FirstOrDefault(d => d.Controller is WmiDisplayController);
            if (panel is null
                || ReferenceEquals(Displays.Target, panel)
                || _settings.SyncMonitors
                || !_settings.RevertNonTargetPanelChanges)
            {
                return;
            }

            _boundaryCheckPending = true;
            _boundaryTimer.Stop();
            _boundaryTimer.Start();
        });
    }

    private async Task HandleBoundaryPressAsync()
    {
        // A real change's event handler ran before this tick (FIFO) and cleared
        // the flag; only a genuine boundary press leaves it set.
        if (!_boundaryCheckPending)
        {
            return;
        }

        _boundaryCheckPending = false;

        var panel = Displays.Displays.FirstOrDefault(d => d.Controller is WmiDisplayController);
        if (panel is null)
        {
            return;
        }

        var live = _lastPanelEventValue ?? await panel.Controller.RefreshBrightnessAsync();
        var direction = live switch
        {
            <= 0 => BrightnessDirection.Down,
            >= 100 => BrightnessDirection.Up,
            _ => (BrightnessDirection?)null,
        };

        // Not at an extreme: the press should have produced an event. Most likely
        // this OSD was the volume flyout - not ours to mirror.
        if (direction is null)
        {
            return;
        }

        var target = Displays.Target;
        if (target is null || ReferenceEquals(target, panel))
        {
            return;
        }

        Log.Write($"Boundary press inferred from native OSD (panel pinned at {live}%) -> {direction}");
        ApplyBrightness(Math.Clamp(target.Brightness + _settings.BrightnessStep * (int)direction, 0, 100));
    }

    #endregion

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
        NativeOsdSuppressor.NativeOsdShown -= OnNativeOsdShown;
        if (_tray is not null)
        {
            _tray.RawInputMessage -= _hook.ProcessRawInputMessage;
            _tray.DisplayChangeBroadcast -= OnDisplayChangeBroadcast;
            _tray.BrightnessSettingChanged -= OnPowerBrightnessEvent;
            _tray = null;
        }

        _suppressWatchdog.Stop();
        _restoreTimer.Stop();
        _hotplugTimer.Stop();
        _pollTimer.Stop();
        _boundaryTimer.Stop();

        _wmiWatcher.Dispose();
        _hook.Dispose();
        NativeOsdSuppressor.Restore();
    }
}
