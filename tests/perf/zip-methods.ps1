<#
.SYNOPSIS
  Which ZIP methods the engine really writes, and what the entry ends up as.

.DESCRIPTION
  Stage P2-a needed one fact that reading the code could not give: whether the
  vendor's Deflate64 encoder is actually reachable from the shell's ZIP method
  list. It is -- which is why the -DZ7_DEFLATE_EXTRACT_ONLY route (plan A in
  docs/perf-p2a-registry.md) is not free: the same define would strip that
  encoder too.

  This script writes the same 256 KiB input with every method the UI lists for
  ZIP (plus LZMA2, the one it must not list) and records both the archive size
  and the method the entry was *written as*, read back from "azcore list".
  That second column is the point: on incompressible bytes 7-Zip's ZIP handler
  keeps the smaller representation, so every method lands on "Store" and every
  archive is input + 146 bytes. Size alone cannot tell those two cases apart.

  Deterministic inputs: random bytes from a fixed seed, and the first 256 KiB of
  the baseline text corpus when bench.ps1 has produced it.

  Output: docs/perf-zip-methods.tsv (tab separated, header included).

.EXAMPLE
  powershell -File tests/perf/zip-methods.ps1

.NOTES
  Keep this file pure ASCII (every .ps1 in this repo does).
#>
param(
  [string]$Azcore = '',
  [string]$BenchData = '',
  [string]$WorkDir = '',
  [string]$Tsv = '',
  [int]$Level = 3,
  [int]$KiB = 256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Azcore) { $Azcore = Join-Path $repoRoot 'build\native\bin\azcore.exe' }
if (-not (Test-Path -LiteralPath $Azcore)) { throw "azcore not found: $Azcore (build the engine first)" }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'andzip-perf-zipmethods' }
if (-not $Tsv) { $Tsv = Join-Path $repoRoot 'docs\perf-zip-methods.tsv' }
if (-not $BenchData) { $BenchData = Join-Path $env:TEMP 'andzip-perf-bench\data' }

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

$want = $KiB * 1KB

# A fixed seed, so re-running this compares against its own previous rows.
$randFile = Join-Path $WorkDir 'rand.bin'
$rng = New-Object System.Random (20261018)
$bytes = New-Object byte[] $want
for ($i = 0; $i -lt $want; $i++) { $bytes[$i] = [byte]$rng.Next(0, 256) }
[IO.File]::WriteAllBytes($randFile, $bytes)

$inputs = [ordered]@{ 'rand' = $randFile }
$textCorpus = Join-Path $BenchData 'text-single\all.bin'
if (Test-Path -LiteralPath $textCorpus) {
  $textFile = Join-Path $WorkDir 'text.bin'
  $in = [IO.File]::OpenRead($textCorpus)
  try {
    $buffer = New-Object byte[] $want
    $read = 0
    while ($read -lt $want) {
      $got = $in.Read($buffer, $read, $want - $read)
      if ($got -le 0) { break }
      $read += $got
    }
  } finally { $in.Dispose() }
  if ($read -ne $want) { throw "short read from $textCorpus" }
  [IO.File]::WriteAllBytes($textFile, $buffer)
  $inputs['text'] = $textFile
} else {
  Write-Output "  note: baseline text corpus not found, only the incompressible input is swept"
}

# The seven names CompressionMethodOption hands to the engine for ZIP, plus LZMA2,
# which is deliberately no longer in that list.
$methods = @('Deflate', 'Copy', 'BZip2', 'LZMA', 'Deflate64', 'PPMd', 'LZMA2')

Set-Content -LiteralPath $Tsv -Value "input`tinput_bytes`tmethod`tlevel`texit`tarchive_bytes`tentry_written_as`tentry_packed_bytes`tnote" -Encoding ASCII

foreach ($kind in $inputs.Keys) {
  $src = $inputs[$kind]
  foreach ($method in $methods) {
    $archive = Join-Path $WorkDir ("{0}-{1}.zip" -f $kind, $method)
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $created = & $Azcore create $archive -f zip -m $method -x $Level $src 2>&1 | Out-String
    $createCode = $LASTEXITCODE
    $archiveBytes = 0L
    $writtenAs = ''
    $packed = 0L
    if ($createCode -eq 0) {
      $archiveBytes = (Get-Item -LiteralPath $archive).Length
      # Read back what the entry actually is: field 4 of the data line azcore list prints
      # ("Store", "Deflate", "LZMA:eos", ...). The listing has a blank first line, a
      # "format:" line, a column header, the entries, and a trailing count -- so pick the
      # line that starts with a number instead of counting lines.
      $listed = & $Azcore list $archive 2>&1 | Out-String
      $line = ((($listed -split "`r?`n") | Where-Object { $_ -match '^\s*\d+\s+\d+\s+' }) | Select-Object -First 1)
      $fields = @(($line -split '\s+') | Where-Object { $_ })
      if ($fields.Count -ge 4) { $writtenAs = $fields[3]; $packed = [long]$fields[1] }
    }
    $ErrorActionPreference = $previousPreference

    $note = ''
    if ($createCode -ne 0) {
      $note = ((($created -split "`r?`n") | Where-Object { $_ }) | Select-Object -Last 1)
    }
    Add-Content -LiteralPath $Tsv -Value (@($kind, $want, $method, $Level, $createCode,
      $archiveBytes, $writtenAs, $packed, ($note -replace "`t", ' ')) -join "`t") -Encoding ASCII
    Write-Output ('  {0,-5} {1,-10} exit={2} archive={3,-9} written_as={4,-10} packed={5}' -f `
      $kind, $method, $createCode, $archiveBytes, $writtenAs, $packed)
    if ($note) { Write-Output "      $note" }
  }
}

Write-Output "== written =="
Write-Output "  $Tsv"
