<#
  Andzip build & packaging (requirements §11.3)
  Usage:
    powershell -ExecutionPolicy Bypass -File build.ps1                # full pipeline (tests are a hard gate)
    powershell -ExecutionPolicy Bypass -File build.ps1 -SkipTests     # debug only
    powershell -ExecutionPolicy Bypass -File build.ps1 -Rid linux-x64

  Pipeline: read VersionNo.ini -> unit tests (abort on failure) -> publish (single-file + trim + compress) -> artifact -> size report
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
Write-Host "[1/4] Version: $version (Build $buildNo)"

# 2) Unit test gate (§11.1: no green, no package)
if (-not $SkipTests) {
    Write-Host "[2/4] Running unit tests (abort on failure)..."
    dotnet test (Join-Path $root "Andzip.slnx") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed; packaging aborted (requirements §11.1)." }

    # Andzip.Native.Tests is a console self-check runner (deliberately xUnit-free), so
    # "dotnet test" discovers zero tests in it. Run it explicitly, otherwise the gate
    # silently skips the 69 ABI / marshalling / handle-lifetime checks.
    $shellProj = Join-Path $root "src\Andzip.Native.Tests\Andzip.Native.Tests.csproj"
    dotnet run --project $shellProj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "Native shell checks failed; packaging aborted (requirements §11.1)." }
} else {
    Write-Host "[2/4] Tests skipped (-SkipTests; debug only)."
}

# 3) Publish
Write-Host "[3/4] Publishing $Rid (single-file + trim + compress)..."
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
$artifact = Join-Path $root "dist\Andzip-v$version-$Rid.exe"
# Move-Item -Force does NOT replace an existing file on Windows (it only overrides the
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

$sizeMb = [math]::Round((Get-Item $artifact).Length / 1MB, 2)
Write-Host "[4/4] Artifact: $artifact ($sizeMb MB)"