using BrightFlyout.Services.Displays;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace BrightFlyout.ViewModels;

/// <summary>
/// UI-facing wrapper around an <see cref="IDisplayController"/>; binds to the OSD
/// sliders and the settings window.
/// </summary>
public partial class DisplayViewModel : ObservableObject
{
    private readonly Action<DisplayViewModel, int> _setRequested;
    private readonly Action<DisplayViewModel, bool> _includeChanged;
    private bool _suppressNotification;

    public DisplayViewModel(IDisplayController controller,
        Action<DisplayViewModel, int> setRequested,
        Action<DisplayViewModel, bool> includeChanged)
    {
        Controller = controller;
        _setRequested = setRequested;
        _includeChanged = includeChanged;
        Brightness = controller.CurrentBrightness ?? 50;
    }

    public IDisplayController Controller { get; }

    public string Id => Controller.Id;
    public string Name => Controller.Name;
    public string Technology => Controller.Technology;

    /// <summary>Bound two-way to the display's slider.</summary>
    [ObservableProperty]
    public partial int Brightness { get; set; }

    [ObservableProperty]
    public partial bool IsTarget { get; set; }

    /// <summary>Whether this display is controlled (keyboard, sync, flyout).</summary>
    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    partial void OnIsIncludedChanged(bool value) => _includeChanged(this, value);

    // Exposed as a property so row XAML needs no value converter.
    public Visibility TargetMarkerVisibility => IsTarget ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsTargetChanged(bool value) => OnPropertyChanged(nameof(TargetMarkerVisibility));

    [ObservableProperty]
    public partial bool IsAvailable { get; set; } = true;

    public string Summary => $"{Name} - {Technology}";

    partial void OnBrightnessChanged(int value)
    {
        if (!_suppressNotification)
        {
            Controller.SetBrightness(value);
            _setRequested(this, value);
        }
    }

    /// <summary>Sets brightness from code (keyboard steps, sync); keeps the slider in sync.</summary>
    public void SetBrightness(int value)
    {
        Controller.SetBrightness(value);
        _suppressNotification = true;
        Brightness = Math.Clamp(value, 0, 100);
        _suppressNotification = false;
    }

    /// <summary>Updates the cached value without writing to the hardware (the OS changed
    /// the brightness itself, e.g. laptop brightness keys handled by Windows).</summary>
    public void SetBrightnessSilently(int value)
    {
        _suppressNotification = true;
        Brightness = Math.Clamp(value, 0, 100);
        _suppressNotification = false;
    }

    public async Task RefreshAsync()
    {
        var value = await Controller.RefreshBrightnessAsync();
        IsAvailable = Controller.IsAvailable;
        if (value is { } v)
        {
            _suppressNotification = true;
            Brightness = v;
            _suppressNotification = false;
        }
    }
}
