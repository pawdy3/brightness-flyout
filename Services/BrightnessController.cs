using BrightFlyout.Native;
using BrightFlyout.Views;
using Microsoft.UI.Dispatching;

namespace BrightFlyout.Services;

/// <summary>
/// Orchestrates the app: keyboard hook events -> DDC/CI brightness change ->
/// custom OSD + transient suppression of the native Windows OSD.
/// </summary>
internal sealed class BrightnessController : IDisposable
{
    private readonly SettingsService _settings;
    private readonly OsdWindow _osd;
    private readonly DdcCiMonitorService _monitor = new();
    private readonly KeyboardHookService _hook;
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>Raised (on the UI thread) whenever the brightness value changes.</summary>
    public event Action<int>? BrightnessChanged;

    public DdcCiMonitorService Monitor => _monitor;

    public BrightnessController(SettingsService settings, OsdWindow osd)
    {
        _settings = settings;
        _osd = osd;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _hook = new KeyboardHookService(settings);
        _hook.BrightnessStepRequested += OnBrightnessStepRequested;
        _osd.AutoHidden += NativeOsdSuppressor.Restore;
    }

    public void Start()
    {
        _hook.Install(_osd.Hwnd);

        // Warm the brightness cache in the background; DDC reads are slow.
        Task.Run(() =>
        {
            var value = _monitor.ReadBrightness();
            if (value is { } v)
            {
                _dispatcherQueue.TryEnqueue(() => BrightnessChanged?.Invoke(v));
            }
        });
    }

    private void OnBrightnessStepRequested(BrightnessDirection direction)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            var step = _settings.BrightnessStep * (int)direction;
            var value = _monitor.StepBrightness(step);

            // Hide the native Windows OSD only for the short window in which it
            // could appear; restored on OSD auto-hide and on app exit.
            NativeOsdSuppressor.Suppress();

            _osd.ShowBrightness(value);
            BrightnessChanged?.Invoke(value);
        });
    }

    /// <summary>Directly sets brightness (used by the settings slider). Shows the OSD.</summary>
    public void SetBrightness(int value, bool showOsd = true)
    {
        var applied = _monitor.SetBrightness(value);
        if (showOsd)
        {
            _osd.ShowBrightness(applied);
        }

        BrightnessChanged?.Invoke(applied);
    }

    /// <summary>Preview the OSD without touching brightness or suppressing the native one.</summary>
    public void PreviewOsd()
    {
        _osd.ShowBrightness(_monitor.CurrentBrightness ?? 65);
    }

    public void Dispose()
    {
        _osd.AutoHidden -= NativeOsdSuppressor.Restore;
        _hook.Dispose();
        NativeOsdSuppressor.Restore();
    }
}
