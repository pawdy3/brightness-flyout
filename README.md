# BrightFlyout

A WinUI 3 tray app that controls **external monitor brightness over DDC/CI** with the
keyboard brightness keys, and shows its own Windows-11-style OSD instead of the
built-in one.

## Features

- **Multi-transport display control**: external monitors via DDC/CI (VCP code `0x10`),
  internal laptop panels via WMI (`WmiMonitorBrightnessMethods`) — each display is
  probed at startup and paired with the transport that answers. Writes are coalesced,
  so rapid key presses never queue slow DDC transactions.
- **Multi-display mode** (off by default): the flyout gains an expand arrow listing
  one slider per display plus a *Synchronize all displays* toggle. The keyboard
  controls the display whose slider was touched last (or the default display chosen
  in Settings at startup). Individual displays can be excluded from control in
  Settings, and the flyout can appear on the primary display or on all displays.
- **Laptop support**: on mobile systems Windows consumes the brightness keys itself;
  a `WmiMonitorBrightnessEvent` watcher mirrors those changes into our OSD (and to
  all included displays when sync is on). Displays that answer neither DDC/CI nor
  WMI (phantom "Generic PnP" entries) are filtered out.
- **Windows 10**: icon fonts fall back to Segoe MDL2 Assets, and the native OSD is
  suppressed via the legacy `NativeHWNDHost` host window.
- **Keyboard input detection**, two complementary paths:
  - `WH_KEYBOARD_LL` low-level hook with configurable virtual-key codes
    (matching keys are swallowed before the shell sees them), and
  - **Raw Input** on HID consumer page `0x0C` for the standardized
    *Display Brightness Increment/Decrement* usages `0x006F` / `0x0070` —
    these never surface as virtual keys, so this is the path that makes real
    brightness keys work. Held keys auto-repeat.
- **Custom OSD** replicating the Windows 11 flyout: borderless, always-on-top,
  non-activatable, acrylic-backed pill anchored bottom-center above the taskbar,
  with a percentage readout (can be hidden in Settings).
- **Transient native OSD suppression**: while the custom OSD is shown, the built-in
  shell OSD (the `XamlExplorerHostIslandWindow` XAML island) is hidden using the
  FluentFlyout/HideVolumeOSD technique — but only for the duration of the OSD, and
  always restored on timeout and on app exit. Volume OSD etc. are unaffected
  outside that window.
- **System tray** with Settings / Exit context menu.
- **Settings window**: brightness step, OSD duration, run-at-startup, theme
  (System/Light/Dark), flyout background (acrylic or custom color), bar fill
  (system accent or custom color), raw-input toggle and hook VK codes.
  Persisted to `%LOCALAPPDATA%\BrightFlyout\settings.json`.

## Build & run

Requires: .NET 10 SDK, Windows App SDK 1.8 runtime (debug builds).

```powershell
dotnet build -c Debug -p:Platform=x64 -r win-x64
.\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\BrightFlyout.exe
```

The app lives in the system tray (sun icon). Right-click → **Settings**,
double-click opens Settings directly.

## Distribution

CI (`.github/workflows/build.yml`) produces three artifacts on every push, and
attaches them to a GitHub Release when a `v*` tag is pushed:

| Artifact | Size | Prerequisites |
|---|---|---|
| `BrightFlyoutSetup-*.exe` | ~56 MB | none (per-user install, optional autostart) |
| `BrightFlyout-win-x64.zip` | ~200 MB | none (self-contained single-file exe) |
| `BrightFlyout-win-x64-minimal.zip` | ~38 MB | .NET Desktop Runtime 10 + Windows App SDK 1.8 runtime |

```powershell
git tag v1.0.2
git push origin v1.0.2
```

Local equivalents:

```powershell
# self-contained single file
dotnet publish -c Release -p:Platform=x64 -r win-x64 --self-contained `
  -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableMsixTooling=true

# installer (requires Inno Setup 6, ISCC.exe on PATH)
ISCC.exe installer.iss /DAppVersion=1.0.2
```

## Notes

- Only the **primary** display is controlled (single-display scope by design).
- If your keyboard emits actual virtual-key codes for brightness instead of HID
  consumer usages, set them (hex) under *Settings → Keyboard input*.
