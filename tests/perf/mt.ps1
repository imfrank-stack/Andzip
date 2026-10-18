<#
.SYNOPSIS
  How many cores does each 7z method actually use, and what moves it?

.DESCRIPTION
  Stage MT-1 of the "7z does not fill the CPU" report. Five sweeps, all through azcore so the
  numbers are the product's own path (the cli one is the vendor's own front door, on purpose):

    mtscale    one method x {mt=1, mt=auto} x 128 MiB single entry, for every method the 7z
               dropdown offers. This is the survey: which coders scale at all, and which are
               single-threaded by construction (PPMd, Deflate) rather than by a missing knob.
    lzma2bs    LZMA2 x block size ("c" / NCoderPropID::kBlockSize) at the three tiers the UI
               can reach. This is the lever: Lzma2EncProps_Normalize (C/Lzma2Enc.c:298-331)
               takes the number of parallel block threads down to fileSize / blockSize, and
               AUTO blockSize is clamp(dictSize << 2, 1 MiB, 256 MiB) -- so one 128 MiB file
               with a 32 MiB dictionary is exactly ONE block, whatever mt says.
    corpusbs   the same block ladder on the text and rand corpora at one tier, because the
               ratio penalty of chopping a stream is corpus-dependent: text has repeats longer
               than any block, mixed does not. Read together with lzma2bs this is what says how
               far down the block size may go before the dictionary stops paying for itself.
    entry      the same 512 MiB of bytes as one file, as 16 solid files and as 16 non-solid
               files, each across the whole block ladder. This is the shape the user actually
               has -- a folder, not one file -- and the size where AUTO already splits (four
               blocks), so the ceiling of the policy is measured here rather than assumed.
    cli        LZMA2 through the installed 7-Zip's own CLI, same corpus, -mmt=1/4/on. It is
               the control on "is this our plumbing?": the vendor's command line lands on the
               same plateau and the same bytes. Skipped when 7z.exe is not installed.
    mt1        (-SingleThreadControl) the same cells with mt=1 -- the machine that cannot
               parallelise, which is what makes TierExtras decline the block size there.

  Every cell reports wall seconds, CPU seconds, cores_used (= cpu/wall, the number the report
  is about), the peak OS thread count, the peak working set and the archive size. Two runs per
  cell, the faster one wins, same rule as tests/perf/tiers.ps1.

  Result table: docs/perf-mt.tsv. Nothing is written outside %TEMP% and the TSV.

.EXAMPLE
  powershell -File tests/perf/mt.ps1
  powershell -File tests/perf/mt.ps1 -SkipLzma2bs
  powershell -File tests/perf/mt.ps1 -MiB 512
  powershell -File tests/perf/mt.ps1 -SkipMtscale -SkipLzma2bs -Append
  powershell -File tests/perf/mt.ps1 -SkipMtscale -SkipLzma2bs -SkipEntry -SkipCli `
    -BlockSizes auto,16m,8m,4m,2m -Append          # just the corpusbs ladder

.NOTES
  Keep this file pure ASCII (every .ps1 in this repo does).
#>
[CmdletBinding()]
param(
  [string]$Azcore = '',
  [string]$WorkDir = '',
  [string]$Tsv = '',
  [long]$MiB = 128,
  [string[]]$Methods = @('LZMA2', 'LZMA', 'PPMd', 'BZip2', 'Deflate', 'ZSTD', 'LZ4', 'BROTLI'),
  [string[]]$BlockSizes = @('auto', '64m', '32m', '16m', '8m', '4m', '2m'),
  [int[]]$BlockLevels = @(1, 5, 9),
  [int]$Runs = 2,
  [long]$EntryMiB = 512,
  [string[]]$EntryBlocks = @('auto', '16m', '8m', '4m', '2m'),
  [string]$SevenZip = 'C:\Program Files\7-Zip\7z.exe',
  [string]$PartsDir = '',
  [switch]$Append,
  [switch]$SkipMtscale,
  [switch]$SkipLzma2bs,
  [switch]$SkipCorpusBs,
  [switch]$SkipEntry,
  [switch]$SkipCli,
  [switch]$SingleThreadControl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
# "powershell -File" hands a comma list over as ONE string (it does not split like -Command
# does), so a list given that way is split here rather than silently becoming one method name.
if ($Methods.Count -eq 1 -and $Methods[0].Contains(',')) { $Methods = $Methods[0].Split(',') }
if ($BlockSizes.Count -eq 1 -and $BlockSizes[0].Contains(',')) { $BlockSizes = $BlockSizes[0].Split(',') }
if ($EntryBlocks.Count -eq 1 -and $EntryBlocks[0].Contains(',')) { $EntryBlocks = $EntryBlocks[0].Split(',') }
if ($BlockLevels.Count -eq 1 -and $BlockLevels[0].ToString().Contains(',')) {
  $BlockLevels = $BlockLevels[0].ToString().Split(',') | ForEach-Object { [int]$_ }
}

if (-not $Azcore)  { $Azcore  = Join-Path $repoRoot 'build\native\bin\azcore.exe' }
if (-not (Test-Path -LiteralPath $Azcore)) { throw "azcore not found: $Azcore (build the engine first)" }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'andzip-perf-mt' }
if (-not $Tsv)     { $Tsv     = Join-Path $repoRoot 'docs\perf-mt.tsv' }

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null

# The same inputs the other perf scripts measure on, cut to an exact prefix length so -MiB
# means what it says. Sources, in order of preference: the tiers corpus at that size (produced
# by tests/perf/tiers.ps1), then the bench corpora (tests/perf/bench.ps1). The trimmed copy
# lives in this script's own work directory.
function Get-Corpus {
  param([string]$Name, [long]$SizeMiB = $MiB)
  $want = $SizeMiB * 1MB
  $target = Join-Path $WorkDir ("{0}-{1}.bin" -f $Name, $SizeMiB)
  if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -eq $want) { return $target }
  $sources = @(
    (Join-Path $env:TEMP ("andzip-perf-bench\data\{0}-single\all.bin" -f $Name)),
    (Join-Path $env:TEMP ("andzip-perf-tiers\{0}-{1}.bin" -f $Name, $SizeMiB)),
    (Join-Path $env:TEMP ("andzip-perf-tiers\{0}-128.bin" -f $Name)),
    (Join-Path $env:TEMP ("andzip-perf-pareto\{0}-128.bin" -f $Name))
  )
  $src = $sources | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
  if (-not $src) { throw "no '$Name' corpus to trim from (looked for: $($sources -join '; ')) -- run tests/perf/bench.ps1" }
  if ((Get-Item -LiteralPath $src).Length -lt $want) {
    throw "$src is shorter than $SizeMiB MiB; raise nothing or run tests/perf/bench.ps1 first"
  }
  $in = [IO.File]::OpenRead($src); $out = [IO.File]::Create($target)
  try {
    $buf = New-Object byte[] (1MB); $left = $want
    while ($left -gt 0) {
      $n = [int][Math]::Min($buf.Length, $left)
      $r = $in.Read($buf, 0, $n)
      if ($r -le 0) { throw "short read from $src" }
      $out.Write($buf, 0, $r); $left -= $r
    }
  } finally { $out.Dispose(); $in.Dispose() }
  return $target
}

$header = @(
  'sweep', 'corpus', 'mib', 'format', 'method', 'level', 'props',
  'exit', 'seconds', 'cpu_seconds', 'cores_used', 'mb_per_s', 'out_bytes', 'ratio',
  'max_threads', 'peak_mib', 'note'
) -join "`t"
if (-not $Append) { Set-Content -LiteralPath $Tsv -Value $header -Encoding ASCII }

function Measure-Cell {
  param(
    [string]$Sweep, [string]$Method, [int]$Level, [string[]]$PropArgs,
    [string]$PropsLabel, [string[]]$InputPaths, [string]$Note = '', [string]$Corpus = '',
    [switch]$VendorCli
  )
  $archive = Join-Path $WorkDir ("{0}-{1}-{2}{3}.7z" -f $Sweep, $Method, $Level, ($PropsLabel -replace '[^A-Za-z0-9]', ''))
  $bestSec = [double]::MaxValue; $bestCpu = 0.0; $bestThreads = 0; $bestPeak = 0.0; $size = 0
  # azcore speaks the product's own option names (-t for threads, -e for a raw coder
  # property); the vendor CLI speaks -mmt and -mx. Same engine, two front doors -- which is
  # the point of the cli sweep.
  $cmdLine = if ($VendorCli) {
    @('a', '-t7z', "-mx$Level", "-mmt=$($PropArgs[0])", $archive) + $InputPaths
  } else {
    @('create', $archive, '-f', '7z', '-m', $Method, '-x', "$Level") + $PropArgs + $InputPaths
  }
  $exe = if ($VendorCli) { $SevenZip } else { $Azcore }
  $inBytes = @($InputPaths | ForEach-Object { (Get-Item -LiteralPath $_).Length } | Measure-Object -Sum).Sum
  for ($i = 0; $i -lt $Runs; $i++) {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    foreach ($a in $cmdLine) { $psi.Arguments += ('"{0}" ' -f $a) }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    $threads = 0; $peak = 0.0; $cpu0 = [TimeSpan]::Zero; $cpu1 = [TimeSpan]::Zero
    try { $cpu0 = $p.TotalProcessorTime } catch { }
    while (-not $p.WaitForExit(100)) {
      try {
        $p.Refresh()
        if ($p.Threads.Count -gt $threads) { $threads = $p.Threads.Count }
        $ws = $p.WorkingSet64 / 1MB
        if ($ws -gt $peak) { $peak = $ws }
        $cpu1 = $p.TotalProcessorTime
      } catch { break }
    }
    $sw.Stop()
    $wall = $sw.Elapsed.TotalSeconds
    $cpu = [Math]::Max(($cpu1 - $cpu0).TotalSeconds, $wall)
    $code = $p.ExitCode
    $p.Dispose()
    $size = 0
    if (Test-Path -LiteralPath $archive) { $size = (Get-Item -LiteralPath $archive).Length }
    if ($code -ne 0) { break }        # a refused cell is a refused cell; running it twice adds nothing
    if ($wall -lt $bestSec) { $bestSec = $wall; $bestCpu = $cpu; $bestThreads = $threads; $bestPeak = $peak }
  }
  $ratio = if ($inBytes) { [Math]::Round($size / $inBytes, 5) } else { 0 }
  $cores = if ($bestSec -gt 0) { $bestCpu / $bestSec } else { 0 }
  $methodCell = if ($VendorCli) { 'cli' } else { $Method }
  # The corpus column is the file name the rows were measured on unless the sweep says
  # otherwise (the multi-file cells have no single file to name).
  $corpusCell = if ($Corpus) { $Corpus } else { Split-Path -Leaf $InputPaths[0] }
  $row = @(
    $Sweep, $corpusCell, ($inBytes / 1MB), '7z', $methodCell, $Level,
    $PropsLabel, $code, ([Math]::Round($bestSec, 2)), ([Math]::Round($bestCpu, 1)),
    ([Math]::Round($cores, 2)), ([Math]::Round(($inBytes / 1MB) / [Math]::Max($bestSec, 0.01), 1)),
    $size, $ratio, $bestThreads, ([Math]::Round($bestPeak, 0)), $Note
  ) -join "`t"
  Add-Content -LiteralPath $Tsv -Value $row -Encoding ASCII
  Write-Output ('  {0,-10} {1,-8} x{2,-2} {3,-16} {4,7:N2}s {5,6:N2} cores {6,7:N0} MiB  out {7}' -f `
    $Sweep, $Method, $Level, $PropsLabel, $bestSec, $cores, $bestPeak, $size)
}

if (-not $SkipMtscale) {
  $single = Get-Corpus 'mixed'
  $cores = [Environment]::ProcessorCount
  Write-Output "mtscale: one thread vs all $cores, one $([Math]::Round((Get-Item $single).Length / 1MB)) MiB entry"
  foreach ($m in $Methods) {
    Measure-Cell -Sweep 'mtscale' -Method $m -Level 5 -PropArgs @('-t', '1') -PropsLabel 'mt=1' -InputPaths $single
    Measure-Cell -Sweep 'mtscale' -Method $m -Level 5 -PropArgs @('-t', "$cores") -PropsLabel "mt=$cores" -InputPaths $single
  }
}

if (-not $SkipLzma2bs) {
  $single = Get-Corpus 'mixed'
  $cores = [Environment]::ProcessorCount
  Write-Output "lzma2bs: LZMA2 x block size, $cores logical processors"
  foreach ($lvl in $BlockLevels) {
    foreach ($bs in $BlockSizes) {
      $props = @('-t', "$cores")
      $label = "mt=$cores"
      if ($bs -ne 'auto') { $props += @('-e', "c=$bs"); $label += " c=$bs" }
      Measure-Cell -Sweep 'lzma2bs' -Method 'LZMA2' -Level $lvl -PropArgs $props -PropsLabel $label -InputPaths $single
    }
  }
  # The same rule against the other two corpora, so the size/ratio penalty is not read off
  # one kind of input only.
  foreach ($name in @('text', 'rand')) {
    try { $path = Get-Corpus $name } catch { Write-Warning "no $name corpus: $_"; continue }
    foreach ($bs in @('auto', '16m', '4m')) {
      $props = @('-t', "$cores")
      $label = "mt=$cores"
      if ($bs -ne 'auto') { $props += @('-e', "c=$bs"); $label += " c=$bs" }
      Measure-Cell -Sweep 'lzma2bs' -Method 'LZMA2' -Level 5 -PropArgs $props -PropsLabel $label -InputPaths $path
    }
  }
}

if (-not $SkipCorpusBs) {
  # The same block ladder on the other two corpora at one tier. The ratio penalty of chopping
  # the stream is corpus-dependent -- text has repeats longer than any block, mixed does not --
  # and the block the product picks has to be chosen against that, not only against 'mixed'.
  $cores = [Environment]::ProcessorCount
  Write-Output "corpusbs: block ladder on text and rand at x5, $cores threads"
  foreach ($name in @('text', 'rand')) {
    try { $path = Get-Corpus $name } catch { Write-Warning "no $name corpus: $_"; continue }
    foreach ($bs in $BlockSizes) {
      $props = @('-t', "$cores")
      $label = "mt=$cores"
      if ($bs -ne 'auto') { $props += @('-e', "c=$bs"); $label += " c=$bs" }
      Measure-Cell -Sweep 'corpusbs' -Method 'LZMA2' -Level 5 -PropArgs $props -PropsLabel $label -InputPaths $path
    }
  }
}

if (-not $SkipEntry) {
  # The block ladder at a size where AUTO already splits (512 MiB / 128 MiB blocks = 4 blocks),
  # because that is where the formula's ceiling would be used -- and the same total as one file
  # vs as a folder, because the format hint tells users a folder is the good shape for 7z.
  # Everything here runs in ONE invocation: the same cell measured twice across invocations
  # moves by 15% on this box (12.67 s vs 14.59 s for 512 MiB / auto), so only within one run
  # are two rows comparable.
  $cores = [Environment]::ProcessorCount
  if (-not $PartsDir) { $PartsDir = Join-Path $env:TEMP 'andzip-perf-bench\data\mixed-parts' }
  $parts = @(Get-ChildItem -LiteralPath $PartsDir -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
  Write-Output "entry: $EntryMiB MiB total, $cores threads, block ladder $($EntryBlocks -join ' ')"
  $shapes = @()
  try { $shapes += ,@('', (Get-Corpus 'mixed' -SizeMiB $EntryMiB), $null) }
  catch { Write-Warning "no $EntryMiB MiB single corpus: $_" }
  if ($parts.Count -ge 2) {
    $total = [Math]::Round(($parts | ForEach-Object { (Get-Item -LiteralPath $_).Length } | Measure-Object -Sum).Sum / 1MB)
    $shapes += ,@("mixed-parts$total", $parts, '--solid=on')
    $shapes += ,@("mixed-parts$total", $parts, '--solid=off')
  } else {
    Write-Warning "no multi-file corpus in $PartsDir -- the solid/non-solid cells were not run"
  }
  foreach ($shape in $shapes) {
    $name = $shape[0]; $paths = @($shape[1]); $solid = $shape[2]
    # the two non-solid shapes are controls only -- the product always sends solid=on
    $blocks = if ($solid -eq '--solid=off') { @('auto', '16m') } else { $EntryBlocks }
    foreach ($bs in $blocks) {
      $props = @('-t', "$cores")
      $label = "mt=$cores"
      if ($solid) { $props += $solid; $label += " $solid" }
      if ($bs -ne 'auto') { $props += @('-e', "c=$bs"); $label += " c=$bs" }
      Measure-Cell -Sweep 'entry' -Method 'LZMA2' -Level 5 -PropArgs $props `
        -PropsLabel $label -InputPaths $paths -Corpus $name
    }
  }
}

if (-not $SkipCli) {
  # The control on "is this our plumbing". Same engine, the vendor's own front door.
  if (-not (Test-Path -LiteralPath $SevenZip)) {
    Write-Warning "7-Zip CLI not at $SevenZip -- the cli sweep was skipped"
  } else {
    $single = Get-Corpus 'mixed'
    Write-Output "cli: the installed 7-Zip CLI on the same corpus ($SevenZip)"
    foreach ($spec in @('1', '4', 'on')) {
      Measure-Cell -Sweep 'cli' -Method 'LZMA2' -Level 5 -PropArgs @($spec) `
        -PropsLabel "-mmt=$spec" -InputPaths $single -VendorCli
    }
  }
}

if ($SingleThreadControl) {
  # The other half of the policy: on a machine that cannot parallelise, the block size
  # would only buy a bigger package (the dictionary is cut down to the block), so
  # TierExtras declines to send it. These rows are that claim's witness.
  $single = Get-Corpus 'mixed'
  Write-Output 'single-thread control: same cells with mt=1'
  foreach ($lvl in @(3, 5)) {
    Measure-Cell -Sweep 'mt1' -Method 'LZMA2' -Level $lvl -PropArgs @('-t', '1') -PropsLabel 'mt=1' -InputPaths $single
    Measure-Cell -Sweep 'mt1' -Method 'LZMA2' -Level $lvl -PropArgs @('-t', '1', '-e', 'c=4m') -PropsLabel 'mt=1 c=4m' -InputPaths $single
  }
}

Write-Output "  tsv: $Tsv"
