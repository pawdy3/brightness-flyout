using System.Threading.Tasks;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// Serializes brightness writes to one background writer per display and coalesces
/// bursts: only the newest pending value is ever written (a held key would
/// otherwise queue dozens of near-identical DDC/CI or WMI writes, both of which
/// are slow and visible).
///
/// Every queued value is written once, even if equal to the previous write:
/// the hardware can be changed externally (Windows itself moves laptop panels;
/// monitors have their own buttons), so "same as last written" never means
/// "already at that value". Coalescing collapses bursts - nothing more.
/// </summary>
internal sealed class CoalescingBrightnessWriter
{
    private readonly object _gate = new();
    private readonly System.Action<int> _write;
    private bool _running;
    private bool _hasPending;
    private int _pending;

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
            _hasPending = true;
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
                if (!_hasPending)
                {
                    _running = false;
                    return;
                }

                _hasPending = false;
                value = _pending;
            }

            _write(value);
        }
    }
}
