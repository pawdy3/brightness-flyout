using BrightFlyout.Services;
using BrightFlyout.Views;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BrightFlyout;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private SystemThemeService? _systemTheme;

    internal SettingsService Settings { get; private set; } = null!;
    internal BrightnessController Controller { get; private set; } = null!;
    internal OsdWindow Osd { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

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
        _trayIcon.ForceCreate();

        // Events can't be wired in Application XAML; build the menu in code.
        var menu = new MenuFlyout();
        var settingsItem = new MenuFlyoutItem { Text = "Settings", Icon = new FontIcon { Glyph = "\uE713" } };
        settingsItem.Click += OnSettingsMenuClick;
        menu.Items.Add(settingsItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += OnExitMenuClick;
        menu.Items.Add(exitItem);
        _trayIcon.ContextFlyout = menu;

        // The OSD window doubles as the (hidden) raw-input message target.
        Osd = new OsdWindow(Settings);

        Controller = new BrightnessController(Settings, Osd);
        Controller.BrightnessChanged += OnBrightnessChanged;
        Controller.Start();

        // WinUI 3 doesn't track OS theme switches for ElementTheme.Default at
        // runtime - push explicit themes to all windows ourselves.
        _systemTheme = new SystemThemeService();
        _systemTheme.Changed += () => Osd.DispatcherQueue.TryEnqueue(ApplyThemeToWindows);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsService.Theme))
            {
                ApplyThemeToWindows();
            }
        };
        ApplyThemeToWindows();

        if (Environment.GetCommandLineArgs().Contains("--open-settings"))
        {
            OpenSettings();
        }
    }

    internal ElementTheme EffectiveTheme() => Settings.Theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => _systemTheme?.IsDark == true ? ElementTheme.Dark : ElementTheme.Light
    };

    internal void ApplyThemeToWindows()
    {
        var theme = EffectiveTheme();
        Osd.SetTheme(theme);
        _settingsWindow?.SetTheme(theme);
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
