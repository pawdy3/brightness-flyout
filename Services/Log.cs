namespace BrightFlyout.Services;

/// <summary>
/// Append-only diagnostic log at %LOCALAPPDATA%\BrightFlyout\debug.log.
/// Bounded: when the file exceeds 256 KB it is tail-trimmed to 128 KB.
/// Never throws.
/// </summary>
internal static class Log
{
    private const int MaxBytes = 256 * 1024;
    private const int TrimToBytes = 128 * 1024;

    private static readonly object Gate = new();
    private static readonly string FilePath = AppPaths.LogFile;

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                AppPaths.EnsureAppDataDirectory();
                TrimIfNeeded();
                AppendRaw(ex is null ? message : $"{message}: {ex}");
            }
        }
        catch
        {
            // logging must never throw
        }
    }

    /// <summary>Tail-trims the log file when it exceeds <see cref="MaxBytes"/>.</summary>
    private static void TrimIfNeeded()
    {
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length <= MaxBytes)
        {
            return;
        }

        var bytes = File.ReadAllBytes(FilePath);
        File.WriteAllBytes(FilePath, bytes[^TrimToBytes..]);
        AppendRaw("[... older log trimmed ...]");
    }

    private static void AppendRaw(string line) =>
        File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
}
