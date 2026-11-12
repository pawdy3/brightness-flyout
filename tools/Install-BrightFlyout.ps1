<#
.SYNOPSIS
  Installs or upgrades BrightFlyout from the MSIX next to this script.

.DESCRIPTION
  Handles the things raw Add-AppxPackage does badly:
  - first-run certificate trust (elevates just for the cert import),
  - a running app instance (closed gracefully before install),
  - downgrades and same-version rebuilds (remove + install),
  - reports deployment errors in readable form.

.EXAMPLE
  .\Install-BrightFlyout.ps1
#>
[CmdletBinding()]
param(
    [string]$PackagePath,
    [switch]$CertOnly
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

# --- certificate trust (one-time per machine, needs admin) ---
$cerPath = Join-Path $PSScriptRoot 'BrightFlyout.cer'
if (Test-Path $cerPath) {
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cerPath)
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object Thumbprint -eq $cert.Thumbprint
    if (-not $trusted) {
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $isAdmin) {
            Write-Step 'Trusting the signing certificate (one-time, asks for admin)...'
            $self = $MyInvocation.MyCommand.Path
            $p = Start-Process powershell -Verb RunAs -PassThru -Wait -ArgumentList `
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $self, '-CertOnly'
            if ($p.ExitCode -ne 0) {
                Write-Host 'Certificate trust failed or was declined; installation will fail with 0x800B0109.' -ForegroundColor Yellow
            }
        } else {
            Write-Step 'Trusting the signing certificate (one-time)...'
            Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        }
    }

    if ($CertOnly) { exit 0 }
} elseif ($CertOnly) {
    Write-Host 'BrightFlyout.cer not found next to the script.' -ForegroundColor Red
    exit 1
}

# --- locate the package ---
if (-not $PackagePath) {
    $PackagePath = Get-ChildItem $PSScriptRoot -Filter 'BrightFlyout*.msix' |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $PackagePath -or -not (Test-Path $PackagePath)) {
    Write-Host 'No BrightFlyout*.msix found next to the script.' -ForegroundColor Red
    exit 1
}

# --- close a running instance (avoids the 0x80073D02 "app in use" error) ---
$running = Get-Process BrightFlyout -ErrorAction SilentlyContinue
if ($running) {
    Write-Step 'Closing the running BrightFlyout instance...'
    $running | ForEach-Object { Stop-Process -Id $_.Id -Force }
    Start-Sleep -Seconds 1
}

# --- install ---
Write-Step "Installing $(Split-Path $PackagePath -Leaf)..."
try {
    Add-AppxPackage -Path $PackagePath -ForceUpdateFromAnyVersion -ForceApplicationShutdown -ErrorAction Stop
} catch {
    if ($_.Exception.Message -match '0x80073CFB') {
        # Same version, different content (a rebuild): remove first, then install.
        Write-Step 'Same version already installed - replacing it...'
        $existing = Get-AppxPackage -Name BrightFlyout
        if ($existing) { Remove-AppxPackage -Package $existing.PackageFullName }
        Add-AppxPackage -Path $PackagePath -ErrorAction Stop
    } else {
        Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

$pkg = Get-AppxPackage -Name BrightFlyout
Write-Host ''
Write-Host "BrightFlyout $($pkg.Version) installed." -ForegroundColor Green

if ($running) {
    # We closed a running instance for the install - start the new one again.
    Write-Step 'Restarting BrightFlyout...'
    $appId = (Get-StartApps | Where-Object Name -eq 'BrightFlyout' | Select-Object -First 1).AppID
    if (-not $appId) { $appId = "$($pkg.PackageFamilyName)!App" }
    Start-Process "shell:AppsFolder\$appId"
} else {
    Write-Host "Start it from the Start Menu, or: Start-Process `"shell:AppsFolder\$($pkg.PackageFamilyName)!App`""
}
