namespace BrightFlyout.Services.Displays;

/// <summary>
/// In-memory display used by the integration test suite (enabled via the
/// BRIGHTFLYOUT_SIMULATE_DISPLAYS environment variable): no hardware is touched,
/// so the full key -> brightness -> OSD chain can be tested on any machine.
/// </summary>
internal sealed class SimulatedDisplayController : IDisplayController
{
    private int _brightness;

    public SimulatedDisplayController(string id, string name, string technology, int initialBrightness = 50)
    {
        Id = id;
        Name = name;
        Technology = technology;
        _brightness = initialBrightness;
    }

    public string Id { get; }
    public string Name { get; }
    public string Technology { get; }
    public bool IsAvailable => true;
    public int? CurrentBrightness => _brightness;

    public Task<int?> RefreshBrightnessAsync() => Task.FromResult<int?>(_brightness);

    public void SetBrightness(int percent) => _brightness = Math.Clamp(percent, 0, 100);

    /// <summary>The two displays a simulated run gets: one external-looking
    /// (DDC/CI), one internal-looking (WMI), so multi-display UI is covered too.</summary>
    public static List<IDisplayController> CreateSet() =>
    [
        new SimulatedDisplayController("sim|ext", "Simulated monitor", "DDC/CI (simulated)"),
        new SimulatedDisplayController("sim|int", "Simulated internal display", "WMI (simulated)"),
    ];
}
