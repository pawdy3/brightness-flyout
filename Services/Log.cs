using System;
using System.IO;
using Serilog;

namespace BrightFlyout.Services;

/// <summary>
/// Diagnostic log at %LOCALAPPDATA%\BrightFlyout\debug.log, backed by Serilog's
/// file sink. Bounded: tail-trimmed to 128 KB at startup when over 256 KB
/// (Serilog's own size limits either stop writes or rename the file to
/// debug_001.log - both worse than a startup trim here). Never throws.
/// </summary>
internal static class Log
{
    private const int MaxBytes = 256 * 1024;
    private const int TrimToBytes = 128 * 1024;

    private static readonly Serilog.Core.Logger Logger = CreateLogger();

    private static Serilog.Core.Logger CreateLogger()
    {
        TextWriter? writer = null;
        try
        {
            AppPaths.EnsureAppDataDirectory();
            TrimIfNeeded();

            // Open the file ourselves: Serilog's file sink shares read/write but not
            // delete, which locks the file against moving/renaming while we run.
            var stream = new FileStream(AppPaths.LogFile, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            writer = TextWriter.Synchronized(new StreamWriter(stream) { AutoFlush = true });
        }
        catch
        {
            // logging must never throw
        }

        var config = new LoggerConfiguration();
        if (writer is not null)
        {
            config = config.WriteTo.TextWriter(writer,
                outputTemplate: "[{Timestamp:HH:mm:ss}] {Message:lj}{NewLine}{Exception}");
        }

        return config.CreateLogger();
    }

    /// <summary>Tail-trims the log file when it exceeds <see cref="MaxBytes"/>.</summary>
    private static void TrimIfNeeded()
    {
        if (!File.Exists(AppPaths.LogFile) || new FileInfo(AppPaths.LogFile).Length <= MaxBytes)
        {
            return;
        }

        var bytes = File.ReadAllBytes(AppPaths.LogFile);
        File.WriteAllBytes(AppPaths.LogFile, bytes[^TrimToBytes..]);
    }

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            // Messages are pre-formatted by callers; escape braces so they are
            // never interpreted as Serilog message-template tokens.
            var template = message.Replace("{", "{{").Replace("}", "}}");
            if (ex is null)
            {
                Logger.Information(template);
            }
            else
            {
                Logger.Information(ex, template);
            }
        }
        catch
        {
            // logging must never throw
        }
    }

    /// <summary>Flushes pending writes (call before process exit).</summary>
    public static void Flush()
    {
        try
        {
            Logger.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
