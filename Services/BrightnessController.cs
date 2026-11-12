using System.ComponentModel;
using BrightFlyout.Native;
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
    private readonly WmiBrightnessWatcher _wmiWatcher = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _suppressWatchdog;
    private readonly DispatcherQueueTimer _restoreTimer;
    private readonly DispatcherQueueTimer _hotplugTimer;
    private NativeMethods.SubclassProc? _displayChangeSubclassProc;

    // Excluded-panel revert state (see OnWmiBrightnessChanged).
    private readonly DispatcherQueueTimer _wmiRevertTimer;
    private int? _wmiRevertValue;
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

        // When the internal panel is excluded from control, Windows still changes
        // it on laptop key presses. Revert those changes once the press burst ends.
        _wmiRevertTimer = _dispatcherQueue.CreateTimer();
        _wmiRevertTimer.IsRepeating = false;
        _wmiRevertTimer.Interval = TimeSpan.FromMilliseconds(250);
        _wmiRevertTimer.Tick += (_, _) =>
        {
            if (_wmiRevertValue is not { } revertTo)
            {
                return;
            }

            _wmiRevertValue = null;
            var wmiDisplay = Displays.Displays.FirstOrDefault(d => d.Controller is WmiDisplayController);
            if (wmiDisplay is not null)
            {
                Log.Write($"Reverting Windows-made change on excluded panel -> {revertTo}%");
                _wmiRevertingTo = revertTo;
                wmiDisplay.Controller.SetBrightness(revertTo);
                wmiDisplay.SetBrightnessSilently(revertTo);
            }
        };
    }

    private IntPtr DisplayChangeSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == NativeMethods.WM_DISPLAYCHANGE)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                _hotplugTimer.Stop();
                _hotplugTimer.Start();
            });
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void OnOsdAutoHidden()
    {
        _suppressWatchdog.Stop();
        _restoreTimer.Stop();
        _restoreTimer.Start();
    }

    public void Start()
    {
        _hook.Install(_osd.Hwnd);

        // The OSD window is always alive (hidden), so it receives display-change
        // broadcasts even with no visible UI.
        _displayChangeSubclassProc = DisplayChangeSubclassProc;
        NativeMethods.SetWindowSubclass(_osd.Hwnd, _displayChangeSubclassProc, (UIntPtr)3, UIntPtr.Zero);

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
                // The panel is excluded, but Windows changed it anyway. Revert the
                // change (debounced per burst) and forward the key press to the
                // actual target display instead.
                var direction = Math.Sign(value - wmiDisplay.Brightness);
                _wmiRevertValue ??= wmiDisplay.Brightness;
                wmiDisplay.SetBrightnessSilently(value);
                _wmiRevertTimer.Stop();
                _wmiRevertTimer.Start();

                if (direction != 0 && Displays.Target is { } target && target != wmiDisplay)
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

    public void Dispose()
    {
        _osd.AutoHidden -= OnOsdAutoHidden;
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _suppressWatchdog.Stop();
        _restoreTimer.Stop();
        _hotplugTimer.Stop();
        if (_displayChangeSubclassProc is not null)
        {
            NativeMethods.RemoveWindowSubclass(_osd.Hwnd, _displayChangeSubclassProc, (UIntPtr)3);
            _displayChangeSubclassProc = null;
        }

        _wmiWatcher.Dispose();
        _hook.Dispose();
        NativeOsdSuppressor.Restore();
    }
}
