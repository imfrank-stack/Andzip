<#
.SYNOPSIS
  Build and run the follow-up spike to stage P2-b: measure a streaming-capable fast Deflate
  library (zlib-ng) on the same corpus the engine rows and the libdeflate rows were measured
  on.

.DESCRIPTION
  P2-b rejected libdeflate on API shape, not on speed (docs/perf-p2b-libdeflate.md): it can
  only compress a whole buffer, and a ZIP entry is one long stream. P1-a had already named
  the criterion -- the coder has to offer Z_SYNC_FLUSH / a carried history. This script asks
  the price question for a library that does have it, so the decision to open a next stage is
  made on numbers instead of on reputation.

  The checkout and the build live under build/, which is gitignored; nothing here can write
  into third_party/ or into a product build tree (same rule as the P2-a probe: an experiment
  must not have a path into the repository). Only the release build of the library is touched,
  and the product build script is never invoked.

  Needs the same toolset as the native build (cl.exe + cmake + ninja via Visual Studio) and git
  on PATH. On this machine git needs a proxy to reach github; pass it with -Proxy.

.EXAMPLE
  powershell -File native/tests/probe/build-and-run-zlibng.ps1 -Proxy http://127.0.0.1:2080

.EXAMPLE
  powershell -File native/tests/probe/build-and-run-zlibng.ps1 -Out docs/perf-zlib-ng.tsv

.NOTES
  Keep this file pure ASCII (every .ps1 in this repo does).
#>
param(
  [string]$DataFile = '',
  [string]$Proxy = '',
  [string]$Out = '',
  [switch]$SkipClone
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$nativeRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$repoRoot   = (Resolve-Path (Join-Path $nativeRoot '..')).Path
$spikeRoot  = Join-Path $repoRoot 'build\zlibng-spike'
$srcRoot    = Join-Path $spikeRoot 'src'
$buildRoot  = Join-Path $spikeRoot 'build'

function Find-VsPath {
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (-not (Test-Path -LiteralPath $vswhere)) { throw "vswhere not found: $vswhere" }
  $found = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
  if (-not $found) { throw 'no Visual Studio with the C++ toolset found' }
  return ($found | Select-Object -First 1)
}

function Find-Tool([string]$Name, [string]$VsPath, [string]$Relative) {
  $cmd = Get-Command $Name -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $candidate = Join-Path $VsPath $Relative
  if (Test-Path -LiteralPath $candidate) { return $candidate }
  throw "$Name not found (PATH, nor $candidate)"
}

function Import-VcVars([string]$VsPath) {
  $vcvars = Join-Path $VsPath 'VC\Auxiliary\Build\vcvars64.bat'
  if (-not (Test-Path -LiteralPath $vcvars)) { throw "vcvars64.bat not found: $vcvars" }
  $lines = & cmd.exe /d /c "`"$vcvars`" >nul 2>&1 && set"
  foreach ($line in $lines) {
    if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
  }
  if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'cl.exe is not on PATH after importing vcvars64' }
}

if (-not $DataFile) { $DataFile = Join-Path $env:TEMP 'andzip-perf-tiers\mixed-128.bin' }
if (-not (Test-Path -LiteralPath $DataFile)) {
  throw "corpus not found: $DataFile (generate it with: powershell -File tests/perf/bench.ps1)"
}

if (-not $SkipClone -and -not (Test-Path -LiteralPath (Join-Path $srcRoot 'CMakeLists.txt'))) {
  New-Item -ItemType Directory -Path $spikeRoot -Force | Out-Null
  $repo = 'https://github.com/zlib-ng/zlib-ng.git'
  Write-Output "== clone zlib-ng into $srcRoot =="
  if ($Proxy) {
    & git -c "http.proxy=$Proxy" clone --quiet --depth 1 $repo $srcRoot
  } else {
    & git clone --quiet --depth 1 $repo $srcRoot
  }
  if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit $LASTEXITCODE)" }
}
if (-not (Test-Path -LiteralPath (Join-Path $srcRoot 'CMakeLists.txt'))) {
  throw "no checkout at $srcRoot -- drop -SkipClone"
}
# The default branch is what gets measured, so the commit has to be on the record: without
# it the row cannot be re-run or compared a month later.
$head = (& git -C $srcRoot rev-parse --short HEAD)
Write-Output "checkout: zlib-ng default branch @ $head"

$vsPath = Find-VsPath
Import-VcVars -VsPath $vsPath
$env:VSLANG = '1033'
$cmake = Find-Tool 'cmake.exe' $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Find-Tool 'ninja.exe' $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'

New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

Write-Output '== configure (library only, zlib names, static) =='
# BUILD_TESTING=OFF is not cosmetic: with it ON the project FetchContent-clones GoogleTest
# from github during configure, which is a second network dependency this spike does not need.
& $cmake -S $srcRoot -B $buildRoot -G Ninja -DCMAKE_BUILD_TYPE=Release `
    -DZLIB_COMPAT=ON -DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON -DBUILD_TESTING=OFF `
    -DCMAKE_MAKE_PROGRAM="$ninja"
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed (exit $LASTEXITCODE)" }

Write-Output '== build the static library =='
# No --target: the CMake target name moves with the ZLIB_COMPAT branch (zlib-ng-static vs
# an alias of zlib-ng), and with tests off the default target is just that one library.
& $cmake --build $buildRoot
if ($LASTEXITCODE -ne 0) { throw "cmake build failed (exit $LASTEXITCODE)" }

$lib = Get-ChildItem -LiteralPath $buildRoot -Filter 'zlibstatic*.lib' -Recurse | Select-Object -First 1
if (-not $lib) { throw "no zlibstatic lib under $buildRoot" }
Write-Output "  lib: $($lib.FullName)"

$exe = Join-Path $buildRoot 'zlibng_spike.exe'
# Generated headers (zlib.h / zconf.h) land in the build root, the sources in the checkout.
& cl.exe /nologo /TC /O2 /MD /DNDEBUG /D_CRT_SECURE_NO_WARNINGS `
    "/I$srcRoot" "/I$buildRoot" "/Fo:$buildRoot\" `
    (Join-Path $PSScriptRoot 'zlibng_spike.c') /link "/OUT:$exe" $lib.FullName
if ($LASTEXITCODE -ne 0) { throw "cl.exe failed (exit $LASTEXITCODE)" }

Write-Output '== run =='
$name = [IO.Path]::GetFileNameWithoutExtension($DataFile) -replace '-\d+$', ''
$text = & $exe $DataFile $name
if ($LASTEXITCODE -ne 0) { throw "spike failed (exit $LASTEXITCODE)" }
# The commit goes into the table itself: the row is meaningless without knowing which build
# of a moving default branch produced it.
$lines = @($text | Where-Object { $_ -notmatch '^zlibng-version' })
$text | ForEach-Object { Write-Output $_ }

if ($Out) {
  $path = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $repoRoot $Out }
  # The sha is printed above and recorded in docs/perf-p2b-libdeflate.md next to the table;
  # a comment line would break the header-first convention every other perf-*.tsv follows.
  Set-Content -LiteralPath $path -Value (($lines -join "`r`n") + "`r`n") -Encoding ascii -NoNewline
  Write-Output "  wrote $path"
}
