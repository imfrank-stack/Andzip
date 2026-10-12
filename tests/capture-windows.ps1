# Capture real on-screen windows of the running app.
# NOTE: keep this file ASCII-only -- Windows PowerShell 5.1 reads non-BOM UTF-8 as
# ANSI and that breaks parsing (same reason as build.ps1).
#
# PrintWindow is unreliable for Avalonia (the GPU surface often comes back black), so
# we grab the window rectangle from the screen; that needs the window in the foreground.
# The main window handle comes from the Process object; dialogs are looked up by title,
# and if that fails we fall back to a full-screen shot (crude, but it proves the dialog
# actually painted and where it landed).
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$OutDir = "$env:TEMP\andzip-real-shots",
    [string[]]$DialogTitles = @(),
    [string[]]$Keys = @(),
    [string[]]$Clicks = @()
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T, Rt, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  public const uint LeftDown = 0x0002, LeftUp = 0x0004;
  public static void ClickAt(int x, int y) {
    SetCursorPos(x, y);
    mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
    mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
  }
}
'@

# Without this the screen coordinates and the mouse events are virtualised by the DPI
# scale, so a click computed from a screenshot lands somewhere else entirely.
[void][Win]::SetProcessDPIAware()

function Save-Rect([int]$x, [int]$y, [int]$w, [int]$h, [string]$path) {
  if ($w -le 1 -or $h -le 1) { Write-Host "  skip degenerate $path"; return }
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "  saved $path ($w x $h)"
}

function Save-Window([IntPtr]$h, [string]$path) {
  if ($h -eq [IntPtr]::Zero) { Write-Host "  no handle for $path"; return }
  [void][Win]::SetForegroundWindow($h)
  [void][Win]::BringWindowToTop($h)
  Start-Sleep -Milliseconds 700
  $r = New-Object Win+R
  [void][Win]::GetWindowRect($h, [ref]$r)
  Save-Rect $r.L $r.T ($r.Rt - $r.L) ($r.B - $r.T) $path
}

function Save-Screen([string]$path) {
  $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  Save-Rect $bounds.X $bounds.Y $bounds.Width $bounds.Height $path
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem $OutDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force

$process = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 5

try {
  $process.Refresh()
  Write-Host ("pid={0} main={1}" -f $process.Id, $process.MainWindowHandle)
  Save-Window $process.MainWindowHandle (Join-Path $OutDir "01-main.png")

  for ($i = 0; $i -lt $Keys.Count; $i++) {
    [System.Windows.Forms.SendKeys]::SendWait($Keys[$i])
    Start-Sleep -Seconds 2
    if ($i -lt $Clicks.Count -and $Clicks[$i]) {
      # Real pointer click at a screen coordinate: the nav list on the left of the
      # settings window has no keyboard shortcut to reach a specific page.
      $parts = $Clicks[$i].Split(',')
      [Win]::ClickAt([int]$parts[0], [int]$parts[1])
      Start-Sleep -Milliseconds 800
    }

    # Full screen: dialogs are separate top-level windows we would have to match by a
    # localised title, and a screen grab proves the same thing (it painted, where it is).
    Save-Screen (Join-Path $OutDir ("{0:d2}-after-{1}.png" -f ($i + 2), $i))
  }
}
finally {
  if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}
