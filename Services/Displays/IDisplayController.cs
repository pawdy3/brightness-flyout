using System.Threading.Tasks;

namespace BrightFlyout.Services.Displays;

/// <summary>
/// Controls the brightness of a single display, regardless of the underlying
/// transport (DDC/CI for external monitors, WMI for internal panels).
/// Implementations re-resolve their hardware handle per operation so they
/// survive hot-plug and sleep, and coalesce rapid writes.
/// </summary>
public interface IDisplayController
{
    /// <summary>Stable identifier across re-enumerations (device name + description).</summary>
    string Id { get; }

    /// <summary>Friendly display name (e.g. monitor description).</summary>
    string Name { get; }

    /// <summary>Human-readable transport name, e.g. "DDC/CI" or "WMI (internal panel)".</summary>
    string Technology { get; }

    bool IsAvailable { get; }

    /// <summary>Last known brightness (0-100), null if never read.</summary>
    int? CurrentBrightness { get; }

    /// <summary>Reads the current brightness from the hardware.</summary>
    Task<int?> RefreshBrightnessAsync();

    /// <summary>Queues a brightness write (0-100); intermediate values are coalesced.</summary>
    void SetBrightness(int percent);
}
