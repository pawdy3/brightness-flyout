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
    private readonly OsdWindow _osd;
    private readonly KeyboardHookService _hook;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _suppressWatchdog;
    private readonly DispatcherQueueTimer _restoreTimer;

    /// <summary>Raised (on the UI thread) whenever the target display's brightness changes.</summary>
    public event Action<int>? BrightnessChanged;

    public DisplayManager Displays { get; }

    public BrightnessController(SettingsService settings, OsdWindow osd)
    {
        _settings = settings;
        _osd = osd;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        Displays = new DisplayManager(settings, _dispatcherQueue);
        Displays.Changed += OnDisplaysChanged;

        _hook = new KeyboardHookService(settings);
        _hook.BrightnessStepRequested += OnBrightnessStepRequested;
        _osd.AutoHidden += OnOsdAutoHidden;

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
            target.SetBrightness(value);

            // Hide the native Windows OSD only for the short window in which it
            // could appear; restored on OSD auto-hide and on app exit.
            NativeOsdSuppressor.Suppress();
            _restoreTimer.Stop();
            _suppressWatchdog.Start();

            _osd.ShowBrightness(value);
            BrightnessChanged?.Invoke(value);
        });
    }

    /// <summary>Directly sets the target display's brightness (settings slider).</summary>
    public void SetTargetBrightness(int value, bool showOsd = false)
    {
        var target = Displays.Target;
        if (target is null)
        {
            return;
        }

        target.SetBrightness(value);
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
        _suppressWatchdog.Stop();
        _restoreTimer.Stop();
        _hook.Dispose();
        NativeOsdSuppressor.Restore();
    }
}
