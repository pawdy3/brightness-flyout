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

### Development inner loop

Day-to-day development does **not** need the MSIX machinery (packaging, signing,
AOT) - that is for releases only. Use the dev script:

```powershell
.\tools\dev-run.ps1                    # kill running instance, Debug build, launch
.\tools\dev-run.ps1 -Release           # optimized JIT build instead (still seconds)
.\tools\dev-run.ps1 -OpenSettings      # launch straight into the settings window
.\tools\dev-run.ps1 -NoLaunch          # just build
```

Plain `dotnet` equivalent: `dotnet build -c Debug -p:Platform=x64 -r win-x64`,
exe lands in `bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\`.

Requires: .NET 10 SDK, and the **Windows App SDK 2.6 runtime** installed once
(unpackaged dev builds bootstrap against it). If the app fails to start with a
missing-runtime error, either install any released MSIX once (it pulls the
runtime from the Store) or run `windowsappruntimeinstall-x64.exe` from the
[Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).

Other dev affordances:

- Diagnostics go to `%LOCALAPPDATA%\BrightFlyout\debug.log` (bounded, 256 KB).
- `BrightFlyout.exe --open-settings` opens the settings window at startup
  (test hook; also works on installed MSIX builds via their exe path).
- The app lives in the system tray (sun icon). Right-click → **Settings**,
  double-click opens Settings directly.

### Release builds

The two MSIX variants and their sizes are listed under
[Distribution](#distribution). When publishing both variants locally, wipe
`bin`/`obj` between them - switching `PublishAot` on shared intermediates has
produced corrupted packages.

## Distribution

CI (`.github/workflows/build.yml`) produces two MSIX packages on every push,
and attaches them to a GitHub Release when a `v*` tag is pushed:

| Artifact | Size | Contains |
|---|---|---|
| `BrightFlyout_AOT_x64.msix` | ~3.8 MB | NativeAOT exe only — but the WMI path (internal laptop panels) is not AOT-clean and may misbehave |
| `BrightFlyout_Hybrid_x64.msix` | ~10 MB | regular build + trimmed .NET 10 runtime (reliable WMI; System.Management is kept untrimmed) |

Both pull the Windows App SDK 2.6 automatically as a framework package from the
Microsoft Store during install (one-time, needs network + Store access), and
both are proper installers: Start Menu entry, Settings > Apps uninstall, and
in-place upgrades (installing a newer `.msix` upgrades the app and keeps
settings). Pick AOT for the tiny package; pick hybrid if internal laptop panel
(WMI) control matters to you.

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

Local equivalents (both need MSVC C++ build tools for AOT; MSIX signing uses
`certs\BrightFlyout.pfx` automatically if present):

```powershell
# portable NativeAOT single exe, no installer, zero prerequisites
# (self-contained Windows App SDK - unpackaged apps can't pull it from the Store)
dotnet publish -c Release -p:Platform=x64 -r win-x64 -p:PublishAot=true -p:EnableMsixTooling=true `
  -p:WindowsAppSDKSelfContained=true

# AOT MSIX -> AppPackages\..._x64_Test\
dotnet publish -c Release -p:Platform=x64 -r win-x64 -p:BuildMSIX=true -p:PublishAot=true `
  -p:EnableMsixTooling=true -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never

# hybrid MSIX -> AppPackages\..._x64_Test\
# IMPORTANT: wipe bin/obj first if you published the AOT variant before (see "Release builds" above)
dotnet publish -c Release -p:Platform=x64 -r win-x64 -p:BuildMSIX=true -p:PublishAot=false `
  -p:SelfContained=true -p:PublishTrimmed=true `
  -p:EnableMsixTooling=true -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never
```

## Notes

- If your keyboard emits actual virtual-key codes for brightness instead of HID
  consumer usages, set them (hex) under *Settings → Keyboard input*.
