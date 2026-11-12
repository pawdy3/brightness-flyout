<#
.SYNOPSIS
  Identifies the window behind the native (or vendor) on-screen display.

.DESCRIPTION
  BrightFlyout suppresses the Windows 11 XAML-island OSD and the legacy Windows 10
  NativeHWNDHost OSD. If another OSD still appears (e.g. a laptop vendor's hotkey
  utility), run this script WHILE the rogue OSD is visible - press and hold a
  brightness key to keep it on screen, then run:

      powershell -NoProfile -ExecutionPolicy Bypass -File .\Find-NativeOsd.ps1

  Send the output back for analysis. Look for a small window near the bottom
  center of the screen; the "Class" column is what we need.
#>
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public class OsdFinder {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$rows = New-Object System.Collections.Generic.List[object]
$cb = [OsdFinder+EnumProc]{
    param($h, $l)
    if ([OsdFinder]::IsWindowVisible($h)) {
        $cls = New-Object System.Text.StringBuilder 256
        $ttl = New-Object System.Text.StringBuilder 256
        [OsdFinder]::GetClassNameW($h, $cls, 256) | Out-Null
        [OsdFinder]::GetWindowTextW($h, $ttl, 256) | Out-Null
        $r = New-Object OsdFinder+RECT
        [OsdFinder]::GetWindowRect($h, [ref]$r) | Out-Null
        $procId = 0
        [OsdFinder]::GetWindowThreadProcessId($h, [ref]$procId) | Out-Null
        $procName = (Get-Process -Id $procId -ErrorAction SilentlyContinue).ProcessName
        $rows.Add([pscustomobject]@{
            Hwnd    = ('0x{0:X}' -f $h.ToInt64())
            Class   = $cls.ToString()
            Title   = $ttl.ToString()
            Process = $procName
            Rect    = "$($r.Left),$($r.Top)-$($r.Right),$($r.Bottom)"
        })
    }
    $true
}
[OsdFinder]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

$rows |
    Where-Object { $_.Rect -notmatch '^(\d+),(\d+)-\1,\2$' } |
    Sort-Object Process, Class |
    ForEach-Object { "{0}  [{1}]  {2}  ""{3}""  {4}" -f $_.Hwnd, $_.Process, $_.Class, $_.Title, $_.Rect } |
    Out-String -Width 300 | Write-Host
