# Extra coders vendored from cankao/NanaZip-main.zip and cankao/7-Zip-zstd-master.zip.
#
# Off by default (ANDZIP_EXTRA_CODECS). When enabled this adds encoders and
# decoders that stock 7-Zip does not ship: Zstandard, LZ4, LZ5, Lizard and
# Brotli, plus the multithreaded wrappers from zstdmt. Stock 7-Zip already reads
# Zstd but cannot write it; the fork's encoder/decoder pair is added alongside it,
# see AZ_GLUE_SOURCES below.
#
# Layout note: the libraries live under third_party/codecs/, but their 7-Zip
# wrappers were vendored straight into third_party/7zip/CPP/7zip/{Compress,Archive}
# and third_party/7zip/C/{zstd,zstdmt}. The wrappers resolve every include
# relative to those locations, so moving them would mean patching sources.
#
# Two flags have to agree: this option needs the wrappers placed by
#   pwsh native/scripts/vendor-sources.ps1 -ExtraCodecs
# Enabling one without the other is caught up front below.

# Resolved at FILE scope, not inside the function: CMAKE_CURRENT_LIST_DIR tracks
# the listfile being processed, so reading it from az_add_extra_codecs() -- which
# runs later, called from CMakeLists.txt -- yields native/ instead of native/cmake/
# and every codec path lands one directory above third_party/. Same idiom as
# cmake/7zip-sources.cmake uses for AZ_7ZIP_ROOT.
if(NOT DEFINED AZ_CODEC_ROOT)
  set(AZ_CODEC_ROOT "${CMAKE_CURRENT_LIST_DIR}/../third_party/codecs")
endif()

# Same file-scope reason as AZ_CODEC_ROOT: our own sources beside the library.
if(NOT DEFINED AZ_NATIVE_ROOT)
  set(AZ_NATIVE_ROOT "${CMAKE_CURRENT_LIST_DIR}/..")
endif()

function(az_add_extra_codecs target)
  set(CODECS "${AZ_CODEC_ROOT}")
  set(ZSTD   "${AZ_7ZIP_ROOT}/C/zstd")
  set(ZSTDMT "${AZ_7ZIP_ROOT}/C/zstdmt")
  set(COMPR  "${AZ_7ZIP_ROOT}/CPP/7zip/Compress")
  set(ARCH   "${AZ_7ZIP_ROOT}/CPP/7zip/Archive")

  # ------------------------------------------------------- Zstandard/ shim
  #
  # LZ5 and Lizard came from NanaZip, whose 7-Zip glue reaches zstd headers through
  # the *upstream* layout: `#include <Zstandard/common/mem.h>`. Our zstd came from
  # the 7-Zip-zstd fork, which flattens common/ and compress/ into C/zstd/.
  #
  # Generate forwarding headers into the build tree instead of copying the real ones
  # (two copies of mem.h would drift) and instead of symlinking (needs privileges
  # on many Windows setups). The target is an absolute path on purpose: a bare
  # `#include "mem.h"` from inside the shim would find *itself* first and recurse.
  set(ZSTD_SHIM_ROOT "${CMAKE_CURRENT_BINARY_DIR}/codec-include")
  set(ZSTD_SHIM "${ZSTD_SHIM_ROOT}/Zstandard")
  foreach(_entry
      common/bitstream.h common/compiler.h common/debug.h
      common/mem.h common/zstd_deps.h compress/hist.h)
    get_filename_component(_flat "${_entry}" NAME)
    get_filename_component(_dir "${ZSTD_SHIM}/${_entry}" DIRECTORY)
    file(MAKE_DIRECTORY "${_dir}")
    file(WRITE "${ZSTD_SHIM}/${_entry}" "#include \"${ZSTD}/${_flat}\"\n")
  endforeach()

  # ------------------------------------------------------------------ zstd
  # third_party/7zip/C/zstd/zstd_internal.h does `#include "../hashes/xxhash.h"`.
  # That sibling directory is part of the 7-Zip-zstd fork's layout and is not in
  # our tree (the fork archive is gone); the header itself we already vendor as
  # third_party/codecs/xxhash/xxhash.h. Materialise the expected path from it so
  # there is still exactly one copy of the code -- and so a future re-vendor that
  # does bring C/hashes back simply overwrites an identical file.
  set(HASHES "${AZ_7ZIP_ROOT}/C/hashes")
  if(NOT EXISTS "${HASHES}/xxhash.h")
    file(MAKE_DIRECTORY "${HASHES}")
    file(COPY "${CODECS}/xxhash/xxhash.h" DESTINATION "${HASHES}")
  endif()

  file(GLOB AZ_ZSTD_SOURCES "${ZSTD}/*.c")
  # Legacy decoders (v0.1 .. v0.7) are only reachable through
  # ZSTD_LEGACY_SUPPORT, which we turn off; they also redefine entropy symbols.
  list(FILTER AZ_ZSTD_SOURCES EXCLUDE REGEX "/zstd_v0[0-9]+\\.c$")

  file(GLOB AZ_ZSTDMT_SOURCES "${ZSTDMT}/*.c")

  # ------------------------------------------------------------------- lz4
  set(AZ_LZ4_SOURCES
    "${CODECS}/lz4/lz4.c"
    "${CODECS}/lz4/lz4hc.c"
    "${CODECS}/lz4/lz4frame.c"
  )

  # ------------------------------------------------------------------ lz5
  file(GLOB AZ_LZ5_SOURCES "${CODECS}/lz5/*.c")

  # --------------------------------------------------------------- lizard
  file(GLOB AZ_LIZARD_SOURCES "${CODECS}/lizard/*.c" "${CODECS}/lizard/entropy/*.c")

  # --------------------------------------------------------------- brotli
  file(GLOB AZ_BROTLI_SOURCES
    "${CODECS}/brotli/common/*.c"
    "${CODECS}/brotli/dec/*.c"
    "${CODECS}/brotli/enc/*.c"
  )

  # ------------------------------------------------------------- xxhash
  set(AZ_XXHASH_SOURCES "${CODECS}/xxhash/xxhash.c")

  # ------------------------------------------- 7-Zip wrappers for the above
  #
  # Zstd is a pair, not a single file: stock 7-Zip already ships a decode-only
  # NCompress::NZstd coder that Archive/Zip and Archive/ZstdHandler use, and the
  # fork's NCompress::NZSTD cannot replace it (the build dies on C2039). Both live
  # side by side; vendor-sources.ps1 places the fork's as ZstdFork* so the file
  # names do not collide either. Only the fork pair is listed here -- the stock
  # decoder is already part of the engine source list.
  #
  # The zstd *library* is compiled below because these wrappers call it, and
  # because zstdmt/ hosts the shared multithreaded wrappers (lz4-mt / lz5-mt /
  # lizard-mt / brotli-mt) that the four codecs here depend on.
  set(AZ_GLUE_SOURCES
    "${COMPR}/ZstdForkDecoder.cpp" "${COMPR}/ZstdForkEncoder.cpp" "${COMPR}/ZstdForkRegister.cpp"
    "${COMPR}/Lz4Decoder.cpp"    "${COMPR}/Lz4Encoder.cpp"    "${COMPR}/Lz4Register.cpp"
    "${COMPR}/Lz5Decoder.cpp"    "${COMPR}/Lz5Encoder.cpp"    "${COMPR}/Lz5Register.cpp"
    "${COMPR}/LizardDecoder.cpp" "${COMPR}/LizardEncoder.cpp" "${COMPR}/LizardRegister.cpp"
    "${COMPR}/BrotliDecoder.cpp" "${COMPR}/BrotliEncoder.cpp" "${COMPR}/BrotliRegister.cpp"
    "${ARCH}/Lz4Handler.cpp"     "${ARCH}/Lz5Handler.cpp"
    "${ARCH}/LizardHandler.cpp"  "${ARCH}/BrotliHandler.cpp"
  )

  foreach(_src IN LISTS AZ_GLUE_SOURCES)
    if(NOT EXISTS "${_src}")
      # Without this the build dies hundreds of compiles later on a missing
      # header, which reads like a broken vendor run rather than a skipped one.
      message(FATAL_ERROR
        "ANDZIP_EXTRA_CODECS=ON but ${_src} is not vendored. The 7-Zip codec "
        "wrappers are only placed into the engine tree by the extra-codecs "
        "vendor mode -- re-run:\n"
        "    pwsh native/scripts/vendor-sources.ps1 -ExtraCodecs\n"
        "then: cmake -B <build> -DANDZIP_EXTRA_CODECS=ON")
    endif()
  endforeach()

  foreach(_src IN LISTS AZ_LZ4_SOURCES AZ_LZ5_SOURCES AZ_XXHASH_SOURCES)
    if(NOT EXISTS "${_src}")
      message(WARNING "andzip_core: extra codec source missing: ${_src} "
                      "(re-run native/scripts/vendor-sources.ps1)")
    endif()
  endforeach()

  target_sources(${target} PRIVATE
    ${AZ_ZSTD_SOURCES}
    ${AZ_ZSTDMT_SOURCES}
    ${AZ_LZ4_SOURCES}
    ${AZ_LZ5_SOURCES}
    ${AZ_LIZARD_SOURCES}
    ${AZ_BROTLI_SOURCES}
    ${AZ_XXHASH_SOURCES}
    ${AZ_GLUE_SOURCES}
  )

  target_include_directories(${target} PRIVATE
    "${ZSTD_SHIM_ROOT}"
    "${ZSTD}"
    "${ZSTDMT}"
    "${CODECS}/lz4"
    "${CODECS}/lz5"
    "${CODECS}/lizard"
    "${CODECS}/lizard/entropy"
    "${CODECS}/brotli"
    "${CODECS}/brotli/include"
    # zstdmt/brotli-mt_*.c include "encode.h" and "decode.h" bare -- written
    # against the fork's flat C/brotli/ layout. Our vendored brotli keeps the
    # upstream tree, so expose its public header directory by name as well.
    "${CODECS}/brotli/include/brotli"
    "${CODECS}/xxhash"
  )

  target_compile_definitions(${target} PRIVATE
    ZSTD_MULTITHREAD=1
    ZSTD_LEGACY_SUPPORT=0
    ZSTD_DISABLE_ASM=1
    LZ4_STATIC_LINKING_ONLY
    LZ4_DISABLE_DEPRECATE_WARNINGS
    XXH_STATIC_LINKING_ONLY
    XXH_PRIVATE_API=0
  )

  # The wrapper .cpp files are C++ but sit alongside the engine sources; give
  # them the same treatment rather than the stricter engine flags.
  if(MSVC)
    set_source_files_properties(${AZ_GLUE_SOURCES} PROPERTIES COMPILE_OPTIONS "/wd4996;/wd4244")
  endif()

  message(STATUS "andzip_core: extra codecs ON -- zstd(${CMAKE_SIZEOF_VOID_P}) lz4 lz5 lizard brotli")
endfunction()

# ------------------------------------------------------------------ zlib-ng
#
# Stage P2-c (docs/压缩提速-执行流程.md section 6, and the P2-b follow-up in
# docs/perf-p2b-libdeflate.md): the Deflate *encoder* comes from zlib-ng, while
# the decoder stays with the vendor. zlib-ng is the library that has the API
# shape the ZIP entry coder needs -- Z_SYNC_FLUSH and deflateSetDictionary are
# public, which is what docs/perf-p1a-deflate-mt.md named as the criterion.
# libdeflate measured faster per byte but has no streaming compression at all,
# so it was rejected without vendoring it.
#
# This is a vendored CMake project rather than a hand-listed set of sources,
# on purpose: its arch selection, per-file instruction-set flags and the two
# generated headers (zlib.h / zconf.h from *.in) are platform logic we would
# have to re-derive for every target we build (Windows shared, Windows static,
# Linux via Zig). Re-deriving it wrong produces a library that compiles and
# runs slower, or not at all, on the target nobody tested.
function(az_add_zlibng_deflate target)
  set(ZNG "${AZ_CODEC_ROOT}/zlib-ng")
  if(NOT EXISTS "${ZNG}/CMakeLists.txt")
    message(FATAL_ERROR
      "ANDZIP_DEFLATE_ZLIBNG=ON but ${ZNG} is not vendored. Re-run:\n"
      "    pwsh native/scripts/vendor-sources.ps1 -ZlibNg <checkout of 1727258b>\n"
      "or restore the tracked files with:\n"
      "    git checkout -- native/third_party/codecs/zlib-ng")
  endif()

  # Their options are cache variables, so they have to be written before the
  # subdirectory is read -- and forced, because our own -DANDZIP_* defaults would
  # otherwise lose to whatever a stale cache holds.
  set(ZLIB_COMPAT          ON  CACHE BOOL "" FORCE)   # zlib names/ABI, what our wrapper calls
  set(BUILD_SHARED_LIBS    OFF CACHE BOOL "" FORCE)   # one DLL ships; no second zlib DLL
  set(BUILD_STATIC_LIBS    ON  CACHE BOOL "" FORCE)
  set(BUILD_TESTING        OFF CACHE BOOL "" FORCE)   # ON makes them FetchContent GoogleTest
  set(INSTALL_UTILS        OFF CACHE BOOL "" FORCE)   # minigzip and friends are not ours to ship
  set(WITH_MAINTAINER_WARNINGS OFF CACHE BOOL "" FORCE)
  set(WITH_GTEST           OFF CACHE BOOL "" FORCE)
  set(WITH_FUZZERS         OFF CACHE BOOL "" FORCE)
  set(WITH_BENCHMARKS      OFF CACHE BOOL "" FORCE)

  # EXCLUDE_FROM_ALL also keeps their utils/ targets (minigzip, makefixed,
  # maketrees -- add_subdirectory(utils) is unconditional in their build) out of
  # the default build, so a product build does not start emitting executables
  # nobody asked for into the output directory.
  add_subdirectory("${ZNG}" "${CMAKE_CURRENT_BINARY_DIR}/zlib-ng" EXCLUDE_FROM_ALL)

  # The name exists in both shapes (real target when shared is off, alias when it
  # is on), which is why it is the one to link.
  target_link_libraries(${target} PRIVATE zlib-ng-static)
  target_compile_definitions(${target} PRIVATE AZ_DEFLATE_ZLIBNG=1)

  # Our two files, not vendored: the coder that speaks 7-Zip's ICompressCoder over
  # deflate(), and the one place that edits the codec table. They are listed here
  # rather than in CMakeLists.txt so an engine built without this switch cannot
  # contain a reference to zlib.h at all.
  target_sources(${target} PRIVATE
    "${AZ_NATIVE_ROOT}/src/codecs/az_deflate_encoder.cpp"
    "${AZ_NATIVE_ROOT}/src/codecs/az_deflate_swap.cpp"
  )

  get_target_property(_zng_src zlib-ng-static SOURCE_DIR)
  message(STATUS "andzip_core: deflate encoder from zlib-ng (${_zng_src})")
endfunction()
