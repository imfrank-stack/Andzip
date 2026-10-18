<#
.SYNOPSIS
  Stages P4 and P5: the level ladder and the 7z dictionary knob, measured not guessed.

.DESCRIPTION
  Two sweeps over the same deterministic bytes tests/perf/bench.ps1 produced:

    A (P4)  zip/Deflate at *every* engine level 0..9 -- the shipped four tiers
        (0/1/3/5/9) were inherited from LZMA and never drawn as a curve, so nobody
        knows whether a tier is a no-op. Single entry on purpose: P4 is about the
        coder's cost, and entry-parallelism is already measured in the baseline.
        The 7z ladder (sweep A2) runs on the same bytes; -Levels7z picks its levels.
    B (P5)  7z/LZMA2 with dict_size_mib in {16,32,64,128,256} at -DictLevels
        (default 5 and 9). LZMA2's block-parallel degree follows the dictionary:
        small dict -> more blocks -> more cores but worse ratio. Whether the UI
        should pick a dict per tier is the question this table answers, and the
        engine's own default per level is read back by matching the no--d cell
        against these rows byte for byte.

  -Corpora picks which baseline corpora to sweep (text / mixed / rand); default is
  text+mixed. Rows are appended to the same TSV, so a targeted extra pass
  (-Corpora rand -SkipDicts, or -DictLevels 1,3 -SkipLevels -SkipRepeat) completes
  the grid instead of re-measuring what is already in there.

  Sizes are trimmed from the baseline corpora (prefixes of the same bytes) because
  level 9 on 512 MiB single-entry measured 381.6 s a cell -- a nine-level ladder at
  that size would run for hours. The default is 128 MiB; pass -MiB to change it.

  Rows are appended to the TSV as they are measured, so a stopped job keeps what it got.

  Keep this file pure ASCII (every .ps1 in this repo does).
#>
param(
  [string]$Azcore = '',
  [string]$BenchData = '',
  [string]$WorkDir = '',
  [long]$MiB = 128,
  [string[]]$Corpora = @('text', 'mixed'),
  [int[]]$Levels = @(0, 1, 2, 3, 4, 5, 6, 7, 8, 9),
  [int[]]$Levels7z = @(0, 1, 2, 3, 4, 5, 6, 7, 8, 9),
  [int[]]$DictLevels = @(5, 9),
  [int[]]$Dicts = @(16, 32, 64, 128, 256),
  [int]$Repeat = 2,
  [string]$Tsv = '',
  [switch]$Append,
  [switch]$SkipLevels,
  [switch]$SkipDicts,
  [switch]$SkipRepeat,
  [switch]$SkipZip,
  [switch]$SkipStore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

# The engine does not reject an out-of-range level: measured 2026-10-18, both "-x 13" and
# "-x 2468" produced the level-9 artifact with exit=0. So a typo in one of the level lists
# below would silently write rows that look like measurements of real levels -- and that is
# exactly what happened: -Levels7z 2,4,6,8 passed through powershell.exe -File arrived as
# the single value 2468. Refuse to measure anything outside the engine's own range.
$levelLists = [ordered]@{ Levels = $Levels; Levels7z = $Levels7z; DictLevels = $DictLevels }
foreach ($name in $levelLists.Keys) {
  foreach ($level in $levelLists[$name]) {
    if ($level -lt 0 -or $level -gt 9) {
      throw "-$name contains ${level}: the engine accepts it and quietly clamps to 9, so this row would be a lie"
    }
  }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Azcore) { $Azcore = Join-Path $repoRoot 'build\native\bin\azcore.exe' }
if (-not (Test-Path -LiteralPath $Azcore)) { throw "azcore not found: $Azcore (build the engine first)" }
if (-not $BenchData) { $BenchData = Join-Path $env:TEMP 'andzip-perf-bench\data' }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'andzip-perf-pareto' }
if (-not $Tsv) { $Tsv = Join-Path $repoRoot 'docs\perf-pareto.tsv' }

foreach ($kind in $Corpora) {
  $src = Join-Path $BenchData "$kind-single\all.bin"
  if (-not (Test-Path -LiteralPath $src)) {
    throw "baseline corpus missing: $src -- run tests/perf/bench.ps1 first (this sweep reuses its bytes)"
  }
}

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $WorkDir 'out') -Force | Out-Null
$want = $MiB * 1MB

# Trim = copy the first $want bytes of a corpus. Same generator, same prefix, so the
# rows stay comparable with docs/perf-results.tsv instead of becoming a fourth data kind.
function Get-Trimmed([string]$Kind) {
  $source = Join-Path $BenchData "$Kind-single\all.bin"
  $target = Join-Path $WorkDir "$Kind-$MiB.bin"
  if (-not (Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).Length -ne $want) {
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    $in = [IO.File]::OpenRead($source)
    $out = [IO.File]::Create($target)
    try {
      $buffer = New-Object byte[] (1MB)
      $left = $want
      while ($left -gt 0) {
        $chunk = [int][Math]::Min($buffer.Length, $left)
        $read = $in.Read($buffer, 0, $chunk)
        if ($read -le 0) { throw "short read from $source" }
        $out.Write($buffer, 0, $read)
        $left -= $read
      }
    } finally { $out.Dispose(); $in.Dispose() }
  }
  return $target
}

Write-Output '== machine / input =='
try {
  $cpu = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
  Write-Output "  cpu=$($cpu.Name) cores=$($cpu.NumberOfCores)/$($cpu.NumberOfLogicalProcessors)"
} catch { Write-Output '  cpu=unknown' }
Write-Output "  azcore=$Azcore"
Write-Output "  each cell = $MiB MiB, ONE entry, $Repeat run(s), median"

$rows = New-Object System.Collections.Generic.List[object]
$tsvHeader = 'sweep	corpus	format	method	level	dict_mib	extra	seconds	mb_per_s	out_bytes	ratio	note'
if (-not $Append) { Set-Content -LiteralPath $Tsv -Value $tsvHeader -Encoding ASCII }

function Add-TsvRow([object]$Row) {
  $line = (@($Row.Sweep, $Row.Corpus, $Row.Format, $Row.Method, $Row.Level, $Row.DictMiB,
    $Row.Extra, $Row.Seconds, $Row.MBps, $Row.OutBytes, $Row.Ratio, ($Row.Note -replace "`t", ' ')) -join "`t")
  Add-Content -LiteralPath $Tsv -Value $line -Encoding ASCII
}

function Invoke-Cell {
  param([string]$Sweep, [string]$Corpus, [string]$Format, [string]$Method, [int]$Level,
        [int]$DictMiB = 0, [string]$Extra = '')

  $input1 = Get-Trimmed $Corpus
  $archive = Join-Path $WorkDir ('out\' + $Sweep + '-' + $Corpus + '-' + $Level + '-' + $DictMiB + '.bin')
  $arguments = @('create', $archive, '-f', $Format, '-m', $Method, '-x', "$Level")
  if ($DictMiB -gt 0) { $arguments += @('-d', "$DictMiB") }
  if ($Extra) { foreach ($pair in ($Extra -split ';')) { $arguments += @('-e', $pair) } }
  $arguments += $input1

  $seconds = @()
  $outBytes = 0L
  $note = ''
  $code = 0
  for ($run = 1; $run -le $Repeat; $run++) {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    # Windows PowerShell 5.1 turns anything a native program writes to stderr into an error
    # record, which under Stop aborts the run; relax it just for the call and keep the text.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $captured = & $Azcore @arguments 2>&1 | Out-String
    $stopwatch.Stop()
    $ErrorActionPreference = $previousPreference
    $code = $LASTEXITCODE
    if ($code -ne 0) {
      $note = (@($captured -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 1) -join ' ')
      break
    }
    $seconds += [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
    $outBytes = (Get-Item -LiteralPath $archive).Length
    Remove-Item -LiteralPath $archive -Force
  }

  $median = 0.0
  if (@($seconds).Count -gt 0) {
    $sorted = @($seconds | Sort-Object)
    $median = if ($sorted.Count % 2) { $sorted[[int][Math]::Floor($sorted.Count / 2)] }
              else { ($sorted[[int][Math]::Floor($sorted.Count / 2) - 1] + $sorted[[int][Math]::Floor($sorted.Count / 2)]) / 2 }
  }
  $mbps = if ($code -eq 0 -and $median -gt 0) { [Math]::Round(($want / 1MB) / $median, 1) } else { 0 }
  $ratio = if ($code -eq 0 -and $outBytes -gt 0) { [Math]::Round($outBytes / $want, 5) } else { 0 }

  $row = [pscustomobject]@{
    Sweep = $Sweep; Corpus = $Corpus; Format = $Format; Method = $Method; Level = $Level
    DictMiB = $DictMiB; Extra = $Extra; Seconds = $median; MBps = $mbps
    OutBytes = $outBytes; Ratio = $ratio; Note = $note
  }
  $rows.Add($row) | Out-Null
  Add-TsvRow $row

  $state = if ($code -eq 0) { '{0,7:N1} MB/s  ratio {1,7:N5}  out {2}' -f $mbps, $ratio, $outBytes }
           else { 'FAILED exit {0}' -f $code }
  Write-Output ('  {0,-5} {1,-6} l{2} d{3,-4} {4,-12} {5}' -f $Corpus, $Format, $Level, $DictMiB, $Extra, $state)
  if ($note) { Write-Output "      $note" }
}

if (-not $SkipLevels) {
  if (-not $SkipZip) {
    Write-Output '== sweep A (P4): zip/Deflate across every engine level, single entry =='
    foreach ($corpus in $Corpora) {
      foreach ($level in $Levels) {
        Invoke-Cell -Sweep 'levels' -Corpus $corpus -Format zip -Method Deflate -Level $level
      }
    }
  }
  Write-Output '== sweep A2: the 7z ladder on the same bytes (what the tiers cost in the other container) =='
  foreach ($corpus in $Corpora) {
    foreach ($level in $Levels7z) {
      Invoke-Cell -Sweep 'levels7z' -Corpus $corpus -Format 7z -Method LZMA2 -Level $level
    }
  }
  if (-not $SkipStore) {
    Write-Output '== sweep A3: the cell the UI really sends for the store tier (method Copy, level ignored) =='
    # zip/-m Deflate/-x 0 is reachable from azcore but not from the shell: CompressionLevelOption
    # tier 0 pairs with method Copy (NativeEngine.InferMethod), so this row -- not a Deflate
    # level-0 row -- is what the shipped "store" tier costs.
    foreach ($corpus in $Corpora) {
      Invoke-Cell -Sweep 'store' -Corpus $corpus -Format zip -Method Copy -Level 0
    }
  }
}

if (-not $SkipDicts) {
  Write-Output '== sweep B (P5): 7z/LZMA2 dictionary sizes, single entry =='
  foreach ($corpus in $Corpora) {
    foreach ($level in $DictLevels) {
      foreach ($dict in $Dicts) {
        Invoke-Cell -Sweep 'dict' -Corpus $corpus -Format 7z -Method LZMA2 -Level $level -DictMiB $dict
      }
    }
  }
  if (-not $SkipRepeat) {
    Write-Output '== sweep B2: two dict cells again, as a noise scale (no -t here -- see the note below) =='
    # Fixed to mixed/l5 on purpose: this pair is the noise yardstick the other rows are
    # compared against, so it must stay the same cell no matter which corpora were asked for.
    foreach ($dict in @(16, 256)) {
      Invoke-Cell -Sweep 'dict-repeat' -Corpus 'mixed' -Format 7z -Method LZMA2 -Level 5 -DictMiB $dict
    }
  }
}

Write-Output "== written =="
Write-Output "  $Tsv ($($rows.Count) rows, appended as they were measured)"

$ok = @($rows | Where-Object { $_.MBps -gt 0 })
Write-Output '== best ratio per MB/s spent (the pareto reading) =='
foreach ($corpus in $Corpora) {
  $cells = @($ok | Where-Object { $_.Sweep -eq 'levels' -and $_.Corpus -eq $corpus })
  if ($cells.Count -eq 0) { continue }
  $best = $cells | Sort-Object -Property { $_.Ratio * [Math]::Log($_.MBps + 1) } | Select-Object -First 1
  Write-Output ('  {0,-6} lowest ratio*ln(MB/s): level {1}  {2} MB/s  ratio {3}' -f `
    $corpus, $best.Level, $best.MBps, $best.Ratio)
}

Write-Output '== what this does NOT measure =='
Write-Output '  - multi-entry runs: every row is one entry, so entry-parallelism is held out'
Write-Output '  - levels above 9 (the engine has none) and the extra coders at other levels'
Write-Output '  - peak memory per dictionary, so the P5 rows say nothing about the OOM risk'
Write-Output '  - 7z with the extra coders (ZSTD/LZ4/...), solid=off, and -t interaction with dict'
Write-Output '  - decompression speed and the compatibility of anything written here'
