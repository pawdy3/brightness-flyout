using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BrightFlyout.Services;

public enum OsdBackgroundStyle
{
    Acrylic,
    SolidColor
}

public enum OsdFillStyle
{
    SystemAccent,
    CustomColor
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>
/// JSON-backed, observable application settings persisted to
/// %LOCALAPPDATA%\BrightFlyout\settings.json.
/// </summary>
public partial class SettingsService : ObservableObject
{
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrightFlyout");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private bool _suppressSave = true;

    public SettingsService()
    {
        PropertyChanged += OnPropertyChanged;
    }

    // --- Behavior ---

    [ObservableProperty]
    [property: JsonPropertyName("brightnessStep")]
    private int _brightnessStep = 5;

    [ObservableProperty]
    [property: JsonPropertyName("osdDurationMs")]
    private int _osdDurationMs = 2000;

    [ObservableProperty]
    [property: JsonPropertyName("runAtStartup")]
    private bool _runAtStartup;

    // --- Keyboard hook ---

    [ObservableProperty]
    [property: JsonPropertyName("enableRawInput")]
    private bool _enableRawInput = true;

    /// <summary>VK code (hex or decimal) watched by the WH_KEYBOARD_LL hook for brightness down.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("brightnessDownVk")]
    private string _brightnessDownVk = "0x9E";

    /// <summary>VK code (hex or decimal) watched by the WH_KEYBOARD_LL hook for brightness up.</summary>
    [ObservableProperty]
    [property: JsonPropertyName("brightnessUpVk")]
    private string _brightnessUpVk = "0x9F";

    // --- Appearance ---

    [ObservableProperty]
    [property: JsonPropertyName("theme")]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    [property: JsonPropertyName("backgroundStyle")]
    private OsdBackgroundStyle _backgroundStyle = OsdBackgroundStyle.Acrylic;

    [ObservableProperty]
    [property: JsonPropertyName("customBackgroundColor")]
    private string _customBackgroundColor = "#E61F1F1F";

    [ObservableProperty]
    [property: JsonPropertyName("fillStyle")]
    private OsdFillStyle _fillStyle = OsdFillStyle.SystemAccent;

    [ObservableProperty]
    [property: JsonPropertyName("customFillColor")]
    private string _customFillColor = "#FFF5C542";

    [ObservableProperty]
    [property: JsonPropertyName("showPercentText")]
    private bool _showPercentText = true;

    // --- Multiple displays ---

    [ObservableProperty]
    [property: JsonPropertyName("multiMonitorEnabled")]
    private bool _multiMonitorEnabled;

    /// <summary>Id of the display the keyboard controls by default (empty = first).</summary>
    [ObservableProperty]
    [property: JsonPropertyName("defaultMonitorId")]
    private string _defaultMonitorId = string.Empty;

    partial void OnBrightnessStepChanged(int value) => BrightnessStep = Math.Clamp(value, 1, 50);
    partial void OnOsdDurationMsChanged(int value) => OsdDurationMs = Math.Clamp(value, 500, 10000);

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressSave)
        {
            Save();
        }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<SettingsService>(json, JsonOptions);
                if (loaded is not null)
                {
                    BrightnessStep = loaded.BrightnessStep;
                    OsdDurationMs = loaded.OsdDurationMs;
                    RunAtStartup = loaded.RunAtStartup;
                    EnableRawInput = loaded.EnableRawInput;
                    BrightnessDownVk = loaded.BrightnessDownVk;
                    BrightnessUpVk = loaded.BrightnessUpVk;
                    Theme = loaded.Theme;
                    BackgroundStyle = loaded.BackgroundStyle;
                    CustomBackgroundColor = loaded.CustomBackgroundColor;
                    FillStyle = loaded.FillStyle;
                    CustomFillColor = loaded.CustomFillColor;
                    ShowPercentText = loaded.ShowPercentText;
                    MultiMonitorEnabled = loaded.MultiMonitorEnabled;
                    DefaultMonitorId = loaded.DefaultMonitorId;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings -> keep defaults.
        }
        finally
        {
            _suppressSave = false;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Best effort persistence.
        }
    }

    public static int ParseVkCode(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        text = text.Trim();
        try
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return Convert.ToInt32(text[2..], 16) is var v && v is >= 0 and <= 255 ? v : fallback;
            }

            return int.TryParse(text, out var d) && d is >= 0 and <= 255 ? d : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
