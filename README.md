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

CI (`.github/workflows/build.yml`) produces two artifacts on every push, and
attaches them to a GitHub Release when a `v*` tag is pushed:

| Artifact | Size | Prerequisites |
|---|---|---|
| `BrightFlyout-win-x64.zip` | ~9 MB | none (NativeAOT single exe, portable) |
| `BrightFlyout_*_x64.msix` | ~45 MB | none (NativeAOT exe + self-contained Windows App SDK; cert trust, see below) |

Both contain the same NativeAOT build; the MSIX additionally bundles the Windows
App SDK so it is a proper installer: Start Menu entry, Settings > Apps uninstall,
and in-place upgrades (installing a newer `.msix` upgrades the app and keeps
settings).

### Installing the MSIX (one-time cert trust)

The package is signed with a self-signed certificate, so Windows requires the
cert to be trusted once per machine (needs admin):

```powershell
# one-time, in an elevated terminal, using the BrightFlyout.cer shipped with the release:
Import-Certificate -FilePath BrightFlyout.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople

# then double-click the .msix, or:
Add-AppxPackage .\BrightFlyout_1.0.3.0_x64.msix
```

Without this step, installation fails with `0x800B0109` (untrusted root).

### Releasing

```powershell
git tag v1.0.3
git push origin v1.0.3
```

CI signs the MSIX with the stable cert from the **`MSIX_CERT_PFX_BASE64`**
repository secret (base64 of `certs\BrightFlyout.pfx`):

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("certs\BrightFlyout.pfx"))
# -> GitHub repo -> Settings -> Secrets and variables -> Actions -> New repository secret
```

Keep `certs\` private (gitignored) and back it up - a new cert means every
machine has to trust it again before upgrading. The cert is valid until 2051.

Local equivalents:

```powershell
# NativeAOT single exe (needs MSVC C++ build tools)
dotnet publish -c Release -p:Platform=x64 -r win-x64 -p:PublishAot=true -p:EnableMsixTooling=true

# MSIX (signed with certs\BrightFlyout.pfx if present)
dotnet publish -c Release -p:Platform=x64 -r win-x64 -p:BuildMSIX=true -p:PublishAot=true `
  -p:EnableMsixTooling=true -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never
# -> AppPackages\BrightFlyout_<version>_x64_Test\BrightFlyout_<version>_x64.msix
```

Note: System.Management (the WMI path for internal laptop panels) is not
AOT-clean. If internal-panel control misbehaves in these builds, build locally
without `-p:PublishAot=true`.

## Notes

- If your keyboard emits actual virtual-key codes for brightness instead of HID
  consumer usages, set them (hex) under *Settings → Keyboard input*.
