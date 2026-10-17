// Regenerates native/cmake/7zip-sources.cmake from the vendored 7-Zip makefiles.
//
// 7-Zip ships its build recipe as a set of nmake fragments. The Format7zF
// bundle is the "everything" target: Bundles/Format7zF/makefile pulls in
// Arc.mak, which enumerates one object group per module, and 7zip.mak, which
// maps each group to a source directory. This script replays that mapping so
// the CMake build stays faithful to upstream instead of hand-curating files.
//
//   node native/scripts/gen-sources.mjs
//
// Only the no-ASM configuration is emitted: every optimised routine that
// upstream would take from Asm/x86 is instead taken from its C counterpart
// (AesOpt.c, 7zCrcOpt.c, Sort.c, ...), which keeps the same sources buildable
// on MSVC, GCC and Clang for x64/x86/arm64 alike.

import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const nativeRoot = resolve(here, '..');
const sevenZip = join(nativeRoot, 'third_party', '7zip');
const arcMak = join(sevenZip, 'CPP', '7zip', 'Bundles', 'Format7zF', 'Arc.mak');
const outFile = join(nativeRoot, 'cmake', '7zip-sources.cmake');

// group name in Arc.mak -> directory the sources live in, relative to third_party/7zip
const GROUP_DIRS = {
  COMMON_OBJS: 'CPP/Common',
  WIN_OBJS: 'CPP/Windows',
  '7ZIP_COMMON_OBJS': 'CPP/7zip/Common',
  AR_OBJS: 'CPP/7zip/Archive',
  AR_COMMON_OBJS: 'CPP/7zip/Archive/Common',
  '7Z_OBJS': 'CPP/7zip/Archive/7z',
  CAB_OBJS: 'CPP/7zip/Archive/Cab',
  CHM_OBJS: 'CPP/7zip/Archive/Chm',
  COM_OBJS: 'CPP/7zip/Archive/Com',
  ISO_OBJS: 'CPP/7zip/Archive/Iso',
  NSIS_OBJS: 'CPP/7zip/Archive/Nsis',
  RAR_OBJS: 'CPP/7zip/Archive/Rar',
  TAR_OBJS: 'CPP/7zip/Archive/Tar',
  UDF_OBJS: 'CPP/7zip/Archive/Udf',
  WIM_OBJS: 'CPP/7zip/Archive/Wim',
  ZIP_OBJS: 'CPP/7zip/Archive/Zip',
  COMPRESS_OBJS: 'CPP/7zip/Compress',
  CRYPTO_OBJS: 'CPP/7zip/Crypto',
  C_OBJS: 'C',
};

// Bundle-level additions from Bundles/Format7zF/makefile, plus FileStreams.cpp:
// 7z.dll leaves file I/O to the console/GUI front ends, so CInFileStream and
// COutFileStream are only pulled in by CPP/7zip/UI/Common, which we do not
// vendor. MultiOutStream.cpp came back with 5.4.0: driving it is split-volume
// output for 7z, and the reason it once produced no files at all was our own
// missing FinalFlush_and_CloseFiles() call, not the class (see az_writer.cpp).
const EXTRA = {
  COMPRESS_OBJS: ['CodecExports'],
  AR_OBJS: ['ArchiveExports', 'DllExports2'],
  '7ZIP_COMMON_OBJS': ['FileStreams', 'MultiOutStream'],
};

// Objects contributed by the sub-makefiles Arc.mak !includes at the bottom,
// resolved for the no-ASM / USE_C_* configuration:
//   Aes.mak  -> Aes + AesOpt          Crc.mak    -> 7zCrc + 7zCrcOpt
//   Crc64.mak-> XzCrc64 + XzCrc64Opt  LzFindOpt.mak -> LzFindOpt
//   Sha1.mak -> Sha1 + Sha1Opt        Sha256.mak -> Sha256 + Sha256Opt
//   Sort.mak -> Sort                  LzmaDec.mak -> (asm only, skipped)
// Sha1.mak / Sha256.mak also append *Prepare to COMMON_OBJS.
const SUBMAK = {
  C_OBJS: ['Aes', 'AesOpt', '7zCrc', '7zCrcOpt', 'XzCrc64', 'XzCrc64Opt', 'LzFindOpt', 'Sha1', 'Sha1Opt', 'Sha256', 'Sha256Opt', 'Sort'],
  COMMON_OBJS: ['Sha1Prepare', 'Sha256Prepare'],
};

function parseGroups(text) {
  const groups = new Map();
  const lines = text.split(/\r?\n/);
  let current = null;
  for (const raw of lines) {
    const line = raw.replace(/\s+$/, '');
    if (!line || line.startsWith('#') || line.startsWith('!')) {
      // nmake conditionals we do not model; the object tokens below still count.
      if (!line.endsWith('\\')) current = null;
      continue;
    }
    const assign = line.match(/^([A-Za-z0-9_]+)\s*(\+?=)\s*(.*)$/);
    if (assign) {
      current = assign[1];
      if (!groups.has(current)) groups.set(current, []);
      pushTokens(groups.get(current), assign[3]);
      if (!assign[3].endsWith('\\')) current = null;
      continue;
    }
    if (current) {
      pushTokens(groups.get(current), line);
      if (!line.endsWith('\\')) current = null;
    }
  }
  return groups;
}

function pushTokens(into, chunk) {
  for (const m of chunk.matchAll(/\$O[\\/]([A-Za-z0-9_+-]+)\.obj/g)) into.push(m[1]);
}

function main() {
  if (!existsSync(arcMak)) {
    console.error(`missing ${relative(process.cwd(), arcMak)}`);
    console.error('run native/scripts/vendor-sources.ps1 first');
    process.exit(1);
  }

  const groups = parseGroups(readFileSync(arcMak, 'utf8'));
  const missing = [];
  const emitted = [];
  let total = 0;

  for (const [group, dir] of Object.entries(GROUP_DIRS)) {
    const names = [...(groups.get(group) ?? []), ...(SUBMAK[group] ?? []), ...(EXTRA[group] ?? [])];
    const unique = [...new Set(names)];
    const ext = group === 'C_OBJS' ? '.c' : '.cpp';
    const paths = [];
    for (const name of unique) {
      const rel = `${dir}/${name}${ext}`;
      if (!existsSync(join(sevenZip, ...rel.split('/')))) {
        missing.push(rel);
        continue;
      }
      paths.push(rel);
    }
    if (!paths.length) continue;
    total += paths.length;
    const varName = `AZ7Z_${group.replace(/^7/, 'SEVEN_')}`;
    emitted.push({ varName, group, dir, paths });
  }

  const stamp = new Date().toISOString();
  const lines = [];
  lines.push('# Generated by native/scripts/gen-sources.mjs -- DO NOT EDIT BY HAND.');
  lines.push(`# Generated: ${stamp}`);
  lines.push('#');
  lines.push('# Derived from third_party/7zip/CPP/7zip/Bundles/Format7zF/{makefile,Arc.mak} plus');
  lines.push('# the sub-makefiles Arc.mak !includes, resolved for the no-ASM configuration.');
  lines.push('# Re-run the generator after re-vendoring third_party/7zip.');
  lines.push('');
  lines.push('if(NOT DEFINED AZ_7ZIP_ROOT)');
  lines.push('  set(AZ_7ZIP_ROOT "${CMAKE_CURRENT_LIST_DIR}/../third_party/7zip")');
  lines.push('endif()');
  lines.push('');

  for (const { varName, group, dir, paths } of emitted) {
    lines.push(`# ${group} -> ${dir}  (${paths.length} files)`);
    lines.push(`set(${varName}_DIR "\${AZ_7ZIP_ROOT}/${dir}")`);
    lines.push(`set(${varName}`);
    for (const p of paths) lines.push(`  "\${AZ_7ZIP_ROOT}/${p}"`);
    lines.push(')');
    lines.push('');
  }

  lines.push('set(AZ_7ZIP_ENGINE_SOURCES');
  for (const { varName } of emitted) lines.push(`  \${${varName}}`);
  lines.push(')');
  lines.push('');
  lines.push('set(AZ_7ZIP_INCLUDE_DIRS');
  lines.push('  "${AZ_7ZIP_ROOT}"');
  lines.push('  "${AZ_7ZIP_ROOT}/C"');
  lines.push('  "${AZ_7ZIP_ROOT}/CPP"');
  lines.push('  "${AZ_7ZIP_ROOT}/CPP/7zip"');
  lines.push(')');
  lines.push('');

  writeFileSync(outFile, lines.join('\n'), 'utf8');

  console.log(`wrote ${relative(process.cwd(), outFile)}`);
  console.log(`  ${emitted.length} groups, ${total} translation units`);
  if (missing.length) {
    console.log(`  ${missing.length} listed-but-absent (skipped):`);
    for (const m of missing) console.log(`    ${m}`);
  }
}

main();
