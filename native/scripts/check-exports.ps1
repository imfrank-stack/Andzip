<#
.SYNOPSIS
  Checks which symbols a built andzip_core actually exposes.

.DESCRIPTION
  The ABI contract is that include/andzip/andzip.h is the ONLY thing that leaves
  the library: 41 az_* functions and nothing else, no 7-Zip COM or C symbols.
  A cross-compiled .so has to be checked from a host that has neither dumpbin nor
  nm/objdump, so the ELF .dynsym table is parsed here; for a PE the export table
  is read through dumpbin (located via vswhere, so no developer prompt needed).
  The expected list is taken from the header itself, so it cannot drift.

.EXAMPLE
  pwsh native/scripts/check-exports.ps1 build/native/bin/andzip_core.dll
  pwsh native/scripts/check-exports.ps1 build/linux-x64/bin/andzip_core.so -ShowUndefined
#>
param(
  [Parameter(Mandatory = $true, Position = 0)] [string] $Library,
  [string] $Header = "",
  [switch] $ShowSymbols,
  [switch] $ShowUndefined
)

Set-StrictMode -Version 2
$ErrorActionPreference = "Stop"

$nativeRoot = Split-Path -Parent $PSScriptRoot
if (-not $Header) { $Header = Join-Path $nativeRoot "include/andzip/andzip.h" }
if (-not (Test-Path -LiteralPath $Library)) { throw "no such library: $Library" }
if (-not (Test-Path -LiteralPath $Header))  { throw "no such header: $Header" }

# ----------------------------------------------------------- expected from header

$text = Get-Content -LiteralPath $Header -Raw
# One declaration starts at AZ_API; the first az_ identifier followed by '(' after
# it is the function name, and the scan cannot run past the terminating ';'.
$expected = [regex]::Matches($text, '(?ms)AZ_API\b[^;]*?\b(az_[0-9A-Za-z_]+)\s*\(') |
  ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
"`nexpected from header: $($expected.Count) exports"

# ------------------------------------------------------------------ file bytes

$bytes = [System.IO.File]::ReadAllBytes($Library)
function Read-U16([byte[]] $b, [int] $o) { [BitConverter]::ToUInt16($b, $o) }
function Read-U32([byte[]] $b, [int] $o) { [BitConverter]::ToUInt32($b, $o) }
function Read-U64([byte[]] $b, [int] $o) { [BitConverter]::ToUInt64($b, $o) }
function Read-CString([byte[]] $b, [int] $o) {
  $e = $o; while ($b[$e] -ne 0) { $e++ }
  [System.Text.Encoding]::UTF8.GetString($b, $o, $e - $o)
}

$isElf = $bytes.Length -gt 4 -and $bytes[0] -eq 0x7F -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x4C -and $bytes[3] -eq 0x46
$isPe  = $bytes.Length -gt 2 -and $bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A

if ($isElf) {
  # ------------------------------------------------------------ ELF64 sections
  if ($bytes[4] -ne 2) { throw "only ELF64 is supported" }
  $mach    = Read-U16 $bytes 18
  $shoff   = Read-U64 $bytes 40
  $shentsz = Read-U16 $bytes 58
  $shnum   = Read-U16 $bytes 60
  $shstrndx= Read-U16 $bytes 62
  $eType   = Read-U16 $bytes 16
  $machName = switch ($mach) { 0x3E {"x86-64"} 0xB7 {"AArch64"} 0x28 {"ARM"} 0xF3 {"RISC-V"} 0x14 {"PPC64"} 0x03 {"Intel 80386"} default {"0x{0:X}" -f $mach} }
  $typeName = switch ($eType) { 2 {"EXEC"} 3 {"DYN (shared object)"} default {"$eType"} }

  # sh_offset is at +24 in an ELF64 section header; +0 is sh_name|sh_type.
  $shstrOff = Read-U64 $bytes ($shoff + $shstrndx * $shentsz + 24)
  $sections = @()
  for ($i = 0; $i -lt $shnum; $i++) {
    $o = $shoff + $i * $shentsz
    $sections += [pscustomobject]@{
      Name   = Read-CString $bytes ($shstrOff + (Read-U32 $bytes $o))
      Offset = Read-U64 $bytes ($o + 24)
      Size   = Read-U64 $bytes ($o + 32)
      Link   = Read-U32 $bytes ($o + 40)
      EntSz  = Read-U64 $bytes ($o + 56)
    }
  }
  $dynsym = $sections | Where-Object { $_.Name -eq ".dynsym" } | Select-Object -First 1
  if (-not $dynsym) { throw "no .dynsym section; the file is not a linked shared object" }
  $strtab = $sections[$dynsym.Link]

  "`n$Library"
  "  format    ELF64, $typeName, $machName"
  "  .dynsym   $($dynsym.Size / $dynsym.EntSz) entries"

  $defined = @(); $undefined = @()
  $n = [int]($dynsym.Size / $dynsym.EntSz)
  for ($i = 0; $i -lt $n; $i++) {
    $o = $dynsym.Offset + $i * $dynsym.EntSz
    $nameOff = Read-U32 $bytes $o
    if ($nameOff -eq 0) { continue }
    $info = $bytes[$o + 4]
    $bind = $info -shr 4          # 1 = GLOBAL, 2 = WEAK
    $shndx = Read-U16 $bytes ($o + 6)
    $name = Read-CString $bytes ($strtab.Offset + $nameOff)
    if ($bind -ne 1) { continue }  # only strong global definitions are the ABI
    if ($shndx -eq 0) { $undefined += $name } else { $defined += $name }
  }
  $exported = $defined | Sort-Object -Unique

  # The libraries the loader will have to satisfy; libc/libm/libpthread/libstdc++
  # are expected, anything else means the cross link pulled in a stray dependency.
  $needed = @()
  $dyn = $sections | Where-Object { $_.Name -eq ".dynamic" } | Select-Object -First 1
  if ($dyn) {
    $dstr = ($sections | Where-Object { $_.Name -eq ".dynstr" } | Select-Object -First 1).Offset
    for ($o = $dyn.Offset; $o + 16 -le $dyn.Offset + $dyn.Size; $o += 16) {
      if ((Read-U64 $bytes $o) -eq 1) { $needed += (Read-CString $bytes ([int64](Read-U64 $bytes ($o + 8)) + $dstr)) }
    }
  }
  if ($needed.Count) { "  DT_NEEDED $($needed -join ', ')" }
}
elseif ($isPe) {
  # -------------------------------------------------------------- PE (dll/lib)
  # The machine type is read locally so x64/ARM64 still reports without a VS
  # environment, but the export table itself comes from dumpbin: getting a hand
  # rolled RVA->file mapping and the IMAGE_EXPORT_DIRECTORY layout right is a lot
  # of fragile code when the linker's own tool is one call away.
  $nt = Read-U32 $bytes 0x3C
  $machine = Read-U16 $bytes ($nt + 4)
  $machName = switch ($machine) { 0x8664 {"x64"} 0xAA64 {"ARM64"} 0x1C0 {"x86"} default {"0x{0:X}" -f $machine} }

  "`n$Library"
  "  format    PE, $machName"

  $dumpbinExe = $null
  $g = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
  if ($g) { $dumpbinExe = $g.Source }
  else {
    # Find it without requiring a developer prompt.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path -LiteralPath $vswhere) {
      $vs = & $vswhere -latest -property installationPath | Select-Object -First 1
      if ($vs) {
        $found = Get-ChildItem -Path (Join-Path $vs "VC\Tools\MSVC\*\bin\Host*\x64\dumpbin.exe") -ErrorAction SilentlyContinue |
          Select-Object -Last 1
        if ($found) { $dumpbinExe = $found.FullName }
      }
    }
  }
  if (-not $dumpbinExe) { throw "dumpbin not found; run this from a Visual Studio developer prompt for PE inputs" }

  $lines = & $dumpbinExe /EXPORTS $Library 2>&1
  if ($LASTEXITCODE -ne 0) { throw "dumpbin failed: $($lines -join ' ')" }
  # The name column is the last field of each numbered export line.
  $exported = @($lines |
    Where-Object { $_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(az_\w+)' } |
    ForEach-Object { [regex]::Match($_, '(az_\w+)').Groups[1].Value } |
    Sort-Object -Unique)
  $skipped = @($lines | Where-Object { $_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+' -and $_ -notmatch 'az_\w+' })
  if ($skipped.Count) { "  other     $($skipped.Count) non-az_ export(s) present" }
  "  .exports  $($exported.Count) named"
  $undefined = @()
}
else {
  throw "unrecognised file format (not ELF64, not PE): $Library"
}

# -------------------------------------------------------------------- verdict

$missing = @($expected | Where-Object { $exported -notcontains $_ })
$extra   = @($exported | Where-Object { $expected -notcontains $_ })

if ($ShowSymbols -or $extra.Count) {
  "`nexported: $($exported.Count)"
  foreach ($s in $exported) {
    $tag = if ($expected -contains $s) { "" } else { "   <== not declared in the header" }
    "  $s$tag"
  }
}

if ($ShowUndefined -and $undefined.Count) {
  "`nundefined (imported) globals: $($undefined.Count)"
  foreach ($s in ($undefined | Sort-Object -Unique)) { "  $s" }
}

if ($missing.Count -or $extra.Count) {
  foreach ($m in $missing) { "MISSING     $m" }
  foreach ($x in $extra)   { "UNEXPECTED  $x" }
  "`nexport set does not match $Header"
  exit 1
}

"exported      $($exported.Count) -- exactly the header's declarations, no 7-Zip symbols leaked"
exit 0
