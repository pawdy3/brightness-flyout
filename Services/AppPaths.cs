using System;
using System.IO;

namespace BrightFlyout.Services;

/// <summary>Canonical file system locations used by the app.</summary>
internal static class AppPaths
{
    // The BRIGHTFLYOUT_APPDATA override isolates integration-test runs from the
    // real settings and log.
    public static readonly string AppDataDirectory =
        Environment.GetEnvironmentVariable("BRIGHTFLYOUT_APPDATA")
            is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(
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
