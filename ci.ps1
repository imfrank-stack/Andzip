<#
  The CI gate, runnable locally. .github/workflows/ci.yml calls exactly this on
  Windows, so "it passes in CI" and "it passes on my machine" are the same claim.

  Sequence (each step aborts on failure):
    1. compression engine: configure + build + 642 native assertions + export-table check
    2. managed solution build
    3. tests + packaging via build.ps1, which itself gates on
       dotnet test AND the console self-check (174)

  Why step 3 does not just call "dotnet test": Andzip.Native.Tests is a console
  runner without an xUnit adapter, so "dotnet test" silently runs zero of its
  checks. build.ps1 runs it explicitly; see the development record under docs/ (section 7, item 11).

  Usage:
    powershell -ExecutionPolicy Bypass -File ci.ps1
    powershell -ExecutionPolicy Bypass -File ci.ps1 -SkipEngine   # after a manual engine build
    powershell -ExecutionPolicy Bypass -File ci.ps1 -Static       # also gate the static-library link
    powershell -ExecutionPolicy Bypass -File ci.ps1 -NoPackage    # tests only, skip the publish step

  Release configuration only: the CMake build dirs are single-config, so flipping
  to Debug would fight an existing cache.
#>
param(
    [switch]$SkipEngine,
    [switch]$Static,
    [switch]$NoPackage,
    [string]$Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$script:step = 0

# vcvars sets Platform=X64, and MSBuild reads that as a *solution* platform:
# "Release|x64" does not exist in Andzip.slnx (it is Any CPU only) and the build
# dies with MSB4126. This bites both when ci.ps1 runs from a Developer Prompt and
# when an engine script is dot-sourced into this process, so clear it up front and
# keep the engine scripts in child processes.
if ($env:Platform) {
    Write-Host "clearing inherited Platform=$env:Platform (the solution is Any CPU)"
    Remove-Item Env:\Platform -ErrorAction SilentlyContinue
}

function Invoke-Gate {
    param([Parameter(Mandatory = $true)][string]$Label, [Parameter(Mandatory = $true)][scriptblock]$Body)
    $script:step++
    Write-Host ''
    Write-Host ("=== step {0}: {1} ===" -f $script:step, $Label)
    # try/catch as well as the exit code: a missing tool raises an error that leaves
    # $LASTEXITCODE at 0, which would otherwise let a dead step pass as green.
    try {
        . $Body
        if ($LASTEXITCODE -ne 0) { throw "an external command exited $LASTEXITCODE" }
    } catch {
        throw ("step {0} ({1}) FAILED: {2}" -f $script:step, $Label, $_.Exception.Message)
    }
}

function Invoke-ChildPowerShell {
    param([Parameter(Mandatory = $true)][string]$Script, [string[]]$ScriptArgs = @())
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script) + $ScriptArgs
    & powershell.exe @argv
}

if ($SkipEngine) {
    Write-Host "`n=== engine build skipped (-SkipEngine) ==="
} else {
    Invoke-Gate 'engine: shared library + native tests' {
        # -ExtraCodecs is NOT optional any more: the UI offers ZSTD/LZ4/LZ5/LIZARD/BROTLI
        # for 7z, and Andzip.Native.Tests fails the package when the staged engine lacks
        # them -- an engine built plainly would ship five menu items that all fail.
        Invoke-ChildPowerShell -Script (Join-Path $root 'native\scripts\build-windows.ps1') -ScriptArgs @('-ExtraCodecs', '-RunTests')
    }
    Invoke-Gate 'engine: export table matches andzip.h' {
        & (Join-Path $root 'native\scripts\check-exports.ps1') (Join-Path $root 'build\native\bin\andzip_core.dll')
    }
    if ($Static) {
        # The static link is a separate product shape: without /WHOLEARCHIVE the
        # handler-registry objects are dropped and the library loads with an empty
        # format table. Gate it, do not assume it.
        Invoke-Gate 'engine: static library + native tests' {
            Invoke-ChildPowerShell -Script (Join-Path $root 'native\scripts\build-windows.ps1') `
                -ScriptArgs @('-Static', '-ExtraCodecs', '-BuildRoot', (Join-Path $root 'build\native-static'), '-RunTests')
        }
    }
}

Invoke-Gate 'managed: build solution' {
    dotnet build (Join-Path $root 'Andzip.slnx') -c Release --nologo
}

if ($NoPackage) {
    # Same gate build.ps1 applies, minus the publish.
    Invoke-Gate 'tests: xUnit + console self-check' {
        dotnet test (Join-Path $root 'Andzip.slnx') -c Release --no-build --nologo
        dotnet run --project (Join-Path $root 'src\Andzip.Native.Tests') -c Release --no-build
    }
} else {
    Invoke-Gate "package: $Rid single-file green exe" {
        & (Join-Path $root 'build.ps1') -Rid $Rid
    }
}

if (-not $NoPackage) {
    $artifact = Get-ChildItem -Path (Join-Path $root 'dist') -File |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host ''
    Write-Host ("gate green -- artifact: {0} ({1:N2} MB)" -f $artifact.Name, ($artifact.Length / 1MB))
} else {
    Write-Host ''
    Write-Host 'gate green.'
}
