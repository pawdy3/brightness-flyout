using System.Threading.Tasks;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// Serializes brightness writes to one background writer per display and coalesces
/// bursts: only the newest pending value is ever written (a held key would
/// otherwise queue dozens of near-identical DDC/CI or WMI writes, both of which
/// are slow and visible).
/// </summary>
internal sealed class CoalescingBrightnessWriter
{
    private readonly object _gate = new();
    private readonly System.Action<int> _write;
    private bool _running;
    private int _pending = -1;
    private int _lastWritten = -1;

    public CoalescingBrightnessWriter(System.Action<int> write)
    {
        _write = write;
    }

    /// <summary>Queues a value; returns immediately. The writer loop picks up only
    /// the latest value and stops when nothing new is pending.</summary>
    public void Set(int value)
    {
        lock (_gate)
        {
            _pending = value;
            if (_running)
            {
                return;
            }

            _running = true;
        }

        Task.Run(Loop);
    }

    private void Loop()
    {
        while (true)
        {
            int value;
            lock (_gate)
            {
                if (_pending == _lastWritten)
                {
                    _running = false;
                    return;
                }

                value = _pending;
            }

            _write(value);
            _lastWritten = value;
        }
    }
}
