<#
.SYNOPSIS
  Stage P3: what do engine properties bought through extra_properties actually do?

.DESCRIPTION
  No product code is involved and no kernel file is read: azcore's -e flag pushes
  any "name=value" pair straight into the engine's property table
  (az_create_options.extra_properties), which is exactly what 7z's own command line
  does. Two sweeps:

    A  ZIP/Deflate tuning knobs: fb (match bits), mc (match finder cycles),
       pass. Level 3 and level 9, looking for a point that is faster without
       becoming worse -- not for a "best setting", just the numbers.
    B  7z/ZSTD thread knobs: the upstream SetZstdThreads block in ZipUpdate.cpp is
       commented out, so nbWorkers is never assigned automatically. We ask whether
       pushing -t / nbWorkers explicitly lets 7z+ZSTD use more than one core.

  A rejected property IS a result: the row records the exit code and the last lines
  of output instead of throwing, so the table shows which names the handler does not
  know (unknown coder property => the engine returns E_INVALIDARG and nothing is
  written; that is reported, not papered over).

  Input comes from the corpora tests/perf/bench.ps1 already generated (deterministic,
  SHA-256 printed there). This script only trims a prefix of that file, so the sweep
  runs on the same bytes as the baseline, just shorter -- level 9 Deflate on 512 MiB
  single-entry measured 380-400 s per run, and a sweep cannot afford that.

  Keep this file pure ASCII (every .ps1 in this repo does).
#>
param(
  [string]$Azcore = '',
  [string]$BenchData = '',
  [string]$WorkDir = '',
  [long]$MiB = 128,
  [int[]]$Levels = @(3, 9),
  [int]$Repeat = 1,
  [string]$Tsv = '',
  [switch]$Append,
  [switch]$SkipDeflate,
  [switch]$SkipZstd
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Azcore) { $Azcore = Join-Path $repoRoot 'build\native\bin\azcore.exe' }
if (-not (Test-Path -LiteralPath $Azcore)) { throw "azcore not found: $Azcore (build the engine first)" }
if (-not $BenchData) { $BenchData = Join-Path $env:TEMP 'andzip-perf-bench\data' }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'andzip-perf-props' }
if (-not $Tsv) { $Tsv = Join-Path $repoRoot 'docs\perf-props.tsv' }

$source = Join-Path $BenchData 'text-single\all.bin'
if (-not (Test-Path -LiteralPath $source)) {
  throw "baseline corpus not found: $source -- run tests/perf/bench.ps1 first (this sweep reuses its bytes)"
}

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $WorkDir 'out') -Force | Out-Null
$target = Join-Path $WorkDir 'sweep.bin'
$want = $MiB * 1MB
$have = (Get-Item -LiteralPath $source).Length
if ($have -lt $want) { throw "baseline corpus is only $have bytes, cannot trim to $want" }

# Trim = copy the first $want bytes. Same generator, same prefix, so this stays
# comparable with the baseline rows instead of becoming a fourth data kind.
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

Write-Output "== input =="
Write-Output "  source=$source"
Write-Output "  sweep=$target bytes=$want"
Write-Output ("  sha256={0}" -f (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash)

# Rows land in the TSV as they are measured, not in one write at the end. The sweep
# is long (level 9 is ~95 s a cell, and pass=5 multiplies that), and a level-9 cell
# that dies of an OOM or a stopped job must not take the eleven measurements that
# already finished down with it.
$rows = New-Object System.Collections.Generic.List[object]
$tsvHeader = 'cell	format	method	level	props	exit_code	seconds	mb_per_s	ratio	out_bytes	note'
# -Append exists because the level-9 sweep is ~15 minutes on its own: a run that has
# to redo the level-3 cells to add one row will not get run twice.
if (-not $Append) { Set-Content -LiteralPath $Tsv -Value $tsvHeader -Encoding ASCII }

function Add-TsvRow([object]$Row) {
  $line = (@($Row.Cell, $Row.Format, $Row.Method, $Row.Level, $Row.Props, $Row.ExitCode,
    $Row.Seconds, $Row.MBps, $Row.Ratio, $Row.OutBytes, ($Row.Note -replace "`t", ' ')) -join "`t")
  Add-Content -LiteralPath $Tsv -Value $line -Encoding ASCII
}

function Invoke-Sweep {
  param(
    [string]$Label, [string]$Format, [string]$Method, [int]$Level,
    [string[]]$Extra = @(), [int]$Threads = 0)

  $archive = Join-Path $WorkDir ('out\' + ($Label -replace '[^A-Za-z0-9._-]', '_') + '.bin')
  if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }

  $arguments = @('create', $archive, '-f', $Format, '-m', $Method, '-x', "$Level")
  if ($Threads -gt 0) { $arguments += @('-t', "$Threads") }
  foreach ($pair in $Extra) { $arguments += @('-e', $pair) }
  $arguments += $target

  $previousPreference = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $stopwatch = [Diagnostics.Stopwatch]::StartNew()
  $captured = & $Azcore @arguments 2>&1 | Out-String
  $stopwatch.Stop()
  $ErrorActionPreference = $previousPreference
  $code = $LASTEXITCODE

  $outBytes = 0L
  if ($code -eq 0 -and (Test-Path -LiteralPath $archive)) {
    $outBytes = (Get-Item -LiteralPath $archive).Length
    Remove-Item -LiteralPath $archive -Force
  }

  $note = ''
  if ($code -ne 0) {
    $note = (@($captured -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 2) -join ' ')
  }

  # A rejected row has no throughput: the 0.01 s it took to say "no" would otherwise
  # print as 9 000 MB/s, which is the kind of number that gets quoted out of context.
  $mbps = if ($code -eq 0 -and $stopwatch.Elapsed.TotalSeconds -gt 0) {
    [Math]::Round(($want / 1MB) / $stopwatch.Elapsed.TotalSeconds, 1)
  } else { 0 }

  $rows.Add([pscustomobject]@{
    Cell = $Label; Format = $Format; Method = $Method; Level = $Level
    Props = (($Extra -join ',') + $(if ($Threads -gt 0) { "|mt=$Threads" } else { '' }))
    ExitCode = $code; Seconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
    MBps = $mbps; Ratio = $(if ($outBytes -gt 0 -and $code -eq 0) { [Math]::Round($outBytes / $want, 4) } else { 0 })
    OutBytes = $outBytes
    Note = $note
  }) | Out-Null
  Add-TsvRow $rows[$rows.Count - 1]

  $status = if ($code -eq 0) { '{0} MB/s  ratio {1}' -f $mbps, $rows[$rows.Count - 1].Ratio }
            else { 'FAILED exit {0}' -f $code }
  Write-Output ("  {0,-28} {1}" -f $Label, $status)
  if ($code -ne 0 -and $note) { Write-Output ("      {0}" -f $note) }
}

if (-not $SkipDeflate) {
  Write-Output "== sweep A: ZIP/Deflate knobs (level 3 and 9, $MiB MiB single entry) =="
  foreach ($level in $Levels) {
    Invoke-Sweep -Label "zip-l$level-baseline" -Format zip -Method Deflate -Level $level
    foreach ($knob in @('fb=16', 'fb=64', 'fb=128', 'fb=258', 'mc=16', 'mc=64', 'pass=2', 'pass=5', 'a=0', 'a=1')) {
      Invoke-Sweep -Label "zip-l$level-$($knob -replace '=', '')" -Format zip -Method Deflate -Level $level -Extra @($knob)
    }
    # Two names that are known NOT to reach the deflate coder, kept on purpose so the
    # table says which door is shut: "mf" is a string property the deflate coder does not
    # accept at all, and "algo" is not the engine's name for the algorithm (it is "a").
    Invoke-Sweep -Label "zip-l$level-mf-bt4" -Format zip -Method Deflate -Level $level -Extra @('mf=bt4')
    Invoke-Sweep -Label "zip-l$level-algo-1" -Format zip -Method Deflate -Level $level -Extra @('algo=1')
    Invoke-Sweep -Label "zip-l$level-fb128-mc64" -Format zip -Method Deflate -Level $level -Extra @('fb=128', 'mc=64')
  }

  Write-Output "== control: the same knobs through -t (Deflate has no MT coder) =="
  foreach ($threads in @(1, 4, 16)) {
    Invoke-Sweep -Label "zip-l3-mt$threads" -Format zip -Method Deflate -Level 3 -Threads $threads
  }
}

if (-not $SkipZstd) {
  Write-Output "== sweep B: 7z/ZSTD thread knobs =="
  foreach ($threads in @(0, 1, 2, 4, 8, 16)) {
    Invoke-Sweep -Label "7z-zstd-l3-mt$threads" -Format 7z -Method ZSTD -Level 3 -Threads $threads
  }
  foreach ($workers in @('nbWorkers=2', 'nbWorkers=8')) {
    Invoke-Sweep -Label "7z-zstd-l3-$($workers -replace '=', '')" -Format 7z -Method ZSTD -Level 3 -Extra @($workers)
  }
  Write-Output "== reference: 7z/LZMA2 scaling on the same bytes =="
  foreach ($threads in @(1, 4, 16)) {
    Invoke-Sweep -Label "7z-lzma2-l5-mt$threads" -Format 7z -Method LZMA2 -Level 5 -Threads $threads
  }
}

Write-Output "== written =="
Write-Output "  $Tsv ($($rows.Count) rows, appended as they were measured)"

$ok = @($rows | Where-Object { $_.ExitCode -eq 0 })
if ($ok.Count -gt 0) {
  $fastest = $ok | Sort-Object -Property MBps -Descending | Select-Object -First 1
  Write-Output ("== fastest ok: {0} at {1} MB/s (ratio {2}) ==" -f $fastest.Cell, $fastest.MBps, $fastest.Ratio)
} else {
  Write-Output '== no row succeeded: every property the engine was given was rejected =='
}
$failed = @($rows | Where-Object { $_.ExitCode -ne 0 })
Write-Output ("  rejected rows: {0} of {1}" -f $failed.Count, $rows.Count)

Write-Output "== what this does NOT measure =="
Write-Output "  - decompression speed of anything written here"
Write-Output "  - multi-entry runs: every row is one $MiB MiB entry, so entry-level"
Write-Output '    parallelism (the thing the baseline measures) is held out on purpose'
Write-Output "  - memory: -t and nbWorkers are pushed without watching the working set"
Write-Output "  - solid vs non-solid 7z (that is P5's table, not this one)"
