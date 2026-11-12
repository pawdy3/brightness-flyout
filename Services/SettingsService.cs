using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BrightFlyout.Services;

[JsonConverter(typeof(JsonStringEnumConverter<OsdBackgroundStyle>))]
public enum OsdBackgroundStyle
{
    Acrylic,
    SolidColor
}

[JsonConverter(typeof(JsonStringEnumConverter<OsdFillStyle>))]
public enum OsdFillStyle
{
    SystemAccent,
    CustomColor
}

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    System,
    Light,
    Dark
}

[JsonConverter(typeof(JsonStringEnumConverter<OsdPlacement>))]
public enum OsdPlacement
{
    Primary,
    AllDisplays
}

/// <summary>
/// JSON-backed, observable application settings persisted to
/// %LOCALAPPDATA%\BrightFlyout\settings.json. Uses partial properties and a
/// source-generated JSON context so the app stays NativeAOT-compatible.
/// </summary>
public partial class SettingsService : ObservableObject
{
    private bool _suppressSave = true;

    public SettingsService()
    {
        PropertyChanged += OnPropertyChanged;
    }

    // --- Behavior ---

    [ObservableProperty]
    [JsonPropertyName("brightnessStep")]
    public partial int BrightnessStep { get; set; } = 5;

    [ObservableProperty]
    [JsonPropertyName("osdDurationMs")]
    public partial int OsdDurationMs { get; set; } = 2000;

    [ObservableProperty]
    [JsonPropertyName("runAtStartup")]
    public partial bool RunAtStartup { get; set; }

    // --- Keyboard hook ---

    [ObservableProperty]
    [JsonPropertyName("enableRawInput")]
    public partial bool EnableRawInput { get; set; } = true;

    /// <summary>VK code (hex or decimal) watched by the WH_KEYBOARD_LL hook for brightness down.</summary>
    [ObservableProperty]
    [JsonPropertyName("brightnessDownVk")]
    public partial string BrightnessDownVk { get; set; } = "0x9E";

    /// <summary>VK code (hex or decimal) watched by the WH_KEYBOARD_LL hook for brightness up.</summary>
    [ObservableProperty]
    [JsonPropertyName("brightnessUpVk")]
    public partial string BrightnessUpVk { get; set; } = "0x9F";

    // --- Appearance ---

    [ObservableProperty]
    [JsonPropertyName("theme")]
    public partial AppTheme Theme { get; set; } = AppTheme.System;

    [ObservableProperty]
    [JsonPropertyName("backgroundStyle")]
    public partial OsdBackgroundStyle BackgroundStyle { get; set; } = OsdBackgroundStyle.Acrylic;

    [ObservableProperty]
    [JsonPropertyName("customBackgroundColor")]
    public partial string CustomBackgroundColor { get; set; } = "#E61F1F1F";

    [ObservableProperty]
    [JsonPropertyName("fillStyle")]
    public partial OsdFillStyle FillStyle { get; set; } = OsdFillStyle.SystemAccent;

    [ObservableProperty]
    [JsonPropertyName("customFillColor")]
    public partial string CustomFillColor { get; set; } = "#FFF5C542";

    [ObservableProperty]
    [JsonPropertyName("showPercentText")]
    public partial bool ShowPercentText { get; set; } = true;

    /// <summary>When on, clicking/dragging the collapsed flyout bar sets brightness.</summary>
    [ObservableProperty]
    [JsonPropertyName("clickableFlyoutBar")]
    public partial bool ClickableFlyoutBar { get; set; } = true;

    // --- Multiple displays ---

    [ObservableProperty]
    [JsonPropertyName("multiMonitorEnabled")]
    public partial bool MultiMonitorEnabled { get; set; }

    /// <summary>Id of the display the keyboard controls by default (empty = first).</summary>
    [ObservableProperty]
    [JsonPropertyName("defaultMonitorId")]
    public partial string DefaultMonitorId { get; set; } = string.Empty;

    /// <summary>Ids of displays excluded from control.</summary>
    [ObservableProperty]
    [JsonPropertyName("excludedMonitorIds")]
    public partial List<string> ExcludedMonitorIds { get; set; } = [];

    /// <summary>When on, any brightness change is applied to all included displays.</summary>
    [ObservableProperty]
    [JsonPropertyName("syncMonitors")]
    public partial bool SyncMonitors { get; set; }

    [ObservableProperty]
    [JsonPropertyName("osdPlacement")]
    public partial OsdPlacement OsdPlacement { get; set; } = OsdPlacement.Primary;

    /// <summary>Last version an update notification was shown for (never nag twice).</summary>
    [ObservableProperty]
    [JsonPropertyName("lastNotifiedVersion")]
    public partial string LastNotifiedVersion { get; set; } = string.Empty;

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
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsService);
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
                    ClickableFlyoutBar = loaded.ClickableFlyoutBar;
                    MultiMonitorEnabled = loaded.MultiMonitorEnabled;
                    DefaultMonitorId = loaded.DefaultMonitorId;
                    ExcludedMonitorIds = loaded.ExcludedMonitorIds ?? [];
                    SyncMonitors = loaded.SyncMonitors;
                    OsdPlacement = loaded.OsdPlacement;
                    LastNotifiedVersion = loaded.LastNotifiedVersion;
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable settings -> keep defaults.
            Log.Write("Settings load failed, using defaults", ex);
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
            AppPaths.EnsureAppDataDirectory();
            File.WriteAllText(AppPaths.SettingsFile,
                JsonSerializer.Serialize(this, SettingsJsonContext.Default.SettingsService));
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

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsService))]
internal partial class SettingsJsonContext : JsonSerializerContext;
