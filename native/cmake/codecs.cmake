# Extra coders vendored from cankao/NanaZip-main.zip and cankao/7-Zip-zstd-master.zip.
#
# Off by default (ANDZIP_EXTRA_CODECS). When enabled this adds encoders and
# decoders that stock 7-Zip does not ship: Zstandard, LZ4, LZ5, Lizard and
# Brotli, plus the multithreaded wrappers from zstdmt. Decoding Zstd works
# either way -- third_party/7zip/C/ZstdDec.c is part of the engine.
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

function(az_add_extra_codecs target)
  set(CODECS "${AZ_CODEC_ROOT}")
  set(ZSTD   "${AZ_7ZIP_ROOT}/C/zstd")
  set(ZSTDMT "${AZ_7ZIP_ROOT}/C/zstdmt")
  set(COMPR  "${AZ_7ZIP_ROOT}/CPP/7zip/Compress")
  set(ARCH   "${AZ_7ZIP_ROOT}/CPP/7zip/Archive")

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
  # Zstd is deliberately NOT in this list. Its 7-Zip glue (ZstdEncoder /
  # ZstdRegister / the fork's ZstdHandler) ships in cankao/7-Zip-zstd-master.zip,
  # which is no longer on disk, and the copy in NanaZip lives one layer too high
  # to drop in: NanaZip.Core/Extensions/ZSCodecs/ZstdEncoder.hpp is
  # `namespace NanaZip::Core::Extensions`, not `NCompress::NZstd`, so it cannot
  # register as an engine codec. Zstandard therefore stays decode-only, served by
  # the stock third_party/7zip/C/ZstdDec.c -- exactly as before this option.
  # The zstd *library* is still compiled below because zstdmt/ hosts the shared
  # multithreaded wrappers (lz4-mt / lz5-mt / lizard-mt / brotli-mt) that the
  # four codecs here do depend on.
  set(AZ_GLUE_SOURCES
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
