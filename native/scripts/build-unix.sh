#!/usr/bin/env bash
#
# Builds libandzip_core on Linux and macOS.
#
# The vendored 7-Zip engine is portable (its POSIX support is driven by the
# absence of _WIN32, see CPP/7zip/7zip_gcc.mak), so this is a plain CMake
# configure-and-build. Output lands in build/native/bin, with a copy staged under
# src/Andzip.Native/runtimes/<rid>/native/ for the C# shell (same contract as
# build-windows.ps1, so "dotnet build" finds the engine on any host).
#
# Usage:
#   native/scripts/build-unix.sh                     # Release, shared, tests on
#   native/scripts/build-unix.sh -C Debug -T         # Debug and run the tests
#   native/scripts/build-unix.sh --extra-codecs      # add zstd/lz4/brotli encoders
#   native/scripts/build-unix.sh --static --no-tools
#   AR=aarch64-linux-gnu-gcc CC=aarch64-linux-gnu-gcc native/scripts/build-unix.sh
#
# NOTE: the Linux leg now runs in CI (.github/workflows/ci.yml, job "linux"),
# where the built library is executed rather than only compiled. macOS is still a
# probe: that job is continue-on-error until someone has read its first run.

set -euo pipefail

usage() {
  sed -n '3,20p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

CONFIG=Release
BUILD_ROOT=""
EXTRA_CODECS=OFF
BUILD_SHARED=ON
BUILD_TOOLS=ON
RUN_TESTS=OFF
JOBS=""
GENERATOR=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    -C|--config)   CONFIG="$2"; shift 2 ;;
    -o|--output)   BUILD_ROOT="$2"; shift 2 ;;
    -G|--generator) GENERATOR="$2"; shift 2 ;;
    -j|--jobs)     JOBS="$2"; shift 2 ;;
    --extra-codecs) EXTRA_CODECS=ON; shift ;;
    --static)      BUILD_SHARED=OFF; shift ;;
    --no-tools)    BUILD_TOOLS=OFF; shift ;;
    -T|--test)     RUN_TESTS=ON; shift ;;
    -h|--help)     usage 0 ;;
    *) echo "unknown option: $1" >&2; usage 2 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NATIVE_DIR="$(dirname "$SCRIPT_DIR")"
REPO_ROOT="$(dirname "$NATIVE_DIR")"
[[ -n "$BUILD_ROOT" ]] || BUILD_ROOT="$REPO_ROOT/build/native"

if ! command -v cmake >/dev/null 2>&1; then
  echo "cmake not found. Install it first:" >&2
  echo "  Debian/Ubuntu: sudo apt install cmake ninja-build build-essential" >&2
  echo "  Fedora:        sudo dnf install cmake ninja gcc-c++" >&2
  echo "  macOS:         brew install cmake ninja" >&2
  exit 1
fi

# Prefer Ninja when it is available; Unix Makefiles is the fallback so a bare
# toolchain still builds.
if [[ -z "$GENERATOR" ]]; then
  if command -v ninja >/dev/null 2>&1; then
    GENERATOR=Ninja
  else
    GENERATOR="Unix Makefiles"
  fi
fi

case "$(uname -s)" in
  Linux)
    case "$(uname -m)" in
      x86_64)  RID="linux-x64" ;;
      aarch64) RID="linux-arm64" ;;
      armv7l)  RID="linux-arm" ;;
      *)       RID="linux-$(uname -m)" ;;
    esac
    LIBPREFIX="lib"; LIBSUFFIX="so"
    ;;
  Darwin)
    case "$(uname -m)" in
      x86_64) RID="osx-x64" ;;
      arm64)  RID="osx-arm64" ;;
      *)      RID="osx-$(uname -m)" ;;
    esac
    LIBPREFIX="lib"; LIBSUFFIX="dylib"
    ;;
  MINGW*|MSYS*|CYGWIN*)
    RID="win-x64"; LIBPREFIX=""; LIBSUFFIX="dll"
    echo "This script targets Linux/macOS; use native/scripts/build-windows.ps1 for MSVC." >&2
    echo "Continuing anyway with the host compiler." >&2
    ;;
  *)
    echo "unsupported platform: $(uname -s)" >&2
    exit 1
    ;;
esac

CMAKE_ARGS=(
  -S "$NATIVE_DIR"
  -B "$BUILD_ROOT"
  -G "$GENERATOR"
  -DCMAKE_BUILD_TYPE="$CONFIG"
  -DAZ_RID_HINT="$RID"
  -DANDZIP_EXTRA_CODECS="$EXTRA_CODECS"
  -DANDZIP_BUILD_SHARED="$BUILD_SHARED"
  -DANDZIP_BUILD_TOOLS="$BUILD_TOOLS"
  -DANDZIP_BUILD_TESTS=ON
)

echo "configuring: $GENERATOR / $CONFIG / $RID"
cmake "${CMAKE_ARGS[@]}"

BUILD_ARGS=(--build "$BUILD_ROOT" --config "$CONFIG")
if [[ -n "$JOBS" ]]; then
  BUILD_ARGS+=(--parallel "$JOBS")
elif command -v nproc >/dev/null 2>&1; then
  BUILD_ARGS+=(--parallel "$(nproc)")
else
  BUILD_ARGS+=(--parallel)
fi

cmake "${BUILD_ARGS[@]}" --target andzip_core
if [[ "$BUILD_TOOLS" == "ON" ]]; then
  cmake "${BUILD_ARGS[@]}" --target azcore
fi
cmake "${BUILD_ARGS[@]}" --target andzip_core_tests

echo
echo "artifacts in $BUILD_ROOT/bin:"
find "$BUILD_ROOT/bin" -maxdepth 1 -type f \( -name "*${LIBSUFFIX}" -o -name "azcore" \
  -o -name "andzip_core_tests" \) -exec ls -lh {} \; 2>/dev/null | awk '{printf "  %-28s %s\n", $NF, $5}'

LIBNAME="${LIBPREFIX}andzip_core.${LIBSUFFIX}"

# Stage for the C# shell, which probes <shell>/runtimes/<rid>/native/<libname>.
SHELL_RUNTIMES="$REPO_ROOT/src/Andzip.Native/runtimes/$RID/native"
if [[ -f "$BUILD_ROOT/bin/$LIBNAME" ]]; then
  mkdir -p "$SHELL_RUNTIMES"
  cp -f "$BUILD_ROOT/bin/$LIBNAME" "$SHELL_RUNTIMES/"
  echo "staged engine -> $SHELL_RUNTIMES/$LIBNAME"
fi

if [[ -f "$BUILD_ROOT/bin/$LIBNAME" ]]; then
  echo
  echo "engine version:"
  "$BUILD_ROOT/bin/azcore" version 2>/dev/null | sed 's/^/  /' || \
    echo "  (azcore could not be run -- check that $LIBNAME is next to it)"
fi

if [[ "$RUN_TESTS" == "ON" ]]; then
  echo
  echo "running native tests"
  "$BUILD_ROOT/bin/andzip_core_tests"
fi
