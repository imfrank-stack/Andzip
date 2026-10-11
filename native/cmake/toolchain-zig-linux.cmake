# Cross-compile for Linux using Zig as the compiler driver.
#
# Zig bundles clang plus libc/libc++ for many targets, so one download gives a
# working cross toolchain for glibc, musl and arm64 without installing anything
# system-wide. This is how the POSIX code path gets built and exercised from a
# Windows host.
#
#   cmake -S native -B build/linux-x64 -G Ninja \
#         -DCMAKE_TOOLCHAIN_FILE=native/cmake/toolchain-zig-linux.cmake \
#         -DAZ_ZIG_TARGET=x86_64-linux-gnu
#
#   AZ_ZIG_TARGET   zig target triple  (default x86_64-linux-gnu)
#   AZ_ZIG_EXE      path to the zig driver; searched on PATH when unset
#   AZ_ZIG_LIBC     "" (gnu default) | musl | musl.0.9_36 ...

if(NOT DEFINED AZ_ZIG_TARGET)
  set(AZ_ZIG_TARGET "x86_64-linux-gnu" CACHE STRING "Zig target triple")
endif()

find_program(AZ_ZIG_EXE_PROBE NAMES zig zig.exe)

# Resolution order: -DAZ_ZIG_EXE=..., then $ENV{AZ_ZIG_EXE}, then PATH.
#
# The environment entry is not a nicety. This file is re-included by every
# try_compile scratch project CMake creates during compiler detection, and those
# do NOT inherit arbitrary user cache entries -- so a -D-only path resolves fine
# at configure time and then fails deep inside detection. $ENV{} does propagate,
# which is why AZ_ZIG_EXE is honoured from the environment first.
set(_az_zig "$ENV{AZ_ZIG_EXE}")
if(NOT _az_zig OR NOT EXISTS "${_az_zig}")
  set(_az_zig "${AZ_ZIG_EXE}")
endif()
if(NOT _az_zig OR NOT EXISTS "${_az_zig}")
  set(_az_zig "${AZ_ZIG_EXE_PROBE}")
endif()
if(NOT _az_zig OR NOT EXISTS "${_az_zig}")
  message(FATAL_ERROR
    "Zig not found. Install it (native/scripts/provision-toolchain.ps1 fetches a "
    "portable copy) and set AZ_ZIG_EXE (environment variable, or -DAZ_ZIG_EXE=...) "
    "to the zig executable.")
endif()
get_filename_component(_az_zig "${_az_zig}" ABSOLUTE)

# See the note above: FORCE is what lets the values below survive the
# re-include, since the cache write itself may not.
set(AZ_ZIG_EXE "${_az_zig}" CACHE FILEPATH "Resolved zig driver" FORCE)
set(ENV{AZ_ZIG_EXE} "${_az_zig}")

set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_VERSION 1)

# CMAKE_SYSTEM_PROCESSOR has to match what the target actually is, or 7-Zip's
# CpuArch.h picks the wrong byte-order and alignment branches.
if(AZ_ZIG_TARGET MATCHES "^x86_64")
  set(CMAKE_SYSTEM_PROCESSOR x86_64)
elseif(AZ_ZIG_TARGET MATCHES "^aarch64")
  set(CMAKE_SYSTEM_PROCESSOR aarch64)
elseif(AZ_ZIG_TARGET MATCHES "^i[3-6]86")
  set(CMAKE_SYSTEM_PROCESSOR i686)
elseif(AZ_ZIG_TARGET MATCHES "^armv?7")
  set(CMAKE_SYSTEM_PROCESSOR armv7)
elseif(AZ_ZIG_TARGET MATCHES "^riscv64")
  set(CMAKE_SYSTEM_PROCESSOR riscv64)
elseif(AZ_ZIG_TARGET MATCHES "^powerpc64le")
  set(CMAKE_SYSTEM_PROCESSOR ppc64le)
else()
  set(CMAKE_SYSTEM_PROCESSOR "${AZ_ZIG_TARGET}")
endif()

# "zig cc" / "zig c++" are wrappers: the first argument selects the mode, so it
# has to lead every invocation including the link step.
set(CMAKE_C_COMPILER   "${AZ_ZIG_EXE}" CACHE FILEPATH "zig cc driver")
set(CMAKE_C_COMPILER_ARG1 "cc"          CACHE STRING "")
set(CMAKE_CXX_COMPILER "${AZ_ZIG_EXE}" CACHE FILEPATH "zig c++ driver")
set(CMAKE_CXX_COMPILER_ARG1 "c++"       CACHE STRING "")

set(_az_target_flag "--target=${AZ_ZIG_TARGET}")
if(DEFINED AZ_ZIG_LIBC AND NOT AZ_ZIG_LIBC STREQUAL "")
  string(APPEND _az_target_flag ".${AZ_ZIG_LIBC}")
endif()

set(CMAKE_C_FLAGS_INIT   "${_az_target_flag} -fPIC")
set(CMAKE_CXX_FLAGS_INIT "${_az_target_flag} -fPIC")
set(CMAKE_EXE_LINKER_FLAGS_INIT   "${_az_target_flag}")
set(CMAKE_SHARED_LINKER_FLAGS_INIT "${_az_target_flag}")
set(CMAKE_MODULE_LINKER_FLAGS_INIT "${_az_target_flag}")

# Zig ships binutils as subcommands ("zig ar", "zig ranlib"), but CMake requires
# CMAKE_AR to name a single executable and puts that path first in every archive
# rule -- a "zig;ar" list comes out as `zig.exe;ar` and cmd hands zig the literal
# token ";ar". Instead of wrapper scripts, the subcommand is injected into the
# archive rules, so <CMAKE_AR> is zig and "ar" is its first argument. The rules
# keep CMake's own placeholders, so long object lists still go through a
# response file, and "ar s" writes the index that ranlib otherwise would.
set(CMAKE_AR     "${AZ_ZIG_EXE}" CACHE FILEPATH "zig (ar subcommand added by the archive rules)")
set(CMAKE_RANLIB "${AZ_ZIG_EXE}" CACHE FILEPATH "zig (unused: 'ar s' writes the index)")
foreach(_az_lang C CXX)
  set(CMAKE_${_az_lang}_ARCHIVE_CREATE "<CMAKE_AR> ar qc <TARGET> <LINK_FLAGS> <OBJECTS>")
  set(CMAKE_${_az_lang}_ARCHIVE_APPEND "<CMAKE_AR> ar qs <TARGET> <LINK_FLAGS> <OBJECTS>")
  set(CMAKE_${_az_lang}_ARCHIVE_FINISH "<CMAKE_AR> ar s <TARGET>")
endforeach()

# Cross builds cannot run their executables here, so skip anything that would
# try, and let find_program/file(READ) use the host sysroot-free paths.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)

# Threads.h threads.h detection needs a real link, which zig can do; keep the
# default TryCompile of type EXECUTABLE so FindThreads sees -pthreads honestly.
message(STATUS "zig toolchain: ${AZ_ZIG_EXE} -> ${AZ_ZIG_TARGET} (${CMAKE_SYSTEM_PROCESSOR})")
