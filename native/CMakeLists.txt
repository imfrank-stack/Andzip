# libandzip_core -- the native compression engine behind Andzip.
#
# Layers, bottom up:
#   third_party/7zip     the 7-Zip engine, compiled verbatim (see cmake/7zip-sources.cmake)
#   third_party/codecs   optional extra coders (zstd / lz4 / brotli / lizard), off by default
#   src/api              the flat C ABI declared in include/andzip/andzip.h
#
# Typical use:
#   cmake -S native -B build/native -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build build/native --target andzip_core
#
# Nothing outside native/ is referenced; the existing C# projects keep building
# exactly as before until they choose to take a dependency on Andzip.Native.

cmake_minimum_required(VERSION 3.20)

project(andzip_native
        VERSION 1.0.0
        DESCRIPTION "Andzip cross-platform compression core"
        LANGUAGES C CXX)

# ------------------------------------------------------------------- options

set(ANDZIP_BUILD_SHARED   ON  CACHE BOOL "Build andzip_core as a shared library")
set(ANDZIP_EXTRA_CODECS   OFF CACHE BOOL "Compile zstd/lz4/brotli/lizard encoders from third_party/codecs")
set(ANDZIP_DEFLATE_ZLIBNG OFF CACHE BOOL "Take the Deflate encoder from third_party/codecs/zlib-ng (stage P2-c)")
set(ANDZIP_BUILD_TOOLS    ON  CACHE BOOL "Build the azcore command line smoke tool")
set(ANDZIP_BUILD_TESTS    ON  CACHE BOOL "Build the native test executable")
set(ANDZIP_BUILD_GUI      ON  CACHE BOOL "Build the azgui Win32 verification UI (Windows only)")
set(ANDZIP_WERROR         OFF CACHE BOOL "Treat warnings as errors")
set(ANDZIP_NATIVE_OUTDIR  ""  CACHE PATH "Where to place the built library (defaults to the CMake binary dir)")

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# 7-Zip's own build uses /MT; a DLL consumed from .NET must share the host CRT.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")

if(NOT ANDZIP_NATIVE_OUTDIR)
  set(ANDZIP_NATIVE_OUTDIR "${CMAKE_BINARY_DIR}/bin")
endif()
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${ANDZIP_NATIVE_OUTDIR}")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${ANDZIP_NATIVE_OUTDIR}")
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")

# ------------------------------------------------------------- source lists

include("${CMAKE_CURRENT_LIST_DIR}/cmake/7zip-sources.cmake")

set(AZ_API_SOURCES
  src/api/az_error.cpp
  src/api/az_util.cpp
  src/api/az_formats.cpp
  src/api/az_streams.cpp
  src/api/az_reader.cpp
  src/api/az_writer.cpp
  src/api/az_hash.cpp
  src/api/az_exports.cpp
)

set(AZ_API_HEADERS
  include/andzip/andzip.h
  src/api/az_internal.h
  src/api/az_pch.h
)

# The engine expects MyWindows.cpp only on non-Windows: there it supplies the
# GUID / PROPVARIANT / BSTR emulation that the Win32 headers provide elsewhere.
set(AZ_PLATFORM_GLUE_SOURCES "")
if(NOT WIN32)
  list(APPEND AZ_PLATFORM_GLUE_SOURCES "${AZ_7ZIP_ROOT}/CPP/Common/MyWindows.cpp")
endif()

# --------------------------------------------------------------- 7-Zip flags

# Mirrors CPP/Build.mak, minus /Wall /WX and /Gr: the latter switches every
# function to __fastcall, which buys nothing here and would have to be matched
# by callers of the C ABI.
add_library(az_engine_flags INTERFACE)

target_include_directories(az_engine_flags INTERFACE ${AZ_7ZIP_INCLUDE_DIRS})

target_compile_definitions(az_engine_flags INTERFACE
  $<$<BOOL:${ANDZIP_EXTRA_CODECS}>:AZ_EXTRA_CODECS=1>
)

if(WIN32)
  # No WIN32_LEAN_AND_MEAN and no NOMINMAX here, deliberately: 7-Zip's headers
  # need the OLE declarations that lean-and-mean strips out (LPCOLESTR, PROPID,
  # STDMETHOD) and its sources expect the windows.h min/max macros. This matches
  # CPP/Build.mak, which defines neither.
  target_compile_definitions(az_engine_flags INTERFACE
    UNICODE _UNICODE
    _CRT_SECURE_NO_WARNINGS
    _CRT_NONSTDC_NO_DEPRECATE
    $<$<STREQUAL:"${CMAKE_SIZEOF_VOID_P}","8">:_WIN64>
  )
  target_compile_options(az_engine_flags INTERFACE
    /EHsc /GR- /GS- /Zc:wchar_t /Zc:throwingNew /wd4996 /wd4267 /wd4244 /wd4100 /wd4706
  )
else()
  # These are the macros CPP/7zip/7zip_gcc.mak and C/7zip_gcc_c.mak pass on
  # POSIX. 7-Zip's portability is driven by _WIN32 rather than a project-specific
  # define, so nothing else needs to be set; the file-offset ones matter because
  # the engine stats archives through NWindows::NFile and needs 64-bit sizes.
  target_compile_definitions(az_engine_flags INTERFACE
    _FILE_OFFSET_BITS=64
    _LARGEFILE_SOURCE
    _REENTRANT
    _GNU_SOURCE
  )
  target_compile_options(az_engine_flags INTERFACE
    -fno-rtti -fno-strict-aliasing
    -Wno-unknown-pragmas -Wno-unused-parameter -Wno-missing-field-initializers
    -Wno-address -Wno-comment -Wno-format -Wno-unused-variable
  )
endif()

if(ANDZIP_WERROR)
  target_compile_options(az_engine_flags INTERFACE $<$<C_COMPILER_ID:MSVC>:/WX> $<$<NOT:$<C_COMPILER_ID:MSVC>>:-Werror>)
endif()

# ------------------------------------------------------------------ core lib

if(ANDZIP_BUILD_SHARED)
  add_library(andzip_core SHARED)
  target_compile_definitions(andzip_core PRIVATE ANDZIP_CORE_BUILDING)
else()
  add_library(andzip_core STATIC)
  target_compile_definitions(andzip_core PUBLIC ANDZIP_CORE_STATIC)
  # 7-Zip discovers its archive handlers and codecs from global constructors in
  # ArchiveExports.cpp and CodecExports.cpp, and nothing references those objects
  # by symbol. A static link therefore drops them, and the library loads with an
  # empty registry (az_format_count() == 0), which fails almost every test.
  # Whole-archive pulls in all 293 objects, which is what the shared build already
  # does by construction.
  if(MSVC)
    target_link_options(andzip_core INTERFACE "/WHOLEARCHIVE:$<TARGET_FILE:andzip_core>")
  endif()
  # ELF consumers need the same treatment and CMake cannot express it portably:
  #   -Wl,--whole-archive libandzip_core.a -Wl,--no-whole-archive
endif()

target_sources(andzip_core PRIVATE
  ${AZ_7ZIP_ENGINE_SOURCES}
  ${AZ_PLATFORM_GLUE_SOURCES}
  ${AZ_API_SOURCES}
  ${AZ_API_HEADERS}
)

target_include_directories(andzip_core
  PUBLIC  "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>"
  PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src"
)

target_link_libraries(andzip_core PRIVATE az_engine_flags)

set_target_properties(andzip_core PROPERTIES
  OUTPUT_NAME "andzip_core"
  C_VISIBILITY_PRESET hidden
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN ON
  DEFINE_SYMBOL "ANDZIP_CORE_BUILDING"
)

# File name by platform: the shell probes "andzip_core.dll" on Windows and
# "libandzip_core.so" / "libandzip_core.dylib" elsewhere (see
# src/Andzip.Native/Interop/NativeLibraryResolver.cs). Clearing the prefix
# unconditionally used to emit "andzip_core.so", which no Linux or macOS resolver
# would ever load -- the engine would simply report itself unavailable.
if(WIN32)
  set_target_properties(andzip_core PROPERTIES PREFIX "")
endif()

target_compile_definitions(andzip_core PRIVATE
  ANDZIP_CORE_VERSION="${PROJECT_VERSION}"
  ANDZIP_BUILD_CONFIG="$<CONFIG>"
)

if(WIN32)
  # SysAllocString / VariantClear (oleaut32), CLSID helpers (ole32) and the
  # shell/file APIs 7-Zip's Windows layer calls into.
  target_link_libraries(andzip_core PRIVATE oleaut32 ole32 user32 advapi32 shell32)
  if(MINGW)
    target_link_options(andzip_core PRIVATE -static-libgcc -static-libstdc++)
  endif()
else()
  find_package(Threads REQUIRED)
  target_link_libraries(andzip_core PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
  if(ANDZIP_BUILD_SHARED)
    # The default for ELF is to let undefined symbols escape into a "will be
    # resolved at load time" shared object, which turns a missing engine source
    # file into a crash on the user's machine. The cross builds are not runnable
    # from this host, so catch that class of error at link time instead.
    target_link_options(andzip_core PRIVATE "LINKER:--no-undefined")
  endif()
endif()

if(MSVC)
  # 7-Zip allocates multi-MiB dictionaries on the stack in a few hot paths;
  # the upstream build raises the reserve to 8 MiB and we follow suit.
  target_link_options(andzip_core PRIVATE "/STACK:8388608")
endif()

if(ANDZIP_EXTRA_CODECS)
  include("${CMAKE_CURRENT_LIST_DIR}/cmake/codecs.cmake")
  az_add_extra_codecs(andzip_core)
endif()

if(ANDZIP_DEFLATE_ZLIBNG)
  # The zlib license has no clause that conflicts with the LGPL route already
  # documented for the engine, but it does require the licence text to ship, so
  # the license check below has to see it.
  include("${CMAKE_CURRENT_LIST_DIR}/cmake/codecs.cmake")
  az_add_zlibng_deflate(andzip_core)
endif()

# -------------------------------------------------------------------- tools

if(ANDZIP_BUILD_TOOLS)
  add_executable(azcore tools/azcore/main.cpp)
  target_link_libraries(azcore PRIVATE andzip_core)
  target_include_directories(azcore PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
  set_target_properties(azcore PROPERTIES CXX_STANDARD 17)
  if(MSVC)
    target_compile_options(azcore PRIVATE /EHsc)
  endif()
endif()

# ---------------------------------------------------------------- azgui (GUI)

# The manual verification UI. Win32 only by construction: it is plain User32 and
# it exists so the compression side can be driven by hand on Windows.
if(ANDZIP_BUILD_GUI)
  if(WIN32)
    add_executable(azgui WIN32
      tools/azgui/main.cpp
      tools/azgui/ops.cpp
      tools/azgui/ops.h
      tools/azgui/app.manifest
    )
    target_link_libraries(azgui PRIVATE andzip_core)
    target_include_directories(azgui PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
    # The GUI is written against the wide Win32 APIs throughout, so the TCHAR
    # mappings have to resolve to W; without this LVITEM is the ANSI struct.
    target_compile_definitions(azgui PRIVATE UNICODE _UNICODE _WIN32_WINNT=0x0601)
    set_target_properties(azgui PROPERTIES CXX_STANDARD 17)
    if(MSVC)
      # /utf-8 so the Chinese UI literals in main.cpp are decoded as UTF-8 source
      # rather than the system ANSI codepage.
      target_compile_options(azgui PRIVATE /EHsc /utf-8)
      target_link_libraries(azgui PRIVATE comctl32 ole32 shell32 user32 gdi32)
    endif()
  else()
    message(STATUS "azgui: skipped (Win32 only)")
  endif()
endif()

if(ANDZIP_BUILD_TESTS)
  enable_testing()
  add_executable(andzip_core_tests tests/native/test_main.cpp)
  target_link_libraries(andzip_core_tests PRIVATE andzip_core)
  target_include_directories(andzip_core_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
  set_target_properties(andzip_core_tests PROPERTIES CXX_STANDARD 17)
  if(MSVC)
    target_compile_options(andzip_core_tests PRIVATE /EHsc)
  endif()
  add_test(NAME andzip_core_tests COMMAND andzip_core_tests)
endif()

# ------------------------------------------------------------- C# hand-off

# The managed shell resolves the native library out of
# runtimes/<rid>/native/, so drop a copy there next to the build output.
set(AZ_RID_HINT "" CACHE STRING "Runtime identifier to stage the library under (e.g. win-x64)")
if(AZ_RID_HINT)
  add_custom_command(TARGET andzip_core POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E make_directory
            "${ANDZIP_NATIVE_OUTDIR}/runtimes/${AZ_RID_HINT}/native"
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
            "$<TARGET_FILE:andzip_core>"
            "${ANDZIP_NATIVE_OUTDIR}/runtimes/${AZ_RID_HINT}/native/$<TARGET_FILE_NAME:andzip_core>"
    COMMENT "Staging $<TARGET_FILE_NAME:andzip_core> for ${AZ_RID_HINT}")
endif()

message(STATUS "andzip_core: ${PROJECT_VERSION}  shared=${ANDZIP_BUILD_SHARED}  extra_codecs=${ANDZIP_EXTRA_CODECS}  deflate=${ANDZIP_DEFLATE_ZLIBNG}")
list(LENGTH AZ_7ZIP_ENGINE_SOURCES AZ_ENGINE_TU_COUNT)
message(STATUS "andzip_core: ${AZ_ENGINE_TU_COUNT} engine translation units from ${AZ_7ZIP_ROOT}")
