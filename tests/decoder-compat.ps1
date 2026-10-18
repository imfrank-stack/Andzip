<#
.SYNOPSIS
  Can the archives we write be opened by programs that share no code with us?

.DESCRIPTION
  Stage P2-c replaces the Deflate encoder behind method 0x40108, which both the ZIP entry
  and the 7z member resolve to (docs/perf-p2c-deflate-swap.md). A new encoder that only our
  own decoder can read is not a speed-up, it is a data-loss machine that runs fast, so the
  plan's acceptance gate for this stage is external: open what we wrote with programs that
  carry their own inflate, and compare the bytes.

  Three decoders are tried, and what is not on the machine is reported as absent rather than
  quietly skipped:

    7-Zip        7z.exe t and x. Its decoder is 7-Zip's own NDeflate -- the very code this
                 engine still uses for the decode direction, so it is independent of
                 zlib-ng but NOT of the vendor tree.
    Python       zipfile: its own inflate (CPython links its own zlib copy), and it verifies
                 the member CRC while reading.
    Explorer     the Windows shell's zip folder (System.IO.Compression path). Slow to wait
                 on, so the check polls for the file to appear and then for its size to stop
                 changing; CopyHere is asynchronous and has no exit code.
    WinRAR and Bandizip are in the plan's list and are not driven from here: Bandizip has no
  command line we may call unattended, and WinRAR is not installed on this machine. Both are
  left for the manual half of the gate (docs/perf-p2c-deflate-swap.md).

  Rows go to docs/decoder-compat.tsv (one per archive x decoder) so the claim has a table
  behind it and not just a sentence in a document.

.EXAMPLE
  powershell -File tests/decoder-compat.ps1

.NOTES
  Keep this file pure ASCII (every .ps1 in this repo does).
#>
param(
  [string]$Azcore = '',
  [string]$SevenZip = '',
  [string]$PythonExe = 'python',
  [long]$MiB = 8,
  [string]$WorkDir = '',
  [string]$Tsv = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Azcore) { $Azcore = Join-Path $repoRoot 'build\native\bin\azcore.exe' }
if (-not (Test-Path -LiteralPath $Azcore)) { throw "azcore not found: $Azcore (build the engine first)" }
if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP 'andzip-decoder-compat' }
if (-not $Tsv) { $Tsv = Join-Path $repoRoot 'docs\decoder-compat.tsv' }

function Find-First([string[]]$candidates) {
  foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
  return ''
}

if (-not $SevenZip) {
  $SevenZip = Find-First @('C:\Program Files\7-Zip\7z.exe', 'C:\Program Files (x86)\7-Zip\7z.exe')
}

New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
$data = Join-Path $WorkDir "payload-$MiB.bin"
if (-not (Test-Path -LiteralPath $data) -or (Get-Item -LiteralPath $data).Length -ne ($MiB * 1MB)) {
  Write-Output "  generating the corpus ($MiB MiB)"
  $src = Join-Path $env:TEMP 'andzip-perf-bench\data\mixed-single\all.bin'
  if (-not (Test-Path -LiteralPath $src)) { throw "corpus source missing: $src (run tests/perf/bench.ps1)" }
  $in = [IO.File]::OpenRead($src); $out = [IO.File]::Create($data)
  try {
    $buf = New-Object byte[] (1MB); $left = $MiB * 1MB
    while ($left -gt 0) {
      $n = [int][Math]::Min($buf.Length, $left)
      $r = $in.Read($buf, 0, $n); if ($r -le 0) { throw 'short read' }
      $out.Write($buf, 0, $r); $left -= $r
    }
  } finally { $out.Dispose(); $in.Dispose() }
}
$sourceHash = (Get-FileHash -LiteralPath $data -Algorithm SHA256).Hash

# (label, format, method, level the UI sends)
#
# Deflate64 is deliberately not here: this stage does not touch it (its encoder stays with
# the vendor -- that is the reason the swap edits one table slot instead of using the
# Z7_DEFLATE_EXTRACT_ONLY switch), and half of these decoders cannot read a Deflate64 ZIP
# member at all, which would put "not supported" rows where a real check should be.
$cases = @(
  @{ Label = 'zip-ultrafast'; Format = 'zip'; Method = 'Deflate'; Level = 1 }
  @{ Label = 'zip-normal';    Format = 'zip'; Method = 'Deflate'; Level = 5 }
  @{ Label = 'zip-max';       Format = 'zip'; Method = 'Deflate'; Level = 9 }
  @{ Label = '7z-normal';     Format = '7z';  Method = 'Deflate'; Level = 5 }
  @{ Label = '7z-max';        Format = '7z';  Method = 'Deflate'; Level = 9 }
)

# Which of these can even open which container. A decoder that has no reader for the format
# is reported as such -- it is not a failure, and pretending otherwise would be a green row
# built on nothing. On this machine the 7z side therefore has exactly one independent
# decoder (7-Zip's own); Python's zipfile does not read 7z, the Windows 10 shell does not
# either (libarchive's 7z support is a Windows 11 thing), and WinRAR is not installed.
$canRead = @{
  '7-Zip'    = @('zip', '7z')
  'Python'   = @('zip')
  'Explorer' = @('zip')
}

function New-Archive($case) {
  $ext = if ($case.Format -eq 'zip') { 'zip' } else { '7z' }
  $path = Join-Path $WorkDir ($case.Label + '.' + $ext)
  if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
  & $Azcore create $path -f $case.Format -m $case.Method -x $case.Level $data | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "azcore could not write $path" }
  return $path
}

function Invoke-Decoder([string]$who, [string]$archive, [string]$targetDir) {
  # Each decoder returns the extracted file's hash, or a reason string prefixed with "FAIL".
  $extracted = Join-Path $targetDir ('payload-' + $MiB + '.bin')
  if (Test-Path -LiteralPath $extracted) { Remove-Item -LiteralPath $extracted -Force }
  switch ($who) {
    '7-Zip' {
      & $SevenZip 'x' '-y' ("-o" + $targetDir) $archive | Out-Null
      if ($LASTEXITCODE -ne 0) { return "FAIL 7z exit $LASTEXITCODE" }
    }
    'Python' {
      $py = @"
import hashlib, sys, zipfile
a, dest = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(a) as z:
    bad = z.testzip()
    if bad is not None:
        print('FAIL crc ' + bad); sys.exit(0)
    name = z.namelist()[0]
    with z.open(name) as f, open(dest, 'wb') as w:
        w.write(f.read())
print(hashlib.sha256(open(dest, 'rb').read()).hexdigest())
"@
      $pyFile = Join-Path $WorkDir 'pyzip.py'
      Set-Content -LiteralPath $pyFile -Value $py -Encoding ascii
      $found = & $PythonExe $pyFile $archive $extracted 2>&1
      if ($LASTEXITCODE -ne 0) { return "FAIL python exit $LASTEXITCODE ($found)" }
      return (@($found | Where-Object { $_ }) | Select-Object -Last 1)
    }
    'Explorer' {
      $shell = New-Object -ComObject Shell.Application
      $folder = $shell.NameSpace($archive)
      if (-not $folder) { return 'FAIL shell cannot open the archive' }
      $items = $folder.Items()
      if ($items.Count -lt 1) { return 'FAIL shell sees no members' }
      $dest = $shell.NameSpace($targetDir)
      $dest.CopyHere($items)
      # CopyHere returns immediately: wait for the file, then for its size to stop moving.
      $last = -1; $stable = 0
      for ($i = 0; $i -lt 240; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Path -LiteralPath $extracted) {
          $len = (Get-Item -LiteralPath $extracted).Length
          if ($len -gt 0 -and $len -eq $last) { $stable++; if ($stable -ge 2) { break } } else { $stable = 0 }
          $last = $len
        }
      }
      if (-not (Test-Path -LiteralPath $extracted)) { return 'FAIL shell wrote nothing' }
    }
  }
  if (-not (Test-Path -LiteralPath $extracted)) { return 'FAIL no extracted file' }
  return (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash
}

$decoders = @('7-Zip', 'Python', 'Explorer')

$header = "archive`tformat`tmethod`tmib`tsize`tdecoder`tresult"
Set-Content -LiteralPath $Tsv -Value $header -Encoding ASCII

$failures = 0
foreach ($case in $cases) {
  $archive = New-Archive $case
  $size = (Get-Item -LiteralPath $archive).Length
  foreach ($who in $decoders) {
    if ($who -eq '7-Zip' -and -not $SevenZip) {
      $result = 'absent (no 7z.exe on this machine)'
      $failures++
    } elseif ($canRead[$who] -notcontains $case.Format) {
      $result = "no reader for $($case.Format) in this decoder"
    } else {
      $tmp = Join-Path $WorkDir ('x-' + $case.Label + '-' + ($who -replace '[^A-Za-z0-9]', ''))
      New-Item -ItemType Directory -Path $tmp -Force | Out-Null
      $hash = Invoke-Decoder $who $archive $tmp
      if ($hash -like 'FAIL*') { $result = $hash; $failures++ }
      elseif ($hash -eq $sourceHash) { $result = 'identical bytes' }
      else { $result = "FAIL digest $hash"; $failures++ }
    }
    Add-Content -LiteralPath $Tsv -Value ("$($case.Label)`t$($case.Format)`t$($case.Method)`t$MiB`t$size`t$who`t$result") -Encoding ASCII
    Write-Output ('  {0,-14} {1,-9} {2,11}  {3,-10} {4}' -f $case.Label, $case.Method, $size, $who, $result)
  }
}

Write-Output "  tsv: $Tsv"
Write-Output "  source sha256: $sourceHash"
if ($failures) { Write-Output "== $failures decoder check(s) failed ==" } else { Write-Output '== every decoder opened every archive with identical bytes ==' }
exit ([Math]::Min($failures, 1))
