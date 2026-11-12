<#
.SYNOPSIS
  Development inner loop: fast build with no MSIX packaging, no signing, no AOT.

.DESCRIPTION
  Kills any running BrightFlyout instance, builds Debug (default) or Release
  (JIT, not AOT - still seconds, not minutes), and launches the exe.

  The exe is unpackaged, so it needs the Windows App SDK 2.6 runtime installed
  once per machine (see README -> Development inner loop).

.EXAMPLE
  .\tools\dev-run.ps1                    # Debug build + launch
  .\tools\dev-run.ps1 -Release           # optimized build + launch
  .\tools\dev-run.ps1 -OpenSettings      # launch straight into the settings window
  .\tools\dev-run.ps1 -NoLaunch          # just build
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$OpenSettings,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)  # repo root

$config = $Release.IsPresent ? 'Release' : 'Debug'

Get-Process BrightFlyout -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.Id -Force }

dotnet build -c $config -p:Platform=x64 -r win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($NoLaunch) { return }

$exe = Get-ChildItem "bin\x64\$config" -Recurse -Filter BrightFlyout.exe |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw 'BrightFlyout.exe not found after build' }

$arguments = @()
if ($OpenSettings) { $arguments += '--open-settings' }
Start-Process $exe.FullName -ArgumentList $arguments
"Started $($exe.FullName)"
"Log: $env:LOCALAPPDATA\BrightFlyout\debug.log"
