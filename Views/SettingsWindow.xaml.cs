using System.ComponentModel;
using BrightFlyout.Services;
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
    private bool _initializing = true;
    private bool _sliderUpdating;

    public SettingsWindow(App app)
    {
        _app = app;
        _settings = app.Settings;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(620, 900));

        LoadFromSettings();
        UpdateMonitorStatus();

        _settings.PropertyChanged += OnSettingsPropertyChanged;
        _app.Controller.BrightnessChanged += OnBrightnessChanged;

        Closed += OnClosed;
        _initializing = false;
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
        BackgroundComboBox.SelectedIndex = (int)_settings.BackgroundStyle;
        FillComboBox.SelectedIndex = (int)_settings.FillStyle;
        BackgroundColorPicker.Color = ParseColorOrDefault(_settings.CustomBackgroundColor, Color.FromArgb(0xE6, 0x1F, 0x1F, 0x1F));
        FillColorPicker.Color = ParseColorOrDefault(_settings.CustomFillColor, Color.FromArgb(0xFF, 0xF5, 0xC5, 0x42));
        UpdateColorPickerVisibility();

        if (_app.Controller.Monitor.CurrentBrightness is { } brightness)
        {
            SetSlider(brightness);
        }

        ApplyTheme();
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void UpdateMonitorStatus()
    {
        var monitor = _app.Controller.Monitor;
        MonitorStatusText.Text = monitor.IsAvailable
            ? $"Connected: {monitor.MonitorName}"
            : "No DDC/CI capable monitor found on the primary display. Brightness changes will have no effect.";
    }

    private void OnBrightnessChanged(int value)
    {
        SetSlider(value);
        UpdateMonitorStatus();
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
            _app.Controller.SetBrightness((int)e.NewValue, showOsd: false);
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
            _settings.CustomBackgroundColor = ToHex(args.NewColor);
        }
    }

    private void OnFillColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_initializing)
        {
            _settings.CustomFillColor = ToHex(args.NewColor);
        }
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        _app.Controller.PreviewOsd();
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
        _app.OnSettingsWindowClosed();
    }

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseColorOrDefault(string text, Color fallback)
    {
        try
        {
            var hex = text.Trim().TrimStart('#');
            if (hex.Length == 8)
            {
                var v = Convert.ToUInt32(hex, 16);
                return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }

            if (hex.Length == 6)
            {
                var v = Convert.ToUInt32(hex, 16);
                return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }
        }
        catch
        {
            // fall through
        }

        return fallback;
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
