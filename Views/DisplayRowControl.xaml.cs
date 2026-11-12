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

    public DisplayViewModel Display
    {
        get => (DisplayViewModel)GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    public DisplayRowControl()
    {
        InitializeComponent();
    }
}
