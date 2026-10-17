<#
.SYNOPSIS
  Compression throughput baseline: how much does "parallel per entry" actually buy us?

.DESCRIPTION
  Generates three deterministic corpora, then compresses each of them twice --
  once as ONE big file, once as N files (N = logical core count) -- with azcore.
  The MB/s ratio between those two shapes is the size of the hole we are about to
  dig into: ZIP's parallel unit is the entry, so a single big entry runs on one core
  no matter how many threads the engine has.

  Every number this script prints also lands in a TSV next to it (see -Tsv), so a
  later stage can diff against it instead of trusting a screenshot.

  Nothing here touches product code: it only runs build\native\bin\azcore.exe.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests/perf/bench.ps1 -Quick
  powershell -ExecutionPolicy Bypass -File tests/perf/bench.ps1

.NOTES
  Keep this file pure ASCII (every .ps1 in this repo does).
#>
param(
  [string]$Azcore = '',
  [string]$WorkDir = '',
  [long]$TotalMiB = 512,
  [int]$Repeat = 3,
  [string]$Tsv = '',
  [switch]$Quick
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Azcore) { $Azcore = Join-Path $repoRoot 'build\native\bin\azcore.exe' }
if (-not (Test-Path -LiteralPath $Azcore)) { throw "azcore not found: $Azcore (build the engine first)" }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'andzip-perf-bench' }
if (-not $Tsv) { $Tsv = Join-Path $repoRoot 'docs\perf-results.tsv' }

if ($Quick) { $TotalMiB = 64; $Repeat = 1 }

$coreCount = [Environment]::ProcessorCount
$totalBytes = $TotalMiB * 1MB
$partBytes = [long][Math]::Floor($totalBytes / $coreCount)
$totalBytes = $partBytes * $coreCount   # exact split, so single and multi hold the same bytes

function Get-MachineInfo {
  $rows = @()
  try {
    $cpu = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
    $rows += "cpu=$($cpu.Name)".Trim()
    $rows += "physical_cores=$($cpu.NumberOfCores)"
    $rows += "logical_cores=$($cpu.NumberOfLogicalProcessors)"
  } catch { $rows += 'cpu=unknown' }
  try {
    $sys = Get-CimInstance -ClassName Win32_ComputerSystem
    $rows += "ram_gb=$([Math]::Round($sys.TotalPhysicalMemory / 1GB, 1))"
  } catch { $rows += 'ram_gb=unknown' }
  try {
    # MediaType: 3 = SSD/HDD via MSFT_Disk; ask for the disk backing the working volume.
    $part = Get-Partition -ErrorAction Stop | Where-Object { $_.DriveLetter } |
      Select-Object -First 1
    $disk = Get-Disk -Number $part.DiskNumber -ErrorAction Stop
    $rows += "disk=$($disk.FriendlyName) media=$($disk.ProvisioningType) size_gb=$([Math]::Round($disk.Size / 1GB))"
  } catch { $rows += 'disk=unknown' }
  return $rows
}

# Deterministic corpora. Windows PowerShell 5.1 has no -shl/-shr and uint64
# arithmetic there would silently promote to double on overflow, so the byte
# streams come from System.Random with a fixed seed: same seed, same bytes, and
# the script prints a SHA-256 per corpus so any drift is visible rather than
# quietly changing the numbers under a later stage.
function New-Rng([int]$Seed) { return New-Object System.Random $Seed }

function Write-TextFile {
  param([string]$Path, [long]$Length, [int]$Seed)
  # High redundancy: repeating English-ish lines with a slowly changing counter.
  $line = 'the quick brown fox jumps over the lazy dog '
  $rng = New-Rng $Seed
  $buffer = New-Object byte[] 8192
  $stream = [IO.File]::Create($Path)
  try {
    $written = 0
    for ($counter = 0; $written -lt $Length; $counter++) {
      $chunk = [Math]::Min($buffer.Length, $Length - $written)
      $text = "{0:d9} {1}" -f ($counter % 1000000000), $line
      $bytes = [Text.Encoding]::ASCII.GetBytes($text)
      [Array]::Copy($bytes, 0, $buffer, 0, [Math]::Min($bytes.Length, $buffer.Length))
      for ($i = $bytes.Length; $i -lt $chunk; $i++) { $buffer[$i] = [byte]0x20 }
      $stream.Write($buffer, 0, $chunk)
      $written += $chunk
      if (($counter % 4096) -eq 0) { [void]$rng.Next() }   # keeps the generator honest, changes nothing visible
    }
  } finally { $stream.Dispose() }
}

function Write-MixedFile {
  param([string]$Path, [long]$Length, [int]$Seed)
  # Structured binary: a record layout with a monotone counter, a low-entropy field
  # and a pseudo-random tail -- the shape a real archive of databases/logs looks like.
  $record = 128
  $rng = New-Rng $Seed
  $scratch = New-Object byte[] 32
  $buffer = New-Object byte[] ($record * 64)
  $stream = [IO.File]::Create($Path)
  try {
    $written = 0
    $index = 0
    while ($written -lt $Length) {
      for ($r = 0; $r -lt 64; $r++) {
        $offset = $r * $record
        [BitConverter]::GetBytes([int]$index).CopyTo($buffer, $offset)
        for ($i = 4; $i -lt 12; $i++) { $buffer[$offset + $i] = [byte](($index * 7 + $i) -band 0x1F) }
        $rng.NextBytes($scratch)
        for ($i = 12; $i -lt 44; $i++) { $buffer[$offset + $i] = $scratch[($i - 12) % 32] }
        for ($i = 44; $i -lt $record; $i++) { $buffer[$offset + $i] = [byte](($i -band 7) + 0x30) }
        $index++
      }
      $chunk = [Math]::Min($buffer.Length, $Length - $written)
      $stream.Write($buffer, 0, $chunk)
      $written += $chunk
    }
  } finally { $stream.Dispose() }
}

function Write-RandomFile {
  param([string]$Path, [long]$Length, [int]$Seed)
  # Incompressible: already-compressed payloads behave the same way.
  $rng = New-Rng $Seed
  $buffer = New-Object byte[] 65536
  $stream = [IO.File]::Create($Path)
  try {
    $written = 0
    while ($written -lt $Length) {
      $rng.NextBytes($buffer)
      $chunk = [Math]::Min($buffer.Length, $Length - $written)
      $stream.Write($buffer, 0, $chunk)
      $written += $chunk
    }
  } finally { $stream.Dispose() }
}

function New-Corpus {
  param([string]$Kind, [long]$SizeBytes, [int]$Files, [string]$Directory)
  if (Test-Path -LiteralPath $Directory) { Remove-Item -LiteralPath $Directory -Recurse -Force }
  New-Item -ItemType Directory -Path $Directory | Out-Null
  $writer = switch ($Kind) {
    'text'  { 'Write-TextFile' }
    'mixed' { 'Write-MixedFile' }
    'rand'  { 'Write-RandomFile' }
    default { throw "unknown corpus: $Kind" }
  }
  $paths = @()
  $per = [long][Math]::Floor($SizeBytes / $Files)
  for ($i = 1; $i -le $Files; $i++) {
    $path = Join-Path $Directory ('part_{0:d3}.bin' -f $i)
    $length = if ($i -eq $Files) { $SizeBytes - $per * ($Files - 1) } else { $per }
    & $writer -Path $path -Length $length -Seed (7919 * $i + [int][char]$Kind[0])
    $paths += $path
  }
  return ,$paths
}

function Merge-ToSingle {
  param([string[]]$Parts, [string]$Target)
  $out = [IO.File]::Create($Target)
  try {
    foreach ($part in $Parts) {
      $in = [IO.File]::OpenRead($part)
      try { $in.CopyTo($out) } finally { $in.Dispose() }
    }
  } finally { $out.Dispose() }
}

function Invoke-Compress {
  param([string]$Archive, [string[]]$Inputs, [string]$Format, [string]$Method, [int]$Level)
  if (Test-Path -LiteralPath $Archive) { Remove-Item -LiteralPath $Archive -Force }
  $arguments = @('create', $Archive, '-f', $Format, '-m', $Method, '-x', "$Level") + $Inputs
  # Windows PowerShell 5.1 turns anything a native program writes to stderr into an
  # error record, which under Stop aborts the whole run -- azcore prints its progress
  # bar on stdout and a "solid applies to 7z only" note on stderr for every zip run.
  # Relax the preference just for the call, keep the text so a real failure still
  # reports what happened, and restore it right after.
  $previousPreference = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $stopwatch = [Diagnostics.Stopwatch]::StartNew()
  $captured = & $Azcore @arguments 2>&1 | Out-String
  $stopwatch.Stop()
  $ErrorActionPreference = $previousPreference
  if ($LASTEXITCODE -ne 0) {
    $tail = (@($captured -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 3) -join ' ')
    throw "azcore create failed (exit $LASTEXITCODE): $Format/$Method/$Level -- $tail"
  }
  $archiveBytes = (Get-Item -LiteralPath $Archive).Length
  return [pscustomobject]@{
    Seconds  = $stopwatch.Elapsed.TotalSeconds
    InBytes  = ($Inputs | ForEach-Object { (Get-Item -LiteralPath $_).Length } | Measure-Object -Sum).Sum
    OutBytes = $archiveBytes
  }
}

function Get-Median([double[]]$Values) {
  # @() matters: a single measurement arrives as a bare double, which has no Count
  # under Set-StrictMode, and -Quick runs exactly once per cell.
  $sorted = @($Values | Sort-Object)
  $mid = [int][Math]::Floor($sorted.Count / 2)
  if ($sorted.Count % 2) { return $sorted[$mid] }
  return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

Write-Output '== machine =='
foreach ($info in Get-MachineInfo) { Write-Output "  $info" }
Write-Output "  azcore=$Azcore"
Write-Output "  total_mib=$TotalMiB logical_cores=$coreCount repeat=$Repeat"

$primaryCorpora = @('mixed')
$shapeCorpora = @('text', 'mixed', 'rand')
$zipLevels = if ($Quick) { @(1, 5) } else { @(1, 3, 5, 9) }
$sevenZLevels = if ($Quick) { @(5) } else { @(1, 5, 9) }

$results = New-Object System.Collections.Generic.List[object]

function Add-Cell {
  param([string]$Corpus, [string]$Shape, [string[]]$Inputs, [string]$Format, [string]$Method, [int]$Level)
  $seconds = @()
  $inBytes = 0
  $outBytes = 0
  for ($run = 1; $run -le $Repeat; $run++) {
    $archive = Join-Path $WorkDir ("out\{0}-{1}-{2}-{3}{4}.bin" -f $Corpus, $Shape, $Format, $Method, $Level)
    $dir = Split-Path $archive -Parent
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $one = Invoke-Compress -Archive $archive -Inputs $Inputs -Format $Format -Method $Method -Level $Level
    $seconds += $one.Seconds
    $inBytes = $one.InBytes
    $outBytes = $one.OutBytes
    Remove-Item -LiteralPath $archive -Force
    $mbps = [Math]::Round(($inBytes / 1MB) / $one.Seconds, 1)
    Write-Output ("  {0,-5} {1,-6} {2}/{3}{4} run{5}: {6} s  {7} MB/s" -f `
      $Corpus, $Shape, $Format, $Method, $Level, $run, [Math]::Round($one.Seconds, 1), $mbps)
  }
  $median = Get-Median ([double[]]$seconds)
  # [Math]::Max has no array overload; Measure-Object gives both ends in one pass.
  $spreadStats = @($seconds) | Measure-Object -Maximum -Minimum
  $spread = if ($median -gt 0) { ($spreadStats.Maximum - $spreadStats.Minimum) / $median } else { 0 }
  $results.Add([pscustomobject]@{
    Corpus = $Corpus; Shape = $Shape; Format = $Format; Method = $Method; Level = $Level
    InBytes = $inBytes; OutBytes = $outBytes; MedianSeconds = [Math]::Round($median, 2)
    MBps = [Math]::Round(($inBytes / 1MB) / $median, 1)
    Ratio = [Math]::Round($outBytes / $inBytes, 4)
    SpreadPct = [Math]::Round($spread * 100, 1)
  })
}

foreach ($corpus in ($primaryCorpora + $shapeCorpora | Select-Object -Unique)) {
  Write-Output "== corpus $corpus =="
  $partsDir = Join-Path $WorkDir "data\$corpus-parts"
  $singleDir = Join-Path $WorkDir "data\$corpus-single"
  $parts = New-Corpus -Kind $corpus -SizeBytes $totalBytes -Files $coreCount -Directory $partsDir
  New-Item -ItemType Directory -Path $singleDir -Force | Out-Null
  $singlePath = Join-Path $singleDir 'all.bin'
  Merge-ToSingle -Parts $parts -Target $singlePath
  $singleBytes = (Get-Item -LiteralPath $singlePath).Length
  $partsBytes = ($parts | ForEach-Object { (Get-Item -LiteralPath $_).Length } | Measure-Object -Sum).Sum
  if ($singleBytes -ne $partsBytes) { throw "single/parts byte mismatch for $corpus" }
  $digest = (Get-FileHash -LiteralPath $singlePath -Algorithm SHA256).Hash
  Write-Output "  bytes=$singleBytes sha256=$digest"

  if ($primaryCorpora -contains $corpus) {
    foreach ($level in $zipLevels) {
      Add-Cell -Corpus $corpus -Shape 'single' -Inputs @($singlePath) -Format 'zip' -Method 'Deflate' -Level $level
      Add-Cell -Corpus $corpus -Shape 'multi' -Inputs $parts -Format 'zip' -Method 'Deflate' -Level $level
    }
    foreach ($level in $sevenZLevels) {
      Add-Cell -Corpus $corpus -Shape 'single' -Inputs @($singlePath) -Format '7z' -Method 'LZMA2' -Level $level
      Add-Cell -Corpus $corpus -Shape 'multi' -Inputs $parts -Format '7z' -Method 'LZMA2' -Level $level
    }
  }
  if ($shapeCorpora -contains $corpus) {
    # The headline number: same bytes, same settings, one entry vs N entries.
    Add-Cell -Corpus $corpus -Shape 'single' -Inputs @($singlePath) -Format 'zip' -Method 'Deflate' -Level 5
    Add-Cell -Corpus $corpus -Shape 'multi' -Inputs $parts -Format 'zip' -Method 'Deflate' -Level 5
  }
}

$tsvHeader = 'corpus', 'shape', 'format', 'method', 'level', 'in_bytes', 'out_bytes',
              'median_seconds', 'mb_per_s', 'ratio', 'spread_pct' -join "`t"
$lines = @($tsvHeader)
foreach ($row in $results) {
  $lines += (@($row.Corpus, $row.Shape, $row.Format, $row.Method, $row.Level, $row.InBytes,
               $row.OutBytes, $row.MedianSeconds, $row.MBps, $row.Ratio, $row.SpreadPct) -join "`t")
}
Set-Content -LiteralPath $Tsv -Value $lines -Encoding ASCII
Write-Output "== wrote $Tsv =="

Write-Output '== headline: single vs multi (zip/Deflate/5) =='
foreach ($corpus in $shapeCorpora) {
  $single = $results | Where-Object { $_.Corpus -eq $corpus -and $_.Shape -eq 'single' -and
    $_.Format -eq 'zip' -and $_.Method -eq 'Deflate' -and $_.Level -eq 5 } | Select-Object -First 1
  $multi = $results | Where-Object { $_.Corpus -eq $corpus -and $_.Shape -eq 'multi' -and
    $_.Format -eq 'zip' -and $_.Method -eq 'Deflate' -and $_.Level -eq 5 } | Select-Object -First 1
  if ($single -and $multi) {
    Write-Output ("  {0,-5} single={1} MB/s multi={2} MB/s  hole={3}x" -f `
      $corpus, $single.MBps, $multi.MBps, [Math]::Round($multi.MBps / $single.MBps, 2))
  }
}
