using System.Management;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// Watches WmiMonitorBrightnessEvent, which Windows raises when it changes an
/// internal panel's brightness itself (e.g. laptop brightness keys on mobile
/// systems, where the OS consumes the key before any hook can see it). This lets
/// us mirror the change: update our state, show our OSD, and propagate to other
/// displays when sync is on.
/// </summary>
internal sealed class WmiBrightnessWatcher : IDisposable
{
    private ManagementEventWatcher? _watcher;

    /// <summary>Raised on a WMI thread pool thread with the new brightness (0-100).</summary>
    public event Action<int>? BrightnessChanged;

    /// <summary>Starts watching. No-op when the system has no internal panel.</summary>
    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }

        try
        {
            _watcher = new ManagementEventWatcher(@"root\wmi",
                "SELECT * FROM WmiMonitorBrightnessEvent");
            _watcher.EventArrived += OnEventArrived;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Write("WMI brightness watcher unavailable", ex);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var brightness = Convert.ToInt32(e.NewEvent["Brightness"]);
            BrightnessChanged?.Invoke(Math.Clamp(brightness, 0, 100));
        }
        catch
        {
            // malformed event; ignore
        }
    }

    public void Dispose()
    {
        try
        {
            _watcher?.Stop();
        }
        catch
        {
            // ignore
        }

        _watcher?.Dispose();
        _watcher = null;
    }
}
