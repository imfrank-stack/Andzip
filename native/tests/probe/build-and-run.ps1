<#
.SYNOPSIS
  Build and run the stage P2-a codec-registry probe against the static engine library.

.DESCRIPTION
  Nothing here is shipped or wired into the product build: this compiles one .cpp from
  native/tests/probe/ straight against build/native-static/lib/andzip_core.lib and runs
  it. That is the only way to ask the registry "who wins when two codecs share a MethodID"
  from outside the vendor tree -- the array and the lookup live inside the library, and
  /WHOLEARCHIVE is needed so the static-initialised registrations actually arrive
  (the same reason the native test suite links that way; see native/README.md).

  Pass -ExtractOnly to answer the OTHER half of P2-a: it reconfigures a second static
  build with -DZ7_DEFLATE_EXTRACT_ONLY (the flag the vendor itself uses in
  Bundles/Format7z/makefile, which sets the stock Deflate entry's CreateEncoder to NULL)
  and runs the same probe against it. That build takes ~20 minutes because the define
  changes the flags of every translation unit.

.EXAMPLE
  powershell -File native/tests/probe/build-and-run.ps1
  powershell -File native/tests/probe/build-and-run.ps1 -ExtractOnly

.NOTES
  Keep this file pure ASCII (every .ps1 in this repo does).
#>
param(
  [switch]$ExtractOnly,
  [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$nativeRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$repoRoot   = (Resolve-Path (Join-Path $nativeRoot '..')).Path
$vendor     = Join-Path $nativeRoot 'third_party\7zip'
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'build\verify-codecs' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function Find-VsPath {
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (-not (Test-Path -LiteralPath $vswhere)) { throw "vswhere not found: $vswhere" }
  $found = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
  if (-not $found) { throw 'no Visual Studio with the C++ toolset found' }
  return ($found | Select-Object -First 1)
}

function Import-VcVars([string]$VsPath) {
  $vcvars = Join-Path $VsPath 'VC\Auxiliary\Build\vcvars64.bat'
  if (-not (Test-Path -LiteralPath $vcvars)) { throw "vcvars64.bat not found: $vcvars" }
  # cmd.exe prints the environment; import it into this process the same way
  # native/scripts/build-windows.ps1 does, so cl.exe and the CRT headers are reachable.
  $lines = & cmd.exe /d /c "`"$vcvars`" >nul 2>&1 && set"
  foreach ($line in $lines) {
    if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
  }
  if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'cl.exe is not on PATH after importing vcvars64' }
}

$vsPath = Find-VsPath
Import-VcVars -VsPath $vsPath
$env:VSLANG = '1033'   # English compiler diagnostics; a localized VS writes GBK to the console

$libDir = Join-Path $repoRoot 'build\native-static\lib'
$lib = Join-Path $libDir 'andzip_core.lib'

if ($ExtractOnly) {
  $extractRoot = Join-Path $repoRoot 'build\native-static-extractonly'
  $libDir = Join-Path $extractRoot 'lib'
  $lib = Join-Path $libDir 'andzip_core.lib'
  if (-not (Test-Path -LiteralPath $lib)) {
    Write-Output '== configuring the flagged static build (recompiles everything; ~20 min) =='
    # CMake seeds CMAKE_CXX_FLAGS from the CXXFLAGS/CFLAGS environment on the first
    # configure of a build tree, so the vendor's own switch reaches every TU without
    # touching vendor sources or the product build script.
    $env:CXXFLAGS = '-DZ7_DEFLATE_EXTRACT_ONLY'
    $env:CFLAGS   = '-DZ7_DEFLATE_EXTRACT_ONLY'
    & (Join-Path $nativeRoot 'scripts\build-windows.ps1') -Static -ExtraCodecs -NoGui -NoTests -BuildRoot $extractRoot
    if ($LASTEXITCODE -ne 0) { throw 'flagged static build failed' }
    $ninja = Join-Path $extractRoot 'build.ninja'
    if (-not (Select-String -LiteralPath $ninja -Pattern 'Z7_DEFLATE_EXTRACT_ONLY' -Quiet)) {
      throw "the flagged define did not reach the build (grep $ninja) -- the probe would prove nothing"
    }
  }
}

if (-not (Test-Path -LiteralPath $lib)) { throw "static library missing: $lib (run: pwsh build.ps1 -Static, or use -ExtractOnly)" }

$exe = Join-Path $OutDir 'codec_registry_probe.exe'
$objDir = Join-Path $OutDir 'probe-obj'
New-Item -ItemType Directory -Path $objDir -Force | Out-Null

$includes = @(
  "/I$nativeRoot\include",
  "/I$nativeRoot\src",
  "/I$vendor",
  "/I$vendor\C",
  "/I$vendor\CPP",
  "/I$vendor\CPP\7zip"
)

$source = Join-Path $PSScriptRoot 'codec_registry_probe.cpp'

# cl compiles and links in one go: naming the object again on the link line would be
# a duplicate (LNK4042), so /Fo only decides where the intermediate lands.
$clArgs = @(
  '/nologo', '/EHsc', '/std:c++17', '/O2', '/MD', '/GR-',
  '/D_UNICODE', '/DUNICODE', '/D_WIN64', '/D_CRT_SECURE_NO_WARNINGS', '/DNDEBUG',
  "/Fo:$objDir\",
  $source,
  '/link',
  "/OUT:$exe",
  "/WHOLEARCHIVE:$lib",
  # The static library carries the same Windows imports the shared one does
  # (native/CMakeLists.txt:189) plus the legacy WinWrap char helpers, and a static
  # consumer has to name them itself -- CMake did that for azcore/tests, this probe
  # is outside the build so it repeats the list.
  'oleaut32.lib', 'ole32.lib', 'user32.lib', 'advapi32.lib', 'shell32.lib',
  'gdi32.lib', 'version.lib', 'mpr.lib'
)

Write-Output "== compile =="
Write-Output "  cl.exe $(($clArgs | Where-Object { $_ -notlike '/I*' }) -join ' ')"
Write-Output "  lib:   $lib"
& cl.exe @includes @clArgs
if ($LASTEXITCODE -ne 0) { throw "cl.exe failed (exit $LASTEXITCODE)" }

Write-Output '== run =='
& $exe
Write-Output "  exit=$LASTEXITCODE"
