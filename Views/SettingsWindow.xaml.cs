using System.ComponentModel;
using BrightFlyout.Services;
using BrightFlyout.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Windows.UI;

namespace BrightFlyout.Views;

public sealed partial class SettingsWindow : Window
{
    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "BrightFlyout";

    private readonly App _app;
    private readonly SettingsService _settings;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly List<string> _defaultMonitorIds = [];
    private bool _initializing = true;
    private bool _sliderUpdating;

    private string VersionText { get; } =
        $"BrightFlyout {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}";

    public SettingsWindow(App app)
    {
        _app = app;
        _settings = app.Settings;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(620, 900));

        LoadFromSettings();
        RebuildDisplayList();
        UpdateDisplayStatus();

        _settings.PropertyChanged += OnSettingsPropertyChanged;
        _app.Controller.BrightnessChanged += OnBrightnessChanged;
        _app.Controller.Displays.Changed += OnDisplaysChanged;

        Closed += OnClosed;
        _initializing = false;

        // Pick up hot-plugged displays whenever the window opens.
        _ = _app.Controller.RefreshDisplaysAsync();
    }

    private void LoadFromSettings()
    {
        StepNumberBox.Value = _settings.BrightnessStep;
        DurationNumberBox.Value = _settings.OsdDurationMs / 1000.0;
        StartupToggle.IsOn = IsStartupEnabled();
        RawInputToggle.IsOn = _settings.EnableRawInput;
        VkDownTextBox.Text = _settings.BrightnessDownVk;
        VkUpTextBox.Text = _settings.BrightnessUpVk;
        ThemeComboBox.SelectedIndex = (int)_settings.Theme;
        PlacementComboBox.SelectedIndex = (int)_settings.OsdPlacement;
        BackgroundComboBox.SelectedIndex = (int)_settings.BackgroundStyle;
        FillComboBox.SelectedIndex = (int)_settings.FillStyle;
        BackgroundColorPicker.Color = ColorHex.ParseOrDefault(_settings.CustomBackgroundColor, Color.FromArgb(0xE6, 0x1F, 0x1F, 0x1F));
        FillColorPicker.Color = ColorHex.ParseOrDefault(_settings.CustomFillColor, Color.FromArgb(0xFF, 0xF5, 0xC5, 0x42));
        PercentToggle.IsOn = _settings.ShowPercentText;
        ClickableBarToggle.IsOn = _settings.ClickableFlyoutBar;
        MultiMonitorToggle.IsOn = _settings.MultiMonitorEnabled;
        UpdateColorPickerVisibility();

        if (_app.Controller.Displays.Target is { } target)
        {
            SetSlider(target.Brightness);
        }

        ApplyTheme();
    }

    // NativeAOT: rows are UserControls added to Items in code; the combo holds
    // display names with a parallel id list. No managed collections cross the ABI.
    // Rebuilt only on membership changes - value changes (brightness steps) arrive
    // via the same event, and recreating rows would visibly flash the checkboxes.
    private string _rowsFingerprint = string.Empty;

    private void RebuildDisplayList()
    {
        var fingerprint = string.Join("|", _app.Controller.Displays.Displays.Select(d => d.Id));
        if (fingerprint == _rowsFingerprint)
        {
            return;
        }

        _rowsFingerprint = fingerprint;
        DisplayStatusItems.Items.Clear();
        _defaultMonitorIds.Clear();
        DefaultMonitorComboBox.Items.Clear();

        foreach (var display in _app.Controller.Displays.Displays)
        {
            DisplayStatusItems.Items.Add(new DisplaySettingsRow { Display = display });
            _defaultMonitorIds.Add(display.Id);
            DefaultMonitorComboBox.Items.Add(display.Name);
        }

        var index = _defaultMonitorIds.IndexOf(_settings.DefaultMonitorId);
        DefaultMonitorComboBox.SelectedIndex = index >= 0 ? index
            : _defaultMonitorIds.Count > 0 ? 0 : -1;
    }

    /// <summary>Applies the app's effective theme (system theme is tracked by App).</summary>
    public void SetTheme(ElementTheme theme)
    {
        Root.RequestedTheme = theme;

        var isDark = theme == ElementTheme.Dark;
        ThemeService.ApplyDarkModeFrame(_hwnd, isDark);
        ThemeService.ApplyCaptionButtonColors(_appWindow, isDark);
    }

    private void ApplyTheme()
    {
        SetTheme(_app.EffectiveTheme());
    }

    private void UpdateDisplayStatus()
    {
        NoDisplaysText.Visibility = _app.Controller.Displays.Displays.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnDisplaysChanged()
    {
        UpdateDisplayStatus();
        RebuildDisplayList();
        if (_app.Controller.Displays.Target is { } target)
        {
            SetSlider(target.Brightness);
        }
    }

    private void OnBrightnessChanged(int value)
    {
        SetSlider(value);
        UpdateDisplayStatus();
    }

    private void SetSlider(int value)
    {
        _sliderUpdating = true;
        BrightnessSlider.Value = value;
        _sliderUpdating = false;
    }

    #region Event handlers

    private void OnBrightnessSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_initializing && !_sliderUpdating)
        {
            _app.Controller.SetTargetBrightness((int)e.NewValue);
        }
    }

    private void OnMultiMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            _settings.MultiMonitorEnabled = MultiMonitorToggle.IsOn;
        }
    }

    private void OnDefaultMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing
            && DefaultMonitorComboBox.SelectedIndex is var index
            && index >= 0 && index < _defaultMonitorIds.Count)
        {
            _settings.DefaultMonitorId = _defaultMonitorIds[index];
        }
    }

    private void OnStepChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initializing && !double.IsNaN(args.NewValue))
        {
            _settings.BrightnessStep = (int)args.NewValue;
        }
    }

    private void OnDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_initializing && !double.IsNaN(args.NewValue))
        {
            _settings.OsdDurationMs = (int)(args.NewValue * 1000);
        }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            SetStartupEnabled(StartupToggle.IsOn);
            _settings.RunAtStartup = StartupToggle.IsOn;
        }
    }

    private void OnRawInputToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            _settings.EnableRawInput = RawInputToggle.IsOn;
        }
    }

    private void OnVkTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _settings.BrightnessDownVk = VkDownTextBox.Text;
        _settings.BrightnessUpVk = VkUpTextBox.Text;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && ThemeComboBox.SelectedIndex >= 0)
        {
            _settings.Theme = (AppTheme)ThemeComboBox.SelectedIndex;
            ApplyTheme();
        }
    }

    private void OnPlacementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && PlacementComboBox.SelectedIndex >= 0)
        {
            _settings.OsdPlacement = (OsdPlacement)PlacementComboBox.SelectedIndex;
        }
    }

    private void OnBackgroundStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && BackgroundComboBox.SelectedIndex >= 0)
        {
            _settings.BackgroundStyle = (OsdBackgroundStyle)BackgroundComboBox.SelectedIndex;
            UpdateColorPickerVisibility();
        }
    }

    private void OnFillStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && FillComboBox.SelectedIndex >= 0)
        {
            _settings.FillStyle = (OsdFillStyle)FillComboBox.SelectedIndex;
            UpdateColorPickerVisibility();
        }
    }

    private void OnBackgroundColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_initializing)
        {
            _settings.CustomBackgroundColor = ColorHex.ToHex(args.NewColor);
        }
    }

    private void OnFillColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_initializing)
        {
            _settings.CustomFillColor = ColorHex.ToHex(args.NewColor);
        }
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        _app.Controller.PreviewOsd();
    }

    private async void OnRefreshDisplaysClick(object sender, RoutedEventArgs e)
    {
        RefreshDisplaysButton.IsEnabled = false;
        try
        {
            await _app.Controller.RefreshDisplaysAsync();
        }
        finally
        {
            RefreshDisplaysButton.IsEnabled = true;
        }
    }

    private void OnPercentToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            _settings.ShowPercentText = PercentToggle.IsOn;
        }
    }

    private void OnClickableBarToggled(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
        {
            _settings.ClickableFlyoutBar = ClickableBarToggle.IsOn;
        }
    }

    #endregion

    private void UpdateColorPickerVisibility()
    {
        BackgroundColorPicker.Visibility = BackgroundComboBox.SelectedIndex == (int)OsdBackgroundStyle.SolidColor
            ? Visibility.Visible
            : Visibility.Collapsed;
        FillColorPicker.Visibility = FillComboBox.SelectedIndex == (int)OsdFillStyle.CustomColor
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsService.Theme))
        {
            DispatcherQueue.TryEnqueue(ApplyTheme);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _app.Controller.BrightnessChanged -= OnBrightnessChanged;
        _app.Controller.Displays.Changed -= OnDisplaysChanged;
        _app.OnSettingsWindowClosed();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: false);
        return key?.GetValue(StartupValueName) is not null;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                key.SetValue(StartupValueName, $"\"{path}\"");
            }
        }
        else
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }
}
