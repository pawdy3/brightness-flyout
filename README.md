# BrightFlyout

A WinUI 3 tray app that controls **external monitor brightness over DDC/CI** with the
keyboard brightness keys, and shows its own Windows-11-style OSD instead of the
built-in one.

## Features

- **DDC/CI brightness control** of the primary display (VCP code `0x10`, luminance)
  with coalesced writes, so rapid key presses never queue slow DDC transactions.
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

Requires: .NET 8 SDK, Windows App SDK 1.8 runtime.

```powershell
dotnet build -c Debug -p:Platform=x64 -r win-x64
.\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\BrightFlyout.exe
```

The app lives in the system tray (sun icon). Right-click → **Settings**.

## Notes

- Only the **primary** display is controlled (single-display scope by design).
- If your keyboard emits actual virtual-key codes for brightness instead of HID
  consumer usages, set them (hex) under *Settings → Keyboard input*.
