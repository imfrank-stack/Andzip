<#
.SYNOPSIS
    Builds and checks every native target the engine claims to support, so the
    status tables in native/README.md can be restated from one command.

.DESCRIPTION
    Windows (MSVC): shared DLL + static library, each with azcore, azgui and the
    532-assertion native test suite, tests executed.
    Linux (Zig cross): x86_64 + aarch64 shared libraries with the export table
    verified against andzip.h, plus an x86_64 static library linked into azcore
    (it is only *linked*, not run: this machine has no emulator).

    Nothing is deleted; each target keeps its own build directory. The child
    scripts are invoked in-process, so this runs under both Windows PowerShell
    5.1 and pwsh without needing either on PATH.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File native/scripts/verify-all-targets.ps1
    powershell -ExecutionPolicy Bypass -File native/scripts/verify-all-targets.ps1 -SkipWindows
#>
[CmdletBinding()]
param(
    [switch]$SkipWindows,
    [switch]$SkipLinux
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSScriptRoot) '..')).Path
Set-Location $RepoRoot

function Invoke-NativeStep {
    param([string]$Label, [scriptblock]$Body)
    Write-Host "`n=== $Label ==="
    $global:LASTEXITCODE = 0   # only an external command run by this step may set it
    # try/catch as well as an exit-code check: a missing tool raises an error that
    # leaves $LASTEXITCODE at 0, so without this a dead step still reported green.
    try {
        . $Body
        if ($LASTEXITCODE -ne 0) { throw "an external command exited $LASTEXITCODE" }
    } catch {
        throw "$Label FAILED: $($_.Exception.Message)"
    }
}

function Add-ToolchainToPath {
    # CMake and Ninja ship inside Visual Studio and are not on PATH here; use the
    # same discovery order build-windows.ps1 relies on (vswhere, then known roots).
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'vswhere.exe not found; install the Visual Studio C++ workload.'
    }
    $vs = (& $vswhere -latest -prerelease -products * -property installationPath |
           Select-Object -First 1).Trim()
    $dirs = @(
        (Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin'),
        (Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja')
    )
    foreach ($d in $dirs) {
        if (-not (Test-Path -LiteralPath $d)) { throw "expected CMake/Ninja under $d" }
    }
    $env:PATH = ($dirs -join ';') + ';' + $env:PATH
    Write-Host ("cmake/ninja from: " + $dirs[0])
}

function Build-LinuxTarget {
    param(
        [Parameter(Mandatory = $true)][string]$Dir,
        [Parameter(Mandatory = $true)][string]$Triple,
        [switch]$Static
    )

    $cmakeArgs = @(
        '-S', 'native', '-B', $Dir, '-G', 'Ninja',
        '-DCMAKE_BUILD_TYPE=Release',
        "-DCMAKE_TOOLCHAIN_FILE=$RepoRoot/native/cmake/toolchain-zig-linux.cmake",
        "-DAZ_ZIG_TARGET=$Triple"
    )
    if ($Static) {
        # Static: build the CLI tool so the archive is actually linked into an
        # executable -- that is what proves the global coder registry survived.
        $cmakeArgs += '-DANDZIP_BUILD_SHARED=OFF'
    } else {
        $cmakeArgs += '-DANDZIP_BUILD_TESTS=OFF'
    }

    cmake @cmakeArgs
    if ($LASTEXITCODE -ne 0) { throw "configure failed for $Dir" }

    $target = if ($Static) { 'azcore' } else { 'andzip_core' }
    cmake --build $Dir --target $target
    if ($LASTEXITCODE -ne 0) { throw "build failed for $Dir" }

    $so = Join-Path $Dir 'bin/libandzip_core.so'
    if (Test-Path $so) {
        & (Join-Path $PSScriptRoot 'check-exports.ps1') $so
    } else {
        $a = Get-ChildItem -Path (Join-Path $Dir 'lib') -Filter '*.a' -ErrorAction SilentlyContinue |
             Select-Object -First 1
        if (-not $a) { throw "no static archive produced under $Dir/lib" }
        Write-Host ("static archive: {0} ({1:N1} MB), linked into {2}" -f `
            $a.Name, ($a.Length / 1MB), (Join-Path $Dir 'bin/azcore.exe'))
    }
}

if (-not $SkipWindows) {
    Invoke-NativeStep 'Windows x64 shared library + native tests' {
        & (Join-Path $PSScriptRoot 'build-windows.ps1') -RunTests
    }
    Invoke-NativeStep 'Windows x64 static library (azcore + azgui + tests)' {
        & (Join-Path $PSScriptRoot 'build-windows.ps1') -Static -BuildRoot (Join-Path $RepoRoot 'build/native-static') -RunTests
    }
}

if (-not $SkipLinux) {
    $zig = Join-Path $env:LOCALAPPDATA 'andzip\toolchains\zig\zig.exe'
    if (-not (Test-Path $zig)) {
        throw "zig not found at $zig; run native/scripts/provision-toolchain.ps1 first"
    }
    # The toolchain file resolves the compiler from the environment because CMake's
    # try_compile projects do not inherit -D cache entries from the command line.
    $env:AZ_ZIG_EXE = $zig
    Add-ToolchainToPath

    Invoke-NativeStep 'Linux x86_64 shared library'    { Build-LinuxTarget -Dir 'build/linux-x64'  -Triple 'x86_64-linux-gnu' }
    Invoke-NativeStep 'Linux aarch64 shared library'   { Build-LinuxTarget -Dir 'build/linux-arm64' -Triple 'aarch64-linux-gnu' }
    Invoke-NativeStep 'Linux x86_64 static library'    { Build-LinuxTarget -Dir 'build/linux-x64-static' -Triple 'x86_64-linux-gnu' -Static }
}

Write-Host "`nAll requested native targets built."
