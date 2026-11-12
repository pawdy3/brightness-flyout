using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrightFlyout.Services;
using BrightFlyout.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace BrightFlyout;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private TrayIconService? _tray;
    private SettingsWindow? _settingsWindow;
    private ThemeService? _themeService;
    private UpdateChecker? _updateChecker;
    private DispatcherQueue? _uiDispatcher;

    internal SettingsService Settings { get; private set; } = null!;
    internal BrightnessController Controller { get; private set; } = null!;
    internal OsdManager Osd { get; private set; } = null!;

    public App()
    {
        // Log everything before anything can fail silently (esp. on Win10).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("Fatal (AppDomain)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Log("Unobserved task exception", e.Exception);

        InitializeComponent();

        UnhandledException += (_, e) => Log("Unhandled (XAML)", e.Exception);
    }

    /// <summary>Windows 11 is 10.0.22000+; the manifest's supportedOS entry makes this reliable.</summary>
    internal static bool IsWindows11 => Environment.OSVersion.Version >= new Version(10, 0, 22000);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "BrightFlyout.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Log("Another instance is already running; exiting (single-instance mutex)");
            Services.Log.Flush();
            Environment.Exit(0);
            return;
        }

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Log($"BrightFlyout {version} starting on {Environment.OSVersion.Version}");

        Settings = new SettingsService();
        Settings.Load();

        try
        {
            _tray = new TrayIconService("BrightFlyout", LoadTrayIcon());
            _tray.SettingsRequested += () =>
            {
                try
                {
                    OpenSettings();
                }
                catch (Exception ex)
                {
                    Log("Failed to open settings", ex);
                }
            };
            _tray.ExitRequested += Shutdown;
        }
        catch (Exception ex)
        {
            // Without the tray icon the app still fully works via the keyboard.
            Log("Tray icon failed to initialize", ex);
        }

        Osd = new OsdManager(Settings);
        _uiDispatcher = DispatcherQueue.GetForCurrentThread();

        Controller = new BrightnessController(Settings, Osd);
        Controller.BrightnessChanged += OnBrightnessChanged;
        Osd.BindDisplays(Controller.Displays);
        Controller.Start(_tray);
        if (_tray is not null)
        {
            _tray.RefreshRequested += () => _ = Controller.RefreshDisplaysAsync();
        }

        // Diagnostics: prove the WMI path works (usable on machines without
        // an internal panel too).
        if (Environment.GetEnvironmentVariable("BRIGHTFLYOUT_WMI_SELFTEST") is { Length: > 0 })
        {
            Task.Run(Services.Displays.WmiDisplayController.SelfTest);
        }

        // WinUI 3 doesn't track OS theme switches for ElementTheme.Default at
        // runtime - push explicit themes to all windows ourselves.
        _themeService = new ThemeService(Settings);
        _themeService.EffectiveThemeChanged += () =>
            _uiDispatcher?.TryEnqueue(ApplyThemeToWindows);
        ApplyThemeToWindows();

        _updateChecker = new UpdateChecker(Settings)
        {
            Notify = (title, text) => _tray?.ShowNotification(title, text)
        };
        _updateChecker.Start();

        if (Environment.GetCommandLineArgs().Contains("--open-settings"))
        {
            OpenSettings();
        }
    }

    private static unsafe HICON LoadTrayIcon()
    {
        // The ico is an embedded resource (single-file safe); LoadImage needs a
        // file, so extract it to %TEMP% once.
        var icoPath = Path.Combine(Path.GetTempPath(), "BrightFlyout-app.ico");
        if (!File.Exists(icoPath))
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("BrightFlyout.app.ico")
                ?? throw new InvalidOperationException("Embedded app.ico not found");
            using var file = File.Create(icoPath);
            stream.CopyTo(file);
        }

        HICON hIcon;
        fixed (char* pathPtr = icoPath)
        {
            var handle = PInvoke.LoadImage(default, new PCWSTR(pathPtr), GDI_IMAGE_TYPE.IMAGE_ICON,
                PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON),
                PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSMICON),
                IMAGE_FLAGS.LR_LOADFROMFILE);
            hIcon = new HICON(handle.Value);
        }

        return !hIcon.IsNull
            ? hIcon
            : throw new InvalidOperationException(
                $"LoadImage failed, win32={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    internal ElementTheme EffectiveTheme() =>
        _themeService?.EffectiveTheme ?? ElementTheme.Default;

    internal void ApplyThemeToWindows()
    {
        var theme = EffectiveTheme();
        Osd.SetTheme(theme);
        _settingsWindow?.SetTheme(theme);
    }

    private void OnBrightnessChanged(int value)
    {
        _tray?.SetToolTip($"BrightFlyout - Brightness {value}%");
    }

    internal void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Activate();
    }

    internal static void Log(string message, Exception? ex = null) => Services.Log.Write(message, ex);

    internal void OnSettingsWindowClosed()
    {
        _settingsWindow = null;
    }

    private void Shutdown()
    {
        // Restore the native OSD before we go away.
        _updateChecker?.Dispose();
        Controller.Dispose();
        _tray?.Dispose();
        _settingsWindow?.Close();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Services.Log.Flush();
        Environment.Exit(0);
    }
}
