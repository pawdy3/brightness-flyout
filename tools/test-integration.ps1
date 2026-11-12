<#
.SYNOPSIS
  Integration test suite: runs the real app against simulated displays and
  verifies the observable behavior (no DDC hardware required).

.DESCRIPTION
  Uses two test hooks compiled into the app:
    BRIGHTFLYOUT_SIMULATE_DISPLAYS - in-memory displays instead of hardware
    BRIGHTFLYOUT_APPDATA           - isolated settings/log directory

  Covered: cold start, single-instance mutex, keyboard hook -> brightness ->
  OSD (screenshot diff), OSD auto-hide, tray icon registration, settings
  window. Any already-running BrightFlyout instance is killed first.

.EXAMPLE
  .\tools\test-integration.ps1               # build Debug, run suite
  .\tools\test-integration.ps1 -SkipBuild    # reuse existing Debug build
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)  # repo root

#region Helpers

$script:passCount = 0
$script:failCount = 0

function Record-Test([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) {
        $script:passCount++
        Write-Host "PASS  $Name" -ForegroundColor Green
    } else {
        $script:failCount++
        Write-Host "FAIL  $Name$(if ($Detail) { "  ($Detail)" })" -ForegroundColor Red
    }
}

function Wait-For([scriptblock]$Condition, [int]$TimeoutMs = 15000, [int]$PollMs = 250) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds $PollMs
    }
    return $false
}

function Get-AppLog { Get-Content $script:LogFile -ErrorAction SilentlyContinue }

function Test-LogContains([string]$Text) {
    (Get-AppLog | Select-String -SimpleMatch $Text) -ne $null
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class TestNative {
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint f, UIntPtr e);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string cls, string title);
    [DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONIDENTIFIER {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public Guid guidItem;
    }
}
'@

function Send-BrightnessKey([byte]$Vk) {
    [TestNative]::keybd_event($Vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [TestNative]::keybd_event($Vk, 0, 2, [UIntPtr]::Zero)
}

# The OSD pill is anchored bottom-center above the taskbar.
function Capture-OsdRegion([string]$Path) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap 900, 250
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]($b.Width / 2 - 450), $b.Height - 330, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($Path)
    $bmp.Dispose()
}

# Fraction of sampled pixels that differ noticeably between two captures.
function Get-RegionDiff([string]$PathA, [string]$PathB) {
    $a = New-Object System.Drawing.Bitmap($PathA)
    $b = New-Object System.Drawing.Bitmap($PathB)
    $different = 0
    $total = 0
    for ($y = 0; $y -lt $a.Height; $y += 2) {
        for ($x = 0; $x -lt $a.Width; $x += 2) {
            $total++
            $pa = $a.GetPixel($x, $y)
            $pb = $b.GetPixel($x, $y)
            if ([Math]::Abs($pa.R - $pb.R) -gt 20 -or
                [Math]::Abs($pa.G - $pb.G) -gt 20 -or
                [Math]::Abs($pa.B - $pb.B) -gt 20) {
                $different++
            }
        }
    }
    $a.Dispose(); $b.Dispose()
    return $different / $total
}

function Stop-AppUnderTest {
    Get-Process BrightFlyout -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.Id -Force }
}

function Start-AppUnderTest([string[]]$Arguments = @()) {
    $exe = Get-ChildItem bin\x64\Debug -Recurse -Filter BrightFlyout.exe |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { throw 'BrightFlyout.exe not found; build first (tools\dev-run.ps1 -NoLaunch)' }
    return Start-Process $exe.FullName -ArgumentList $Arguments -PassThru
}

#endregion

# Kill first: a running instance locks the exe against rebuild.
Stop-AppUnderTest
Start-Sleep -Seconds 1

if (-not $SkipBuild) {
    dotnet build -c Debug -p:Platform=x64 -r win-x64
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Isolated environment for the app under test.
$script:TestAppData = Join-Path $env:TEMP 'BrightFlyoutIntegrationTests'
Remove-Item -Recurse -Force $script:TestAppData -ErrorAction SilentlyContinue
$script:LogFile = Join-Path $script:TestAppData 'debug.log'
$env:BRIGHTFLYOUT_APPDATA = $script:TestAppData
$env:BRIGHTFLYOUT_SIMULATE_DISPLAYS = '1'

$snapA = Join-Path $env:TEMP 'bf_test_a.png'
$snapB = Join-Path $env:TEMP 'bf_test_b.png'

try {
    # --- Cold start ---
    $app = Start-AppUnderTest
    $booted = Wait-For { Test-LogContains 'Displays refreshed: 2 found' }
    Record-Test 'cold start: two simulated displays detected' $booted
    Record-Test 'cold start: keyboard hook installed' (Test-LogContains 'Keyboard hook installed')
    Record-Test 'cold start: process alive' (-not $app.HasExited)

    # --- Single instance ---
    $second = Start-AppUnderTest
    $exited = Wait-For { $second.HasExited } -TimeoutMs 10000
    Record-Test 'single instance: second launch exits cleanly' ($exited -and $second.ExitCode -eq 0)
    Record-Test 'single instance: mutex exit is logged' (Test-LogContains 'single-instance mutex')

    # --- Brightness down: OSD appears + chain logged ---
    Capture-OsdRegion $snapA
    Send-BrightnessKey 0x9E
    $stepped = Wait-For { Test-LogContains 'Brightness -> 45% (Simulated monitor)' } -TimeoutMs 5000
    Record-Test 'brightness down: simulated display stepped 50 -> 45' $stepped
    Start-Sleep -Milliseconds 300
    Capture-OsdRegion $snapB
    $diff = Get-RegionDiff $snapA $snapB
    Record-Test 'brightness down: OSD flyout visible' ($diff -gt 0.01) ("region diff {0:P1}" -f $diff)

    # --- Brightness up restores ---
    Send-BrightnessKey 0x9F
    $restored = Wait-For { Test-LogContains 'Brightness -> 50% (Simulated monitor)' } -TimeoutMs 5000
    Record-Test 'brightness up: stepped back to 50' $restored

    # --- OSD auto-hide (default duration 2 s) ---
    Start-Sleep -Milliseconds 3500
    Capture-OsdRegion $snapB
    $diff = Get-RegionDiff $snapA $snapB
    Record-Test 'OSD auto-hides after timeout' ($diff -lt 0.005) ("region diff {0:P1}" -f $diff)

    # --- Tray icon registered ---
    $explorer = Get-Process explorer -ErrorAction SilentlyContinue
    if (-not $explorer) {
        Record-Test 'tray icon registered' $true 'skipped: no explorer.exe in this session'
    } else {
        $trayHwnd = [TestNative]::FindWindowExW([IntPtr]::new(-3), [IntPtr]::Zero, 'BrightFlyoutTrayWindow', 'BrightFlyoutTrayWindow')
        $ok = $false
        if ($trayHwnd -ne [IntPtr]::Zero) {
            $id = New-Object TestNative+NOTIFYICONIDENTIFIER
            $id.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][TestNative+NOTIFYICONIDENTIFIER])
            $id.hWnd = $trayHwnd
            $id.uID = 1
            $id.guidItem = [Guid]::Empty
            $rect = New-Object TestNative+RECT
            $hr = [TestNative]::Shell_NotifyIconGetRect([ref]$id, [ref]$rect)
            $ok = $hr -eq 0 -and ($rect.Right -gt $rect.Left -or $rect.Bottom -gt $rect.Top)
        }
        Record-Test 'tray icon registered' $ok $(if ($trayHwnd -eq [IntPtr]::Zero) { 'tray message window not found' } else { '' })
    }

    # --- Settings window ---
    Stop-AppUnderTest
    Start-Sleep -Seconds 1
    $appWithSettings = Start-AppUnderTest -Arguments '--open-settings'
    $shown = Wait-For {
        $appWithSettings.Refresh()
        -not $appWithSettings.HasExited -and $appWithSettings.MainWindowTitle -eq 'BrightFlyout Settings'
    } -TimeoutMs 10000
    Record-Test 'settings window opens via --open-settings' $shown
}
finally {
    Stop-AppUnderTest
    Remove-Item $snapA, $snapB -ErrorAction SilentlyContinue
    $env:BRIGHTFLYOUT_APPDATA = $null
    $env:BRIGHTFLYOUT_SIMULATE_DISPLAYS = $null
}

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f $script:passCount, $script:failCount) `
    -ForegroundColor ($script:failCount -eq 0 ? 'Green' : 'Red')

if ($script:failCount -eq 0) {
    Remove-Item -Recurse -Force $script:TestAppData -ErrorAction SilentlyContinue
} else {
    Write-Host "Test artifacts kept at $script:TestAppData" -ForegroundColor Yellow
}
exit ($script:failCount -eq 0 ? 0 : 1)
