<#
  Run the Andzip main program from source, without Visual Studio.

  Why this exists: pressing F5 in Visual Studio depends on the *startup project*
  recorded in that IDE's per-user .suo state, and .slnx cannot carry it. When that
  state points at a class library VS refuses to start with
  "cannot start project with class library output type". This script needs no IDE
  state at all: it builds what is missing and launches the app.

  Usage:
    powershell -ExecutionPolicy Bypass -File run.ps1              # build + run (Debug)
    powershell -ExecutionPolicy Bypass -File run.ps1 -Configuration Release
    powershell -ExecutionPolicy Bypass -File run.ps1 -Rebuild     # force a full rebuild
    powershell -ExecutionPolicy Bypass -File run.ps1 -Check       # build + both test suites, no window

  NOTE: keep this script ASCII-only (Windows PowerShell 5.1 reads non-BOM UTF-8 as ANSI).
#>
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [switch]$Rebuild,
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'src\Andzip.App\Andzip.App.csproj'

# The engine is a prebuilt binary staged inside this folder, so a normal run needs
# no CMake and no Visual Studio C++ workload. Fail early with the real reason
# instead of letting the app start and report "compression engine unavailable".
$engine = Join-Path $root 'src\Andzip.Native\runtimes\win-x64\native\andzip_core.dll'
if (-not (Test-Path -LiteralPath $engine)) {
    throw @"
$engine is missing.
Restore it by rebuilding the compression engine:
    powershell -ExecutionPolicy Bypass -File native\scripts\build-windows.ps1 -RunTests
"@
}

if ($Rebuild) {
    dotnet build $app -c $Configuration --no-incremental --nologo
} else {
    dotnet build $app -c $Configuration --nologo
}
if ($LASTEXITCODE -ne 0) { throw "Build failed ($Configuration)." }

if ($Check) {
    dotnet test (Join-Path $root 'Andzip.slnx') -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw "xUnit suite failed." }
    # The shell self-check is a console runner: dotnet test cannot discover it.
    dotnet run --project (Join-Path $root 'src\Andzip.Native.Tests') -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "shell self-check failed." }
    Write-Host "Build and both test suites are green."
    exit 0
}

# Launch the built apphost directly rather than through "dotnet run", so the process
# is a plain window we can point at when something goes wrong.
$exe = Join-Path $root "src\Andzip.App\bin\$Configuration\net10.0\Andzip.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "expected $exe after building." }
Start-Process -FilePath $exe | Out-Null
Write-Host "started $exe"
