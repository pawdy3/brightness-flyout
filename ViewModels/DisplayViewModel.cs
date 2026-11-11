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
        _brightness = controller.CurrentBrightness ?? 50;
    }

    public IDisplayController Controller { get; }

    public string Id => Controller.Id;
    public string Name => Controller.Name;
    public string Technology => Controller.Technology;

    /// <summary>Bound two-way to the display's slider.</summary>
    [ObservableProperty]
    private int _brightness;

    [ObservableProperty]
    private bool _isTarget;

    /// <summary>Whether this display is controlled (keyboard, sync, flyout).</summary>
    [ObservableProperty]
    private bool _isIncluded = true;

    partial void OnIsIncludedChanged(bool value) => _includeChanged(this, value);

    // x:Bind in a Window DataTemplate can't use value converters, hence a property.
    public Visibility TargetMarkerVisibility => IsTarget ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsTargetChanged(bool value) => OnPropertyChanged(nameof(TargetMarkerVisibility));

    [ObservableProperty]
    private bool _isAvailable = true;

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
