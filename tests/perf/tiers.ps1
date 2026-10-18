<#
.SYNOPSIS
  What the shipped tiers would actually send, after the P4/P5 proposal tables.

.DESCRIPTION
  P4/P5 (docs/perf-p4-p5.md) stopped at "here are the tables, tell us what to change".
  This script measures the *candidate configurations themselves* -- the exact command a
  tier would send through Core -- so a tier change is decided on numbers rather than on
  how the level ladder is expected to behave:

    A  acceptance: does "-e a=0" reach every method ZIP offers? A property the coder does
       not know is rejected as a whole request (E_INVALIDARG, nothing written), so this
       must be measured per method before any tier can hand out a=0. Small input on
       purpose: the answer is yes/no, and 128 MiB would only buy that answer slower.
    B  the ZIP ladder at the levels the tiers really use (1 = UltraFast, 5 = Normal,
       7 and 9 = the two Max candidates), with and without a=0.
    C  7z/LZMA2 Max as it ships (level 9, engine default dictionary) against the same
       level with an explicit dictionary, and against level 5/6 -- the proposal said
       "64 MiB + level 5-6", which would make Max equal Normal, so the column that
       decides is the memory one, not the ratio one.

    D  a fixed dictionary against the user's global memory limit (memuse) -- the shipped
       path is "explicit dict + possible limit", so the pair is measured, not assumed.

  peak_mib is what makes C usable: PeakWorkingSet64 of the engine process is sampled
  while it runs, so "a 128 MiB dictionary is a memory question, not a speed question"
  is backed by a measured number instead of an inference.

  Rows are appended to the TSV as they are measured, so a stopped job keeps what it got.

  Keep this file pure ASCII (every .ps1 in this repo does).
#>
param(
  [string]$Azcore = '',
  [string]$BenchData = '',
  [string]$WorkDir = '',
  [string]$Tsv = '',
  [string[]]$Corpora = @('text', 'mixed'),
  [string[]]$AcceptMethods = @('Deflate', 'Deflate64', 'LZMA', 'BZip2', 'PPMd', 'Copy'),
  [string[]]$ZipMethods = @('Deflate', 'Deflate64'),
  [int[]]$ZipLevels = @(1, 5, 7, 9),
  [int]$AcceptMiB = 8,
  [long]$MiB = 128,
  [int[]]$Dicts = @(32, 64, 128),
  [int]$MemLimitMib = 256,
  [switch]$Append,
  [switch]$SkipAccept,
  [switch]$SkipZip,
  [switch]$Skip7z,
  [switch]$SkipMem
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Azcore) { $Azcore = Join-Path $repoRoot 'build\native\bin\azcore.exe' }
if (-not (Test-Path -LiteralPath $Azcore)) { throw "azcore not found: $Azcore (build the engine first)" }
if (-not $BenchData) { $BenchData = Join-Path $env:TEMP 'andzip-perf-bench\data' }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'andzip-perf-tiers' }
if (-not $Tsv) { $Tsv = Join-Path $repoRoot 'docs\perf-tiers.tsv' }

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $WorkDir 'out') -Force | Out-Null

# The corpora are the baseline's own bytes, trimmed: same prefix, so these rows stay
# comparable with docs/perf-pareto.tsv instead of becoming a fourth kind of input.
function Get-Trimmed([string]$Kind, [long]$SizeMiB) {
  $source = Join-Path $BenchData "$Kind-single\all.bin"
  if (-not (Test-Path -LiteralPath $source)) {
    throw "baseline corpus missing: $source -- run tests/perf/bench.ps1 first"
  }
  $want = $SizeMiB * 1MB
  $have = (Get-Item -LiteralPath $source).Length
  if ($have -lt $want) { throw "corpus $source is only $have bytes, cannot trim to $want" }
  $target = Join-Path $WorkDir "$Kind-$SizeMiB.bin"
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

$rows = New-Object System.Collections.Generic.List[object]
$tsvHeader = "sweep`tcorpus`tmib`tformat`tmethod`tlevel`tprops`tdict_mib`texit`tseconds`tmb_per_s`tout_bytes`tratio`tpeak_mib`tnote"
if (-not $Append) { Set-Content -LiteralPath $Tsv -Value $tsvHeader -Encoding ASCII }

function Add-TsvRow([object]$Row) {
  $line = (@($Row.Sweep, $Row.Corpus, $Row.MiB, $Row.Format, $Row.Method, $Row.Level, $Row.Props,
    $Row.DictMiB, $Row.Exit, $Row.Seconds, $Row.MBps, $Row.OutBytes, $Row.Ratio, $Row.PeakMiB,
    ($Row.Note -replace "`t", ' ')) -join "`t")
  Add-Content -LiteralPath $Tsv -Value $line -Encoding ASCII
}

function Invoke-Cell {
  param([string]$Sweep, [string]$Corpus, [long]$SizeMiB, [string]$Format, [string]$Method,
        [int]$Level, [string[]]$Props = @(), [int]$DictMiB = 0)

  $input1 = Get-Trimmed $Corpus $SizeMiB
  $tag = ($Sweep + '-' + $Corpus + '-' + $Method + '-' + $Level + '-' + $DictMiB + '-' +
          (($Props -join '_') -replace '[^A-Za-z0-9._-]', ''))
  $archive = Join-Path $WorkDir ('out\' + $tag + '.bin')
  if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }

  $arguments = @('create', $archive, '-f', $Format, '-m', $Method, '-x', "$Level")
  if ($DictMiB -gt 0) { $arguments += @('-d', "$DictMiB") }
  foreach ($pair in $Props) { $arguments += @('-e', $pair) }
  $arguments += $input1

  # .NET's Process, not Start-Process: the object PowerShell 5.1 hands back from
  # Start-Process -PassThru answers nothing for ExitCode (empty) and for
  # PeakWorkingSet64 (0) once the child is gone, which would silently turn every row into
  # a rejection. This way the handle stays ours: the exit code is real, and the peak is
  # sampled while it runs *and* read once more at the end. The two standard streams are
  # drained as async tasks so a chatty progress line can never fill the pipe and wedge
  # the child -- and the rejected rows keep the engine's own sentence, which is the part
  # that says why a property was refused.
  $previousPreference = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $Azcore
  $psi.Arguments = (($arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' ')
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $stopwatch = [Diagnostics.Stopwatch]::StartNew()
  $proc = [System.Diagnostics.Process]::Start($psi)
  $outTask = $proc.StandardOutput.ReadToEndAsync()
  $errTask = $proc.StandardError.ReadToEndAsync()
  $peakBytes = 0L
  while (-not $proc.WaitForExit(50)) {
    try {
      $proc.Refresh()
      if ($proc.PeakWorkingSet64 -gt $peakBytes) { $peakBytes = [long]$proc.PeakWorkingSet64 }
    } catch { }
  }
  $proc.WaitForExit()
  $stopwatch.Stop()
  $code = $proc.ExitCode
  try {
    $proc.Refresh()
    if ($proc.PeakWorkingSet64 -gt $peakBytes) { $peakBytes = [long]$proc.PeakWorkingSet64 }
  } catch { }
  $proc.Dispose()
  $ErrorActionPreference = $previousPreference
  $peakMib = [Math]::Round($peakBytes / 1MB, 0)
  $captured = @($outTask.Result, $errTask.Result) -join "`n"

  $outBytes = 0L
  if ($code -eq 0 -and (Test-Path -LiteralPath $archive)) {
    $outBytes = (Get-Item -LiteralPath $archive).Length
    Remove-Item -LiteralPath $archive -Force
  }
  $seconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
  # A rejected row has no throughput: the 0.02 s it took to say "no" would otherwise
  # print as several thousand MB/s, which is the kind of number that gets quoted.
  $mbps = if ($code -eq 0 -and $seconds -gt 0) { [Math]::Round($SizeMiB / $seconds, 1) } else { 0 }
  $ratio = if ($code -eq 0 -and $outBytes -gt 0) { [Math]::Round($outBytes / ($SizeMiB * 1MB), 5) } else { 0 }
  $note = ''
  if ($code -ne 0) {
    $note = ((($captured -split "`r?`n") | Where-Object { $_ } | Select-Object -Last 1) -join ' ')
  }

  $row = [pscustomobject]@{
    Sweep = $Sweep; Corpus = $Corpus; MiB = $SizeMiB; Format = $Format; Method = $Method
    Level = $Level; Props = ($Props -join ','); DictMiB = $DictMiB; Exit = $code
    Seconds = $seconds; MBps = $mbps; OutBytes = $outBytes; Ratio = $ratio
    PeakMiB = $peakMib; Note = $note
  }
  $rows.Add($row) | Out-Null
  Add-TsvRow $row

  $state = if ($code -eq 0) { '{0,7:N1} MB/s  ratio {1,7:N5}  out {2,12}  peak {3,6:N0} MiB' -f `
                           $mbps, $ratio, $outBytes, $peakMib }
            else { 'REJECTED (exit {0})  peak {1:N0} MiB' -f $code, $peakMib }
  Write-Output ('  {0,-6} {1,4} MiB {2,-10} l{3} d{4,-4} {5,-8} {6}' -f `
      $Corpus, $SizeMiB, $Method, $Level, $DictMiB, ($Props -join ','), $state)
}

Write-Output '== machine =='
try {
  $cpu = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
  Write-Output "  cpu=$($cpu.Name) cores=$($cpu.NumberOfCores)/$($cpu.NumberOfLogicalProcessors)"
} catch { Write-Output '  cpu=unknown' }
Write-Output "  azcore=$Azcore"

if (-not $SkipAccept) {
  Write-Output "== sweep A: does a=0 reach every method ZIP lists, and does it do anything? ($AcceptMiB MiB, level 9) =="
  # Paired with and without the property: "the engine accepted it" is not the same fact
  # as "the coder used it" -- Copy and PPMd can take the number and ignore it. The pair
  # of out_bytes columns is what tells those two apart.
  foreach ($corpus in $Corpora) {
    foreach ($method in $AcceptMethods) {
      Invoke-Cell -Sweep 'accept' -Corpus $corpus -SizeMiB $AcceptMiB -Format zip -Method $method -Level 9
      Invoke-Cell -Sweep 'accept' -Corpus $corpus -SizeMiB $AcceptMiB -Format zip -Method $method `
        -Level 9 -Props @('a=0')
    }
  }
}

if (-not $SkipZip) {
  Write-Output "== sweep B: the ZIP ladder the tiers really send, with and without a=0 ($MiB MiB) =="
  foreach ($corpus in $Corpora) {
    foreach ($method in $ZipMethods) {
      foreach ($level in $ZipLevels) {
        Invoke-Cell -Sweep 'ziptier' -Corpus $corpus -SizeMiB $MiB -Format zip -Method $method -Level $level
        Invoke-Cell -Sweep 'ziptier' -Corpus $corpus -SizeMiB $MiB -Format zip -Method $method -Level $level `
          -Props @('a=0')
      }
    }
  }
}

if (-not $Skip7z) {
  Write-Output "== sweep C: 7z/LZMA2 Max tier -- the dictionary as a memory question ($MiB MiB) =="
  foreach ($corpus in $Corpora) {
    Invoke-Cell -Sweep '7ztier' -Corpus $corpus -SizeMiB $MiB -Format 7z -Method LZMA2 -Level 9
    foreach ($dict in $Dicts) {
      Invoke-Cell -Sweep '7ztier' -Corpus $corpus -SizeMiB $MiB -Format 7z -Method LZMA2 -Level 9 -DictMiB $dict
    }
    Invoke-Cell -Sweep '7ztier' -Corpus $corpus -SizeMiB $MiB -Format 7z -Method LZMA2 -Level 5
    Invoke-Cell -Sweep '7ztier' -Corpus $corpus -SizeMiB $MiB -Format 7z -Method LZMA2 -Level 6 -DictMiB 64
  }
}

if (-not $SkipMem) {
  Write-Output "== sweep D: does a fixed dictionary survive a user memory limit? (8 MiB, level 9) =="
  # The candidate for the Max tier is "64 MiB dictionary instead of the engine's
  # 128 MiB". A user can also set a limit in the advanced settings page (the global
  # memuse knob), and 7z's thread planner
  # shrinks the dictionary to fit it. The pair has to be measured together, because the
  # shipped path is "explicit dict + possible user limit", and "they probably coexist"
  # is not a result.
  foreach ($corpus in $Corpora) {
    Invoke-Cell -Sweep 'mem' -Corpus $corpus -SizeMiB $AcceptMiB -Format 7z -Method LZMA2 -Level 9
    Invoke-Cell -Sweep 'mem' -Corpus $corpus -SizeMiB $AcceptMiB -Format 7z -Method LZMA2 -Level 9 `
      -Props @("memuse=$MemLimitMib" + 'm')
    Invoke-Cell -Sweep 'mem' -Corpus $corpus -SizeMiB $AcceptMiB -Format 7z -Method LZMA2 -Level 9 -DictMiB 64
    Invoke-Cell -Sweep 'mem' -Corpus $corpus -SizeMiB $AcceptMiB -Format 7z -Method LZMA2 -Level 9 -DictMiB 64 `
      -Props @("memuse=$MemLimitMib" + 'm')
  }
}

Write-Output "== written =="
Write-Output "  $Tsv ($($rows.Count) rows, appended as they were measured)"

Write-Output '== what this does NOT measure =='
Write-Output '  - decompression speed, or the decoder-side memory of a big dictionary'
Write-Output '  - multi-entry runs (every row is one entry; entry parallelism is the baseline)'
Write-Output '  - solid=off, volumes, encryption: a tier change must not touch those, and the'
Write-Output '    suite, not this script, is what checks them'
Write-Output '  - PeakWorkingSet64 covers the azcore process only, not the whole machine view'
