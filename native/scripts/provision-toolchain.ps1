<#
.SYNOPSIS
  Fetches the portable toolchain pieces the cross builds need, into a prefix
  inside %LOCALAPPDATA% -- nothing is installed system-wide and nothing is
  touched outside that prefix.

.DESCRIPTION
  Zig is what makes a Linux build possible from a Windows box: `zig cc` wraps
  clang and ships libc/libc++ for many targets, so one ~100 MB download gives a
  working cross toolchain for glibc, musl and aarch64. cmake/ninja are only
  located (Visual Studio bundles both); they are not downloaded, because a
  guessed URL for a build tool is worse than telling the user to install one.

.EXAMPLE
  pwsh native/scripts/provision-toolchain.ps1
  pwsh native/scripts/provision-toolchain.ps1 -ZigVersion 0.17.0 -Force
  pwsh native/scripts/provision-toolchain.ps1 -Verify
#>
[CmdletBinding()]
param(
  [string] $ZigVersion = "0.17.0",
  [string] $Prefix = (Join-Path $env:LOCALAPPDATA "andzip\toolchains"),
  [switch] $Force,
  [switch] $Verify
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "Continue"

function Say($msg, $color = "White") { Write-Host $msg -ForegroundColor $color }

$zigDir  = Join-Path $Prefix "zig"
$zigExe  = Join-Path $zigDir "zig.exe"
$scratch = Join-Path $Prefix "downloads"

# ------------------------------------------------------------------ locate zig

$haveZig = $false
if (Test-Path -LiteralPath $zigExe) {
  $installed = & $zigExe version 2>$null
  $haveZig = ($installed -eq $ZigVersion)
  if ($haveZig -and -not $Force) {
    Say "zig $installed already present: $zigExe" Green
  } else { $haveZig = $false }
}

if (-not $haveZig) {
  # Zig's own site publishes exactly this file name per release.
  $url = "https://ziglang.org/download/$ZigVersion/zig-x86_64-windows-$ZigVersion.zip"
  New-Item -ItemType Directory -Force -Path $scratch, $zigDir | Out-Null
  $zip = Join-Path $scratch "zig-windows-$ZigVersion.zip"

  Say "downloading $url" Cyan
  Say "  (~100 MB; this is the whole cross toolchain, no system install)" DarkGray
  $ProgressPreference = 'SilentlyContinue'   # the PS 5.1 progress bar is very slow
  Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
  $ProgressPreference = 'Continue'

  $size = (Get-Item -LiteralPath $zip).Length
  if ($size -lt 10MB) { throw "download looks truncated: $size bytes" }

  Say "extracting" Cyan
  $tmp = Join-Path $scratch "zig-extract"
  if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
  Expand-Archive -LiteralPath $zip -DestinationPath $tmp

  # The archive wraps everything in one versioned directory; find zig.exe under it
  # rather than assuming its name.
  $found = Get-ChildItem -Path $tmp -Recurse -Filter zig.exe | Select-Object -First 1
  if (-not $found) { throw "zig.exe not found inside $zip" }
  Copy-Item -Path (Join-Path $found.DirectoryName "*") -Destination $zigDir -Recurse -Force
  Remove-Item -LiteralPath $tmp -Recurse -Force

  $installed = & $zigExe version 2>$null
  if (-not $installed) { throw "extracted zig.exe did not run" }
  Say "zig $installed -> $zigExe" Green
}

# ------------------------------------------------------------ locate the rest

# vswhere ships with the VS installer; CMake and Ninja live under the IDE's
# CommonExtensions when they came from VS, which is the case on this machine.
$found = @{}
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path -LiteralPath $vswhere) {
  $vsRoot = & $vswhere -latest -property installationPath | Select-Object -First 1
  if ($vsRoot) {
    $ext = Join-Path $vsRoot "Common7\IDE\CommonExtensions\Microsoft\CMake"
    foreach ($pair in @{ cmake = "CMake\bin\cmake.exe"; ninja = "Ninja\ninja.exe" }.GetEnumerator()) {
      $p = Join-Path $ext $pair.Value
      if (Test-Path -LiteralPath $p) { $found[$pair.Key] = $p }
    }
  }
}
foreach ($name in "cmake", "ninja") {
  if (-not $found.ContainsKey($name)) {
    $g = Get-Command "$name.exe" -ErrorAction SilentlyContinue
    if ($g) { $found[$name] = $g.Source }
  }
}

Say "`ntoolchain in $Prefix" White
foreach ($name in "zig", "cmake", "ninja") {
  $p = if ($name -eq "zig") { $zigExe } elseif ($found.ContainsKey($name)) { $found[$name] } else { $null }
  if ($p) { Say ("  {0,-6} {1}" -f $name, $p) Green }
  else    { Say ("  {0,-6} NOT FOUND -- install it, or use Visual Studio's bundled copy" -f $name) Yellow }
}

# ------------------------------------------------------------------- --verify

if ($Verify) {
  $samples = Join-Path $scratch "probe"
  New-Item -ItemType Directory -Force -Path $samples | Out-Null
  $src = Join-Path $samples "probe.cpp"
  Set-Content -LiteralPath $src -Value @'
#include <cstdio>
struct S { int x; };
int main() { S s{41}; std::printf("%d\n", s.x + 1); return 0; }
'@
  foreach ($triple in "x86_64-linux-gnu", "aarch64-linux-gnu", "x86_64-linux-musl") {
    $out = Join-Path $samples "probe-$triple"
    & $zigExe c++ "--target=$triple" -O2 -std=c++17 $src -o $out 2>&1 | Out-Null
    if ((Test-Path -LiteralPath $out) -and $LASTEXITCODE -eq 0) {
      Say "  c++ $triple  OK" Green
    } else {
      Say "  c++ $triple  FAILED" Red
    }
  }
  Say "`n(OK here means the target compiles and links; the binaries cannot be run on Windows.)" DarkGray
}

# ----------------------------------------------------------------- next steps

$cm = if ($found.ContainsKey("cmake")) { $found["cmake"] } else { "cmake" }
$nj = if ($found.ContainsKey("ninja")) { $found["ninja"] } else { "ninja" }
Say "`nconfigure a Linux build with:" White
@$"
  `$env:AZ_ZIG_EXE = `"$zigExe`"
  `"$cm`" -S native -B build/linux-x64 -G Ninja -DCMAKE_BUILD_TYPE=Release ``
        -DCMAKE_MAKE_PROGRAM=`"$nj`" ``
        -DCMAKE_TOOLCHAIN_FILE=`"$PWD\native\cmake\toolchain-zig-linux.cmake`" ``
        -DAZ_ZIG_TARGET=x86_64-linux-gnu -DANDZIP_BUILD_TESTS=OFF
  `"$cm`" --build build/linux-x64 --target andzip_core
" -join "`n" | ForEach-Object { Say "  $_" DarkGray }
