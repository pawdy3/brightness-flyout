using BrightFlyout.Services;
using BrightFlyout.Views;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BrightFlyout;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private ThemeService? _themeService;

    internal SettingsService Settings { get; private set; } = null!;
    internal BrightnessController Controller { get; private set; } = null!;
    internal OsdManager Osd { get; private set; } = null!;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

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
            // Another instance is already running; exit quietly.
            Environment.Exit(0);
            return;
        }

        Settings = new SettingsService();
        Settings.Load();

        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];
        // Fixed ID so Windows remembers the icon's tray placement across restarts.
        _trayIcon.Id = new Guid("3F6B2A11-9C4E-4A5D-8E2F-1C7D9B0A6E55");
        SetTrayIcon();
        _trayIcon.DoubleClickCommand = new RelayCommand(OpenSettings);
        TryCreateTrayIcon();

        // Events can't be wired in Application XAML; build the menu in code.
        // MinWidth: SecondWindow mode measures flyout items before their templates
        // are fully loaded right after app start, which crops the item text.
        var menu = new MenuFlyout();
        var settingsItem = new MenuFlyoutItem
        {
            Text = "Settings",
            Icon = new FontIcon { Glyph = "\uE713" },
            MinWidth = 160
        };
        settingsItem.Click += OnSettingsMenuClick;
        menu.Items.Add(settingsItem);
        menu.Items.Add(new MenuFlyoutSeparator { MinWidth = 160 });
        var exitItem = new MenuFlyoutItem { Text = "Exit", MinWidth = 160 };
        exitItem.Click += OnExitMenuClick;
        menu.Items.Add(exitItem);
        _trayIcon.ContextFlyout = menu;

        // The first OSD window doubles as the (hidden) raw-input message target.
        Osd = new OsdManager(Settings);
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        Controller = new BrightnessController(Settings, Osd);
        Controller.BrightnessChanged += OnBrightnessChanged;
        Osd.BindDisplays(Controller.Displays);
        Controller.Start();

        // WinUI 3 doesn't track OS theme switches for ElementTheme.Default at
        // runtime - push explicit themes to all windows ourselves.
        _themeService = new ThemeService(Settings);
        _themeService.EffectiveThemeChanged += () =>
            _uiDispatcher?.TryEnqueue(ApplyThemeToWindows);
        ApplyThemeToWindows();

        if (Environment.GetCommandLineArgs().Contains("--open-settings"))
        {
            OpenSettings();
        }
    }

    internal ElementTheme EffectiveTheme() =>
        _themeService?.EffectiveTheme ?? ElementTheme.Default;

    internal void ApplyThemeToWindows()
    {
        var theme = EffectiveTheme();
        Osd.SetTheme(theme);
        _settingsWindow?.SetTheme(theme);

        if (_trayIcon is not null)
        {
            // The tray menu (SecondWindow mode) follows the TaskbarIcon's ActualTheme;
            // declared in Application.Resources it would otherwise default to light.
            _trayIcon.RequestedTheme = theme;
        }
    }

    private void SetTrayIcon()
    {
        try
        {
            // Load the embedded .ico (same sun glyph as the exe icon). This is
            // synchronous and font-independent: the previous GeneratedIconSource
            // approach converted to an icon asynchronously, so on slower machines
            // ForceCreate() raced it and Shell_NotifyIcon(NIM_ADD) failed with a
            // null icon - killing the app at startup.
            using var stream = typeof(App).Assembly.GetManifestResourceStream("BrightFlyout.app.ico")
                ?? throw new InvalidOperationException("Embedded app.ico not found");
            _trayIcon!.Icon = new System.Drawing.Icon(stream);
        }
        catch (Exception ex)
        {
            Log("Tray icon setup failed", ex);
        }
    }

    private int _trayCreateAttempts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _trayRetryTimer;

    /// <summary>
    /// Tray creation must never kill the app. It fails when explorer isn't running
    /// yet (we start at logon) or the shell rejects the icon; retry a few times -
    /// H.NotifyIcon also re-creates the icon automatically on TaskbarCreated.
    /// </summary>
    private void TryCreateTrayIcon()
    {
        try
        {
            _trayIcon!.ForceCreate();
            _trayRetryTimer?.Stop();
        }
        catch (Exception ex)
        {
            var win32 = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log($"Tray icon creation failed (attempt {_trayCreateAttempts + 1}, win32={win32})", ex);

            if (++_trayCreateAttempts < 10)
            {
                _trayRetryTimer ??= InitializeTrayRetryTimer();
                _trayRetryTimer.Start();
            }
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer InitializeTrayRetryTimer()
    {
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => TryCreateTrayIcon();
        return timer;
    }

    private void OnBrightnessChanged(int value)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = $"BrightFlyout - Brightness {value}%";
        }
    }

    private void OnSettingsMenuClick(object sender, RoutedEventArgs e)
    {
        try
        {
            OpenSettings();
        }
        catch (Exception ex)
        {
            Log("Failed to open settings", ex);
        }
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

    internal static void Log(string message, Exception? ex = null)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrightFlyout");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "debug.log"),
                $"[{DateTime.Now:HH:mm:ss}] {message}{(ex is null ? "" : $": {ex}")}{Environment.NewLine}");
        }
        catch
        {
            // logging must never throw
        }
    }

    internal void OnSettingsWindowClosed()
    {
        _settingsWindow = null;
    }

    private void OnExitMenuClick(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    private void Shutdown()
    {
        // Restore the native OSD before we go away.
        Controller.Dispose();
        _trayIcon?.Dispose();
        _settingsWindow?.Close();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Environment.Exit(0);
    }
}
