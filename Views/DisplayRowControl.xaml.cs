using BrightFlyout.ViewModels;
using Microsoft.UI.Xaml;

namespace BrightFlyout.Views;

/// <summary>
/// One declarative display row. Rows are added to the ItemsControl.Items in code
/// (a UserControl is a WinRT type, safe under NativeAOT), while the row's content
/// stays fully declarative via x:Bind against the Display dependency property.
/// </summary>
public sealed partial class DisplayRowControl
{
    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        nameof(Display), typeof(DisplayViewModel), typeof(DisplayRowControl),
        new PropertyMetadata(null));

    /// <summary>With sync on, per-display sliders are inert (all displays move
    /// together), so they are disabled instead of pretending to work.</summary>
    public static readonly DependencyProperty SlidersEnabledProperty = DependencyProperty.Register(
        nameof(SlidersEnabled), typeof(bool), typeof(DisplayRowControl),
        new PropertyMetadata(true));

    public DisplayViewModel Display
    {
        get => (DisplayViewModel)GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    public bool SlidersEnabled
    {
        get => (bool)GetValue(SlidersEnabledProperty);
        set => SetValue(SlidersEnabledProperty, value);
    }

    public DisplayRowControl()
    {
        InitializeComponent();
    }
}
