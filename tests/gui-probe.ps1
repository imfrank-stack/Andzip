# Click the real screen and screenshot the real window -- the on-the-desktop half of a
# release check. Unit tests (including the Avalonia headless ones) run in a synthetic
# environment; this one drives the shipped artifact the way a user does.
#
# NOTE: keep this file ASCII-only -- Windows PowerShell 5.1 reads non-BOM UTF-8 as ANSI.
#
#   powershell -ExecutionPolicy Bypass -File tests\gui-probe.ps1 `
#     -Exe dist\Andzip-Setup-v1.8.4-win-x64.exe `
#     -OutDir $env:TEMP\andzip-gui-probe `
#     -Steps @('shot:01-open', 'click:470,300', 'wait:2500', 'shot:02-done')
#
# Coordinates in 'click:' are OFFSETS from the window's top-left corner (the script reads
# the rect after SetProcessDPIAware, so they are device pixels, not DIPs). Take a shot
# first, measure on the picture, then feed the clicks back in.
#
# From bash/ssh a real string[] is awkward to quote, so one '-Steps' argument may also
# carry the whole list separated by ';'.
#
# The mouse click is a real SetCursorPos + mouse_event, and the screenshot is a real
# CopyFromScreen of the window rect -- PrintWindow comes back black on Avalonia GPUs.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$OutDir = "$env:TEMP\andzip-gui-probe",
    [string[]]$Steps = @('shot:01-open'),
    [string[]]$ExeArgs = @(),
    [switch]$KeepOpen
)

if ($Steps.Count -eq 1 -and $Steps[0].Contains(';')) { $Steps = $Steps[0].Split(';') }

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class P {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  public static void Click(int x, int y) {
    SetCursorPos(x, y);
    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
  }
}
'@

# Without this the coordinates and the clicks are virtualised by the DPI scale, so a
# click measured off a screenshot lands somewhere else entirely.
[void][P]::SetProcessDPIAware()

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem $OutDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force

$process = if ($ExeArgs.Count -gt 0) {
    Start-Process -FilePath $Exe -ArgumentList $ExeArgs -PassThru
} else {
    Start-Process -FilePath $Exe -PassThru
}

function Rect {
    $r = New-Object P+R
    [void][P]::GetWindowRect($process.MainWindowHandle, [ref]$r)
    return $r
}

function Shot([string]$name) {
    [void][P]::SetForegroundWindow($process.MainWindowHandle)
    [void][P]::BringWindowToTop($process.MainWindowHandle)
    Start-Sleep -Milliseconds 600
    $r = Rect
    $w = $r.Rt - $r.L
    $h = $r.B - $r.T
    if ($w -le 1 -or $h -le 1) { Write-Host "  skip degenerate $name"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $path = Join-Path $OutDir ("$name.png")
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host ("  shot {0} ({1} x {2})" -f $path, $w, $h)
}

# The window appears a moment after the process, and /selftest never has one at all,
# so poll instead of sleeping a fixed guess.
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline -and $process.MainWindowHandle -eq [IntPtr]::Zero) {
    Start-Sleep -Milliseconds 300
    $process.Refresh()
}
if ($process.MainWindowHandle -eq [IntPtr]::Zero -and -not $process.WaitForExit(10)) {
    throw "pid=$($process.Id) started but never showed a window."
}

$process.Refresh()
$r0 = Rect
Write-Host ("pid={0} window={1},{2} {3}x{4}" -f $process.Id, $r0.L, $r0.T, ($r0.Rt - $r0.L), ($r0.B - $r0.T))

foreach ($step in $Steps) {
    $kind = $step.Split(':')[0]
    $rest = if ($step.Contains(':')) { $step.Split(':', 2)[1] } else { '' }
    switch ($kind) {
        'shot' { Shot $rest }
        'wait' { Start-Sleep -Milliseconds ([int]$rest) }
        'click' {
            $xy = $rest.Split(',')
            $r = Rect
            [P]::Click($r.L + [int]$xy[0], $r.T + [int]$xy[1])
            Write-Host ("  click +{0},+{1}" -f $xy[0], $xy[1])
            Start-Sleep -Milliseconds 500
        }
        'key' { [System.Windows.Forms.SendKeys]::SendWait($rest); Start-Sleep -Milliseconds 500 }
        default { throw "unknown step '$step' (expected shot:/wait:/click:/key:)" }
    }
}

if (-not $KeepOpen -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
