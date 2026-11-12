using BrightFlyout.ViewModels;
using Microsoft.UI.Xaml;

namespace BrightFlyout.Views;

/// <summary>Declarative display row for the settings window (see DisplayRowControl
/// for why rows are UserControls populated in code).</summary>
public sealed partial class DisplaySettingsRow
{
    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        nameof(Display), typeof(DisplayViewModel), typeof(DisplaySettingsRow),
        new PropertyMetadata(null));

    public DisplayViewModel Display
    {
        get => (DisplayViewModel)GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    public DisplaySettingsRow()
    {
        InitializeComponent();
    }
}
