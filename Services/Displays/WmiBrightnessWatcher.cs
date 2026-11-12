using System;
using WmiLight;

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
    private WmiConnection? _connection;
    private WmiEventSubscription? _subscription;

    /// <summary>Raised on a WMI thread with the new brightness (0-100).</summary>
    public event Action<int>? BrightnessChanged;

    /// <summary>Starts watching. Logs and gives up when the system has no internal panel.</summary>
    public void Start()
    {
        if (_subscription is not null)
        {
            return;
        }

        try
        {
            _connection = new WmiConnection(@"root\wmi");
            _subscription = _connection.CreateEventSubscription(
                "SELECT * FROM WmiMonitorBrightnessEvent", OnBrightnessEvent);
            Log.Write("WMI brightness watcher started");
        }
        catch (Exception ex)
        {
            Log.Write("WMI brightness watcher unavailable", ex);
            Dispose();
        }
    }

    private void OnBrightnessEvent(WmiObject eventObject)
    {
        try
        {
            var brightness = Convert.ToInt32(eventObject["Brightness"]);
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
            _subscription?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _connection?.Dispose();
        }
        catch
        {
            // ignore
        }

        _subscription = null;
        _connection = null;
    }
}
