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
            if (!NativeOsdSuppressor.IsSuppressed)
            {
                NativeOsdSuppressor.Suppress();
            }
        };

        // Bring the native OSD back only after our flyout has fully disappeared,
        // otherwise both are on screen at once.
        _restoreTimer = _dispatcherQueue.CreateTimer();
        _restoreTimer.IsRepeating = false;
        _restoreTimer.Interval = TimeSpan.FromMilliseconds(400);
        _restoreTimer.Tick += (_, _) => NativeOsdSuppressor.Restore();
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
        _ = RefreshDisplaysAsync();
    }

    public async Task RefreshDisplaysAsync()
    {
        await Displays.RefreshAsync();

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
        _dispatcherQueue.TryEnqueue(() =>
        {
            var target = Displays.Target;
            if (target is null)
            {
                return;
            }

            var value = Math.Clamp(target.Brightness + _settings.BrightnessStep * (int)direction, 0, 100);
            ApplyBrightness(value);
        });
    }

    /// <summary>Laptop path: Windows itself changed the internal panel brightness
    /// (mobile systems consume the HID usages before any hook). Mirror the change.</summary>
    private void OnWmiBrightnessChanged(int value)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var wmiDisplay = Displays.Displays.FirstOrDefault(d => d.Controller is WmiDisplayController);
            if (wmiDisplay is null || Math.Abs(wmiDisplay.Brightness - value) <= 1)
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
        _wmiWatcher.Dispose();
        _hook.Dispose();
        NativeOsdSuppressor.Restore();
    }
}
