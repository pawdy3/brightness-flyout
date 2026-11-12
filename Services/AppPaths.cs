namespace BrightFlyout.Services;

/// <summary>Canonical file system locations used by the app.</summary>
internal static class AppPaths
{
    public static readonly string AppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrightFlyout");

    public static readonly string SettingsFile = Path.Combine(AppDataDirectory, "settings.json");
    public static readonly string LogFile = Path.Combine(AppDataDirectory, "debug.log");

    /// <summary>Creates the app data directory if needed. Never throws.</summary>
    public static void EnsureAppDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
        }
        catch
        {
            // best effort
        }
    }
}
