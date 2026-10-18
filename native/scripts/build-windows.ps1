<#
.SYNOPSIS
    Builds libandzip_core (and the azcore tool + native tests) with MSVC.

.DESCRIPTION
    Locates Visual Studio through vswhere, imports the vcvars environment into
    this process, then drives CMake + Ninja. No Visual Studio project files are
    generated, so the build behaves the same as on Linux/macOS.

    Output lands in <repo>/build/native/bin, plus a copy under
    runtimes/<rid>/native/ which is where the Andzip.Native C# shell looks.

.EXAMPLE
    pwsh native/scripts/build-windows.ps1
    pwsh native/scripts/build-windows.ps1 -Arch arm64 -Config Debug
    pwsh native/scripts/build-windows.ps1 -ExtraCodecs -RunTests
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string]$Arch = 'x64',

    [ValidateSet('Debug', 'Release', 'RelWithDebInfo', 'MinSizeRel')]
    [string]$Config = 'Release',

    [string]$RepoRoot = '',
    [string]$BuildRoot = '',
    [string]$Generator = 'Ninja',

    [switch]$ExtraCodecs,   # ANDZIP_EXTRA_CODECS=ON  (zstd/lz4/brotli/lizard encoders)
    [switch]$Static,        # static library instead of a DLL
    [switch]$NoTools,
    [switch]$NoGui,         # skip the azgui manual verification UI
    [switch]$NoTests,
    [switch]$RunTests,
    [switch]$Clean,
    [int]$Jobs = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrEmpty($RepoRoot)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
    $RepoRoot  = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
}
if ([string]::IsNullOrEmpty($BuildRoot)) {
    $BuildRoot = Join-Path $RepoRoot 'build\native'
}

$rid = switch ($Arch) {
    'x64'   { 'win-x64' }
    'x86'   { 'win-x86' }
    'arm64' { 'win-arm64' }
}

# ---------------------------------------------------------------- Visual Studio
function Find-VisualStudio {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    }
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'vswhere.exe not found; install Visual Studio 2022 or newer with the C++ workload.'
    }

    $installPath = & $vswhere -latest -prerelease -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath | Select-Object -First 1
    if (-not $installPath) { throw 'No Visual Studio installation with the C++ toolset was found.' }
    return $installPath.Trim()
}

function Import-VcVars {
    param([string]$VsPath, [string]$TargetArch)

    $vcvars = Join-Path $VsPath 'VC\Auxiliary\Build\vcvarsall.bat'
    if (-not (Test-Path -LiteralPath $vcvars)) { throw "vcvarsall.bat not found at $vcvars" }

    Write-Host "Importing $vcvars ($TargetArch)"

    # Quoting a call to vcvarsall.bat through `cmd /c "... && set"` is fragile,
    # so run it from a generated batch file instead and replay the environment
    # it prints. Lines that are not NAME=value (the VS banner) are ignored.
    $bat = Join-Path $env:TEMP ('azvcvars_' + [guid]::NewGuid().ToString('N') + '.bat')
    Set-Content -LiteralPath $bat -Encoding ascii -Value @"
@echo off
call "$vcvars" $TargetArch
if errorlevel 1 exit /b 1
set
"@
    try {
        $output = & cmd.exe /c $bat
        if ($LASTEXITCODE -ne 0) { throw "vcvarsall.bat failed for $TargetArch" }
    }
    finally {
        Remove-Item -LiteralPath $bat -Force -ErrorAction SilentlyContinue
    }

    foreach ($line in $output) {
        if ($line -match '^([^=]+)=(.*)$') {
            [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
        }
    }

    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
        throw 'cl.exe is not on PATH after importing vcvarsall'
    }
}

function Find-Tool {
    param([string]$Name, [string]$VsPath, [string]$RelativeHint)

    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $bundled = Join-Path $VsPath $RelativeHint
    if (Test-Path -LiteralPath $bundled) { return $bundled }
    throw "$Name not found on PATH or at $bundled"
}

# ------------------------------------------------------------------------ build
$vsPath = Find-VisualStudio
Import-VcVars -VsPath $vsPath -TargetArch $Arch

# Force English compiler diagnostics; on a localized VS the default output is
# unreadable in a non-Unicode console.
$env:VSLANG = '1033'

$cmake = Find-Tool 'cmake.exe' $vsPath `
    'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Find-Tool 'ninja.exe' $vsPath `
    'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'

Write-Host "cmake : $cmake"
Write-Host "ninja : $ninja"
Write-Host "build : $BuildRoot  ($Config / $rid)"

if ($Clean -and (Test-Path -LiteralPath $BuildRoot)) {
    Write-Host 'cleaning build tree'
    Remove-Item -LiteralPath $BuildRoot -Recurse -Force
}

$cmakeArgs = @(
    '-S', (Join-Path $RepoRoot 'native'),
    '-B', $BuildRoot,
    '-G', $Generator,
    "-DCMAKE_BUILD_TYPE=$Config",
    "-DAZ_RID_HINT=$rid",
    "-DANDZIP_EXTRA_CODECS=$(if ($ExtraCodecs) { 'ON' } else { 'OFF' })",
    "-DANDZIP_BUILD_SHARED=$(if ($Static) { 'OFF' } else { 'ON' })",
    "-DANDZIP_BUILD_TOOLS=$(if ($NoTools) { 'OFF' } else { 'ON' })",
    "-DANDZIP_BUILD_GUI=$(if ($NoGui) { 'OFF' } else { 'ON' })",
    "-DANDZIP_BUILD_TESTS=$(if ($NoTests) { 'OFF' } else { 'ON' })"
)
if ($Generator -eq 'Ninja') {
    $cmakeArgs += "-DCMAKE_MAKE_PROGRAM=$ninja"
}

& $cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed' }

$buildArgs = @('--build', $BuildRoot, '--config', $Config, '--target', 'andzip_core')
if ($Jobs -gt 0) { $buildArgs += @('-j', "$Jobs") }
& $cmake @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if (-not $NoTools) {
    & $cmake --build $BuildRoot --config $Config --target azcore
    if ($LASTEXITCODE -ne 0) { throw 'azcore build failed' }
}
if (-not $NoGui) {
    & $cmake --build $BuildRoot --config $Config --target azgui
    if ($LASTEXITCODE -ne 0) { throw 'azgui build failed' }
}
if (-not $NoTests) {
    & $cmake --build $BuildRoot --config $Config --target andzip_core_tests
    if ($LASTEXITCODE -ne 0) { throw 'andzip_core_tests build failed' }
}

$binDir = Join-Path $BuildRoot 'bin'
Write-Host ''
Write-Host "Artifacts in $binDir :"
Get-ChildItem -LiteralPath $binDir -File -ErrorAction SilentlyContinue |
    ForEach-Object { '  {0,-28} {1,10:N0} bytes' -f $_.Name, $_.Length }

# Hand the engine to the .NET side: Andzip.Native.csproj copies anything under its
# own runtimes/** to the output, and the resolver probes there second. Without this
# step a fresh clone builds the DLL but every managed test fails to find it.
#
# This copy writes INTO the source tree, so it is limited to the two roots the product
# actually ships from. A second configuration of the same library -- the static build
# reconfigured with -DZ7_DEFLATE_EXTRACT_ONLY for the P2-a codec-registry probe
# (native/tests/probe/) -- overwrote the checked-in andzip_core.lib with a copy whose
# stock Deflate encoder was compiled out, and nothing said a word. Experiment trees keep
# their artifacts inside their own build root now.
$staged = Join-Path $binDir 'runtimes'
if (Test-Path -LiteralPath $staged) {
    $normalize = { param([string]$Path) [IO.Path]::GetFullPath($Path).TrimEnd('\',[IO.Path]::DirectorySeparatorChar) }
    $productRoots = @((Join-Path $RepoRoot 'build\native'), (Join-Path $RepoRoot 'build\native-static')) |
        ForEach-Object { if (Test-Path -LiteralPath $_) { & $normalize $_ } }
    $thisRoot = if (Test-Path -LiteralPath $BuildRoot) { & $normalize $BuildRoot } else { '' }
    if ($thisRoot -and @($productRoots | Where-Object { $_ -ieq $thisRoot }).Count -gt 0) {
        $shellRuntimes = Join-Path $RepoRoot 'src\Andzip.Native\runtimes'
        Copy-Item -Path (Join-Path $staged '*') -Destination $shellRuntimes -Recurse -Force
        Write-Host "staged engine -> $shellRuntimes"
    } else {
        Write-Host "  $BuildRoot is not a product build root: artifacts stay in $binDir, source tree untouched"
    }
}

if (-not $Static) {
    # The library's whole contract is "exactly what the header declares", so it
    # is cheaper to assert it on every build than to explain a regression later.
    $checker = Join-Path $RepoRoot 'native\scripts\check-exports.ps1'
    if (Test-Path -LiteralPath $checker) {
        Write-Host ''
        & $checker (Join-Path $binDir 'andzip_core.dll')
        if ($LASTEXITCODE -ne 0) { throw 'exported symbol set does not match andzip.h' }
    }
}

if ($RunTests) {
    Write-Host ''
    & (Join-Path $binDir 'andzip_core_tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'native tests failed' }
}
