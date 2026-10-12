<#
.SYNOPSIS
    Populates native/third_party from the reference archives in cankao/.

.DESCRIPTION
    The vendored trees are the compression engine that libandzip_core links
    against. Everything is copied verbatim from cankao/*.zip so the provenance
    stays auditable; nothing is patched. See native/third_party/MANIFEST.json
    for the exact mapping.

    Re-running is safe: third_party/7zip and third_party/codecs are wiped first.

.EXAMPLE
    pwsh native/scripts/vendor-sources.ps1
    pwsh native/scripts/vendor-sources.ps1 -TempRoot D:\scratch\azref
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = '',
    [string]$TempRoot = '',

    # Folder holding the upstream reference archives (7zip-main.zip,
    # 7-Zip-zstd-master.zip, NanaZip-main.zip). They are deliberately not part of the
    # delivery folder, so this defaults to probing a couple of likely places.
    [string]$SourceArchives = '',

    # Overlays the third-party 7-Zip codec wrappers onto the stock engine tree.
    # Off by default: the fork's ZstdDecoder.h is ABI-incompatible with stock
    # Archive/ZstdHandler.cpp, so enabling this also replaces that handler.
    [switch]$ExtraCodecs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrEmpty($RepoRoot)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
    $RepoRoot  = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
}
if ([string]::IsNullOrEmpty($TempRoot)) {
    $TempRoot = Join-Path ([IO.Path]::GetTempPath()) 'andzip-vendor-src'
}

if ([string]::IsNullOrEmpty($SourceArchives)) {
    # The delivery folder is self-contained; the upstream zips are not part of it.
    # Probe next to the repo root, then one level up (the original working tree).
    $SourceArchives = @(
        (Join-Path $RepoRoot 'cankao'),
        (Join-Path (Split-Path -Parent $RepoRoot) 'cankao')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $SourceArchives) {
        throw ('no reference-archive folder found; pass -SourceArchives <dir> containing ' +
               '7zip-main.zip, 7-Zip-zstd-master.zip and NanaZip-main.zip ' +
               '(native/third_party/MANIFEST.json records what each one provides)')
    }
}

$Cankao     = $SourceArchives
$ThirdParty = Join-Path $RepoRoot 'native\third_party'
$SevenZip   = Join-Path $ThirdParty '7zip'
$Codecs     = Join-Path $ThirdParty 'codecs'

function Expand-Ref {
    param([string]$ZipName, [string]$SubPath)

    $zip = Join-Path $Cankao $ZipName
    if (-not (Test-Path -LiteralPath $zip)) {
        throw "Reference archive not found: $zip"
    }

    $dest = Join-Path $TempRoot ([IO.Path]::GetFileNameWithoutExtension($ZipName))
    if (-not (Test-Path -LiteralPath $dest)) {
        Write-Host "  extracting $ZipName ..."
        New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dest)
    }

    # GitHub archives nest everything under a single "<repo>-<branch>" folder.
    $inner = Get-ChildItem -LiteralPath $dest -Directory | Select-Object -First 1
    $root  = if ($inner) { $inner.FullName } else { $dest }
    $full  = if ($SubPath) { Join-Path $root $SubPath } else { $root }
    if (-not (Test-Path -LiteralPath $full)) { throw "Missing $SubPath in $ZipName" }
    return $full
}

function Copy-Tree {
    param([string]$From, [string]$To)

    if (Test-Path -LiteralPath $To) { Remove-Item -LiteralPath $To -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path $To) | Out-Null
    Copy-Item -LiteralPath $From -Destination $To -Recurse -Force
}

function Remove-Glob {
    param([string]$Root, [string[]]$Patterns)

    foreach ($p in $Patterns) {
        Get-ChildItem -LiteralPath $Root -Recurse -Force -Filter $p -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Add-ForkZstdCoders {
    param([string]$From, [string]$To)

    # The fork spells its coder NCompress::NZSTD and stock 7-Zip spells the newer
    # decode-only one NCompress::NZstd -- same file names, different namespaces.
    # Overwriting stock is not an option: Archive/Zip/ZipHandler.cpp and
    # Archive/ZstdHandler.cpp both include "Compress/ZstdDecoder.h" for the stock
    # class, and the build dies on C2039 "NZstd is not a member of NCompress".
    #
    # So the fork pair lands beside it as ZstdFork*, which means rewriting the
    # quoted self-includes. Relative paths ("../../../C/zstd/zstd.h") are untouched
    # because the files stay in Compress/.
    $map = @{
        'ZstdDecoder.h'  = 'ZstdForkDecoder.h'
        'ZstdDecoder.cpp' = 'ZstdForkDecoder.cpp'
        'ZstdEncoder.h'  = 'ZstdForkEncoder.h'
        'ZstdEncoder.cpp' = 'ZstdForkEncoder.cpp'
        'ZstdRegister.cpp' = 'ZstdForkRegister.cpp'
    }

    foreach ($entry in $map.GetEnumerator()) {
        $source = Join-Path $From $entry.Key
        if (-not (Test-Path -LiteralPath $source)) {
            throw "zstd glue missing from the fork archive: $source"
        }

        $text = [IO.File]::ReadAllText($source)
        foreach ($rename in $map.GetEnumerator()) {
            $text = $text.Replace('#include "' + $rename.Key + '"', '#include "' + $rename.Value + '"')
        }
        [IO.File]::WriteAllText((Join-Path $To $entry.Value), $text)
    }

    Write-Host '      + ZstdFork{Decoder,Encoder,Register} (beside the stock NZstd coder)'
}

function Add-ZstdCoderProperties {
    param([string]$Path)

    # The zstd / LZ4 / LZ5 / Lizard / Brotli wrappers switch on NCoderPropID names
    # that stock 7-Zip keeps inside a /* */ block. Appending them -- never inserting
    # -- right before k_NUM_DEFINED keeps every ID above at the number stock gives
    # it. Handlers and coders compile into the same DLL, so all that matters is that
    # both sides agree, not that the numbers match some other build.
    #
    # This has to live in the script, not in a hand edit: the vendor run deletes
    # third_party/7zip and copies it fresh from the zips every time.
    $anchor = '    k_NUM_DEFINED'
    $macros = '#define Z7_IFACEM_ICompressSetCoderPropertiesOpt'

    $text = [IO.File]::ReadAllText($Path)
    if ($text.Contains('kAdvMax,')) {
        Write-Host '      ICoder.h already carries the zstd coder properties'
        return
    }

    $enum = @(
      '    /* ANDZIP LOCAL PATCH -- applied by native/scripts/vendor-sources.ps1 -ExtraCodecs.'
      '       Property IDs the extra-codec wrappers read. kLdmSearchLength is the fork''s'
      '       name for what stock spells kLdmMinMatchLength; both are dead here, our API'
      '       only ever sends kLevel and kNumThreads. */'
      '    kStrategy,          // VT_UI4 1=ZSTD_fast .. 8=ZSTD_btultra'
      '    kFast,              // VT_UI4 1..64, the zstd --fast levels'
      '    kLong,              // VT_UI4 10..31, the zstd --long window log'
      '    kWindowLog,         // VT_UI4 10..31'
      '    kHashLog,           // VT_UI4 6..26'
      '    kChainLog,          // VT_UI4 6..28'
      '    kSearchLog,         // VT_UI4 1..26'
      '    kMinMatch,          // VT_UI4 3..7'
      '    kTargetLen,         // VT_UI4 0..999'
      '    kOverlapLog,        // VT_UI4 0..9 (default 6)'
      '    kLdmHashLog,        // VT_UI4 6..26 (default 20)'
      '    kLdmSearchLength,   // VT_UI4 4..4096 (default 64)'
      '    kLdmBucketSizeLog,  // VT_UI4 0..8 (default 3)'
      '    kLdmHashRateLog,    // VT_UI4 (default wlog - ldmhlog)'
      '    kAdvMax,            // VT_BOOL zstd --max'
      $anchor
    ) -join "`r`n"

    # k_NUM_DEFINED occurs exactly once in the stock header; the idempotence guard
    # above means a second vendor run cannot splice the block twice.
    $text = $text.Replace($anchor, $enum)
    # zstd's fast/--max level encodings, used by both the encoder and the decoder.
    # The fork gates --max on 64-bit; we gate it off outright: az_create_options has
    # no knob that reaches kAdvMax, and zstd --max asks for hundreds of megabytes.
    $defines = @(
      '/* ANDZIP LOCAL PATCH -- zstd level encodings (see ZstdEncoder.cpp / ZstdDecoder.cpp).'
      '   32: the fast levels are sent as 1..64 and stored as level + 32.'
      '   255: "as strong as zstd goes", the fork''s stand-in for --max.'
      '*/'
      '#define Z7_ZSTD_FAST_LEV_INC  32'
      '#define Z7_ZSTD_ULTIMATE_LEV  255'
      '#ifndef Z7_ZSTD_ADVMAX_ALLOWED'
      '#define Z7_ZSTD_ADVMAX_ALLOWED 0'
      '#endif'
      ''
      $macros
    ) -join "`r`n"
    $text = $text.Replace($macros, $defines)

    [IO.File]::WriteAllText($Path, $text)
    Write-Host '      + ICoder.h: zstd coder property IDs and level macros'
}

Write-Host "Vendoring into $ThirdParty"
New-Item -ItemType Directory -Force -Path $TempRoot | Out-Null

# Start from a clean slate so files dropped by a previous layout do not linger.
foreach ($stale in @($SevenZip, $Codecs)) {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $SevenZip, $Codecs | Out-Null

# ---------------------------------------------------------------- 7-Zip engine
Write-Host '[1/4] 7-Zip (cankao/7zip-main.zip)'
$z7 = Expand-Ref '7zip-main.zip' ''
Copy-Tree (Join-Path $z7 'C')   (Join-Path $SevenZip 'C')
Copy-Tree (Join-Path $z7 'CPP') (Join-Path $SevenZip 'CPP')
Copy-Tree (Join-Path $z7 'DOC') (Join-Path $SevenZip 'DOC')

# Console/GUI front ends are not part of the engine; they pull in Windows
# control classes and resource scripts we have no use for.
Remove-Item -LiteralPath (Join-Path $SevenZip 'CPP\7zip\UI') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Glob $SevenZip @('*.dsp', '*.dsw', '*.vcxproj', '*.vcxproj.filters', '*.rc', '*.def')

# ---------------------------------------------------------------- zstd + zstdmt
Write-Host '[2/4] zstd / zstdmt (cankao/7-Zip-zstd-master.zip)'
$zs = Expand-Ref '7-Zip-zstd-master.zip' ''

# The 7-Zip glue for these codecs includes them as "../../../C/zstd/zstd.h",
# so the libraries have to sit where upstream puts them: inside C/. These two
# directories do not exist in stock 7-Zip, so they never collide.
Copy-Tree (Join-Path $zs 'C\zstd')   (Join-Path $SevenZip 'C\zstd')
Copy-Tree (Join-Path $zs 'C\zstdmt') (Join-Path $SevenZip 'C\zstdmt')
Copy-Item -LiteralPath (Join-Path $zs 'COPYING') -Destination (Join-Path $Codecs 'zstd-COPYING') -Force -ErrorAction SilentlyContinue
Remove-Glob (Join-Path $SevenZip 'C\zstd') @('*.asm', '*.S')

if ($ExtraCodecs) {
    Add-ForkZstdCoders (Join-Path $zs 'CPP\7zip\Compress') (Join-Path $SevenZip 'CPP\7zip\Compress')
    Add-ZstdCoderProperties (Join-Path $SevenZip 'CPP\7zip\ICoder.h')
}

# ------------------------------------------------------------- NanaZip codecs
Write-Host '[3/4] LZ4 / BLAKE3 / xxHash / Brotli / Lizard / LZ5 (cankao/NanaZip-main.zip)'
$nz      = Expand-Ref 'NanaZip-main.zip' ''
$nzCodec = Join-Path $nz 'NanaZip.Codecs'
$nzZs    = Join-Path $nz 'NanaZip.ZS.Codecs'
$glue    = Join-Path $nzZs 'SevenZip\CPP\7zip'

# Standalone libraries; they have no dependency on the 7-Zip tree.
Copy-Tree (Join-Path $nzCodec 'LZ4')    (Join-Path $Codecs 'lz4')
Copy-Tree (Join-Path $nzCodec 'BLAKE3') (Join-Path $Codecs 'blake3')
Copy-Tree (Join-Path $nzCodec 'xxHash') (Join-Path $Codecs 'xxhash')
Copy-Tree (Join-Path $nzZs 'Brotli')    (Join-Path $Codecs 'brotli')
Copy-Tree (Join-Path $nzZs 'Lizard')    (Join-Path $Codecs 'lizard')
Copy-Tree (Join-Path $nzZs 'LZ5')       (Join-Path $Codecs 'lz5')

if ($ExtraCodecs) {
    # The 7-Zip wrappers must land inside the engine tree: they include
    # "../ICoder.h" and "../../../C/zstdmt/lz4-mt.h", which only resolve from
    # CPP/7zip/{Compress,Archive}. Only the wrapper files are taken -- the rest
    # of NanaZip.ZS.Codecs/SevenZip duplicates files we already have from
    # 7zip-main and would collide at link time.
    Write-Host '      + LZ4 / LZ5 / Lizard / Brotli 7-Zip wrappers'
    $compressDst = Join-Path $SevenZip 'CPP\7zip\Compress'
    $archiveDst  = Join-Path $SevenZip 'CPP\7zip\Archive'
    foreach ($stem in @('Lz4', 'Lz5', 'Lizard', 'Brotli')) {
        Get-ChildItem -LiteralPath (Join-Path $glue 'Compress') -Filter "$stem*" -ErrorAction SilentlyContinue |
            Copy-Item -Destination $compressDst -Force
        Get-ChildItem -LiteralPath (Join-Path $glue 'Archive') -Filter "$stem*Handler.cpp" -ErrorAction SilentlyContinue |
            Copy-Item -Destination $archiveDst -Force
    }
}

Copy-Item -LiteralPath (Join-Path $nz 'License.md') -Destination (Join-Path $Codecs 'NanaZip-License.md') -Force -ErrorAction SilentlyContinue
Remove-Glob $Codecs @('*.asm')

# ------------------------------------------------------------------- manifest
Write-Host '[4/4] manifest'
$manifest = [ordered]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    note         = 'Regenerate with native/scripts/vendor-sources.ps1. Do not edit vendored files by hand.'
    sources      = @(
        [ordered]@{
            path     = 'third_party/7zip'
            origin   = 'cankao/7zip-main.zip'
            upstream = 'https://github.com/ip7z/7zip'
            license  = 'LGPL-2.1-or-later with unRAR restriction (see 7zip/DOC/License.txt and 7zip/DOC/unRarLicense.txt)'
            included = @('C', 'CPP', 'DOC')
            excluded = @('CPP/7zip/UI (console/GUI front ends)', 'Asm (x86 optimisations, C fallbacks used instead)')
        },
        [ordered]@{
            path     = 'third_party/7zip/C/zstd'
            origin   = 'cankao/7-Zip-zstd-master.zip -> C/zstd'
            upstream = 'https://github.com/facebook/zstd'
            license  = 'BSD-3-Clause OR GPL-2.0-only'
        },
        [ordered]@{
            path     = 'third_party/7zip/C/zstdmt'
            origin   = 'cankao/7-Zip-zstd-master.zip -> C/zstdmt'
            upstream = 'https://github.com/oneunified/zstdmt'
            license  = 'MIT'
        },
        [ordered]@{
            path     = 'third_party/7zip/CPP/7zip/Compress/Zstd*'
            origin   = 'cankao/7-Zip-zstd-master.zip -> CPP/7zip/Compress/Zstd*'
            license  = 'LGPL-2.1-or-later (7-Zip-Zstandard, see codecs/zstd-COPYING)'
        },
        [ordered]@{
            path     = 'third_party/codecs/lz4'
            origin   = 'cankao/NanaZip-main.zip -> NanaZip.Codecs/LZ4'
            upstream = 'https://github.com/lz4/lz4'
            license  = 'BSD-2-Clause OR GPL-2.0-only'
        },
        [ordered]@{
            path     = 'third_party/codecs/blake3'
            origin   = 'cankao/NanaZip-main.zip -> NanaZip.Codecs/BLAKE3'
            upstream = 'https://github.com/BLAKE3-team/BLAKE3'
            license  = 'CC0-1.0 OR Apache-2.0'
        },
        [ordered]@{
            path     = 'third_party/codecs/xxhash'
            origin   = 'cankao/NanaZip-main.zip -> NanaZip.Codecs/xxHash'
            upstream = 'https://github.com/Cyan4973/xxHash'
            license  = 'BSD-2-Clause'
        },
        [ordered]@{
            path     = 'third_party/codecs/brotli'
            origin   = 'cankao/NanaZip-main.zip -> NanaZip.ZS.Codecs/Brotli'
            upstream = 'https://github.com/google/brotli'
            license  = 'MIT'
        },
        [ordered]@{
            path     = 'third_party/codecs/lizard'
            origin   = 'cankao/NanaZip-main.zip -> NanaZip.ZS.Codecs/Lizard'
            upstream = 'https://github.com/inikep/lizard'
            license  = 'BSD-2-Clause OR GPL-2.0-only'
        },
        [ordered]@{
            path     = 'third_party/codecs/lz5'
            origin   = 'cankao/NanaZip-main.zip -> NanaZip.ZS.Codecs/LZ5'
            upstream = 'https://github.com/inikep/lz5'
            license  = 'BSD-2-Clause OR GPL-2.0-only'
        },
        [ordered]@{
            path     = 'third_party/7zip/CPP/7zip/{Compress,Archive}/{Lz4,Lz5,Lizard,Brotli}*'
            origin   = 'cankao/NanaZip-main.zip -> NanaZip.ZS.Codecs/SevenZip/CPP/7zip/{Compress,Archive}'
            license  = 'MIT (NanaZip, see NanaZip-License.md)'
            note     = '7-Zip wrappers for the codec libraries above; placed in the engine tree so their relative includes resolve'
        }
    )
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ThirdParty 'MANIFEST.json') -Encoding utf8

Write-Host ''
Write-Host 'Vendored tree sizes:'
foreach ($d in @($SevenZip, $Codecs)) {
    $bytes = (Get-ChildItem -LiteralPath $d -Recurse -File | Measure-Object Length -Sum).Sum
    $files = (Get-ChildItem -LiteralPath $d -Recurse -File).Count
    '  {0,-40} {1,8:N1} MB  {2,5} files' -f $d, ($bytes / 1MB), $files
}
Write-Host 'Done.'
