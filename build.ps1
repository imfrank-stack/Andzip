<#
  Andzip build & packaging (requirements §11.3)
  Usage:
    powershell -ExecutionPolicy Bypass -File build.ps1                # full pipeline (tests are a hard gate)
    powershell -ExecutionPolicy Bypass -File build.ps1 -SkipTests     # debug only
    powershell -ExecutionPolicy Bypass -File build.ps1 -Rid linux-x64

  Pipeline: version -> tests -> publish -> embed check -> portable exe + setup exe
  NOTE: keep this script ASCII-only (Windows PowerShell 5.1 reads non-BOM UTF-8 as ANSI).
#>
param(
    [switch]$SkipTests,
    [string]$Rid = "win-x64",
    [string]$TrimMode = "full"      # verified working together with IncludeNativeLibrariesForSelfExtract (see csproj / CHANGELOG)
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# 1) Version (must be valid before packaging, see requirements §11.2)
$ini = Get-Content (Join-Path $root "VersionNo.ini") -Raw
$version = [regex]::Match($ini, '(?m)^\s*Version\s*=\s*([0-9]\.[0-9]\.[0-9])\s*$').Groups[1].Value
$buildNo = [regex]::Match($ini, '(?m)^\s*BuildNo\s*=\s*(\d+)\s*$').Groups[1].Value
if (-not $version -or -not $buildNo) {
    # The numbering rule is "three parts, each a SINGLE decimal digit 0-9, carry left".
    # A two-digit part (0.13.0, 0.9.10) is that rule being misread, so say so here --
    # VersionInfo.Parse enforces the same thing on the managed side.
    throw "VersionNo.ini: Version must be A.B.C with each part a single digit 0-9 (carry left when a part passes 9: 0.9.9 -> 1.0.0, not 0.10.0); BuildNo must be a number. Packaging refused."
}
Write-Host "[1/6] Version: $version (Build $buildNo)"

# 2) Unit test gate (§11.1: no green, no package)
if (-not $SkipTests) {
    Write-Host "[2/6] Running unit tests (abort on failure)..."
    dotnet test (Join-Path $root "Andzip.slnx") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed; packaging aborted (requirements §11.1)." }

    # Andzip.Native.Tests is a console self-check runner (deliberately xUnit-free), so
    # "dotnet test" discovers zero tests in it. Run it explicitly, otherwise the gate
    # silently skips the 174 ABI / marshalling / handle-lifetime checks -- including
    # the five extension coders the 7z method dropdown now offers.
    $shellProj = Join-Path $root "src\Andzip.Native.Tests\Andzip.Native.Tests.csproj"
    dotnet run --project $shellProj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Native shell checks failed; packaging aborted (requirements §11.1)." }
} else {
    Write-Host "[2/6] Tests skipped (-SkipTests; debug only)."
}

# 3) Publish
Write-Host "[3/6] Publishing $Rid (single-file + trim + compress)..."
$stage = Join-Path $root "dist\staging-$Rid"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

# IncludeAllContentForSelfExtract is REQUIRED, not an optimisation: andzip_core.dll
# reaches the app as a copied content item under runtimes/<rid>/native/, which is not
# a RID-graph asset, so IncludeNativeLibrariesForSelfExtract alone does NOT bundle it.
# Without this the single-file exe starts but reports "compression engine unavailable".
#
# The three switches at the bottom are the ones that actually move the needle
# (19.72 -> 19.34 MB, measured). Two tempting ones do not and were rejected:
#   UseSystemResourceKeys=true  -- zero size gain here, and it replaces framework
#       exception messages with resource KEYS, while our error dialogs show
#       ex.Message verbatim.
#   Framework-dependent publish -- LARGER (25.18 MB): it cannot be trimmed, and the
#       native libraries (Skia / ANGLE / HarfBuzz / the engine) dominate the bundle and
#       are not compressible in a single-file package. Self-contained + trim + compress
#       is the smaller option, not the bigger one.
dotnet publish (Join-Path $root "src\Andzip.App\Andzip.App.csproj") -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=$TrimMode -p:EnableCompressionInSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:DebuggerSupport=false -p:EventSourceSupport=false -p:MetadataTrimming=complete `
    -p:Version=$version -p:FileVersion="$version.0" -p:InformationalVersion="$version+$buildNo" `
    -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# 4) Artifact + size report (NFR-8 / AC-14)
$artifact = Join-Path $root "dist\Andzip-v$version-$Rid.exe"# Move-Item -Force does NOT replace an existing file on Windows (it only overrides the
# read-only attribute), so re-publishing the same version died with "cannot create a file
# when that file already exists" -- twice now. Remove first, then move.
if (Test-Path $artifact) { Remove-Item $artifact -Force }
Move-Item -Force (Join-Path $stage "Andzip.exe") $artifact
Remove-Item $stage -Recurse -Force

# The engine must be inside the bundle. "It published" is not the same as
# "it can compress": a missing native asset only shows up at run time, which is
# exactly the failure this gate exists to prevent.
$bytes = [System.IO.File]::ReadAllBytes($artifact)
$needle = [System.Text.Encoding]::ASCII.GetBytes("andzip_core.dll")
$found = $false
for ($i = 0; $i -le $bytes.Length - $needle.Length; $i++) {
    $ok = $true
    for ($j = 0; $j -lt $needle.Length; $j++) { if ($bytes[$i + $j] -ne $needle[$j]) { $ok = $false; break } }
    if ($ok) { $found = $true; break }
}
if (-not $found) { throw "$artifact does not embed andzip_core.dll -- the native engine would be missing at run time." }
Write-Host "      native engine embedded: OK"

# 5) Two ways to ship the same build (1.8.0):
#    - portable single file  : Andzip-vX.Y.Z-rid.exe          (run it, no install)
#    - "normal install"      : Andzip-Setup-vX.Y.Z-rid.exe    (same bytes, renamed)
# The installer is recognised by its OWN FILE NAME (InstallLayout.IsInstallerFile), so the
# setup artifact cannot drift from the app build -- it is literally the same bytes.
# Copy-Item, not a second publish: a second publish would re-trim and could differ.
$setup = Join-Path $root "dist\Andzip-Setup-v$version-$Rid.exe"
if (Test-Path $setup) { Remove-Item $setup -Force }
Copy-Item $artifact $setup

# 6) Run the install round-trip INSIDE the shipped artifact.
# Unit tests run the untrimmed Andzip.dll; the thing users run is trimmed + single-file +
# compressed, and some failures only exist there. 1.8.3 shipped an installer that could not
# create a .lnk because trimming removes .NET's built-in COM support -- all 1101 assertions
# were green while the packaged exe was broken. This gate is what closes that gap.
# (It installs into a temp directory with a probe registry key; real associations untouched.)
Write-Host "[6/6] Self-test on the packaged artifact (/selftest)..."
$selfOut = Join-Path $env:TEMP "andzip-selftest.log"
if (Test-Path $selfOut) { Remove-Item $selfOut -Force }
# -Wait, not $proc.WaitForExit(): with redirected output the unsynced Process object
# hands back an EMPTY ExitCode, and "empty -ne 0" reads as a failure on a passing run.
# (Windows PowerShell 5.1, measured 2026-10-14 -- the self-test had already printed PASS.)
$proc = Start-Process -FilePath $artifact -ArgumentList '/selftest' `
    -RedirectStandardOutput $selfOut -Wait -PassThru -WindowStyle Hidden
Get-Content $selfOut -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "      $_" }
$code = $proc.ExitCode
if ($code -ne 0) { throw "Artifact self-test failed (exit code $code); packaging aborted." }

$sizeMb = [math]::Round((Get-Item $artifact).Length / 1MB, 2)
Write-Host "      Artifacts ($sizeMb MB each):"
Write-Host "      $artifact"
Write-Host "      $setup"