// ops.h -- everything the GUI does through the andzip C ABI, with no windowing
// code. Keeping it separate means the ABI usage is reviewable on its own and
// the dialog procedures stay concerned only with controls.
//
// Every entry point is blocking and intended to run on a worker thread: the
// engine can take a long time on big archives. The progress callback may be
// invoked from the coder's own threads, so callers must marshal to the UI
// thread rather than touching controls directly.

#pragma once

#include <functional>
#include <string>
#include <vector>

namespace azgui {

// Reports one step of work. `total` is 0 when the engine has not announced a
// size yet.
struct Progress
{
  std::wstring entry;
  unsigned long long done;
  unsigned long long total;
};

using ProgressFn = std::function<void(const Progress &)>;

// What to build and how. Paths and names stay wide here; they are converted to
// the ABI's UTF-8 only at the call boundary.
struct Job
{
  std::wstring archivePath;
  std::wstring sourcePath;    // file or directory
  std::string  format;        // "7z" | "zip" | "tar" | "gz" | "bz2" | "xz"
  std::string  method;        // "" = format default
  int          level = -1;    // -1 = default, else 0..9
  std::string  password;
  bool         encryptHeaders = false;
  bool         solid = true;
  bool         recurse = true;
};

struct Entry
{
  unsigned long long index = 0;
  std::wstring  name;
  std::wstring  method;
  unsigned long long size = 0;
  unsigned long long packedSize = 0;
  unsigned        crc = 0;
  bool            isDir = false;
  bool            encrypted = false;
  bool            hasSize = false;
  bool            hasPackedSize = false;
  long long       mtime = 0;
};

// Engine and build facts for the About/status line.
struct BuildInfo
{
  std::wstring coreVersion;
  std::wstring engine;
  std::wstring config;
  unsigned     abiVersion = 0;
  unsigned     formatCount = 0;
  unsigned     codecCount = 0;
  bool         zstdEncoder = false;
  bool         lz4Encoder = false;
  bool         brotliEncoder = false;
};

// Formats that are known-good to offer in the UI, with the label shown to the
// user. Anything the engine reports as writable is also accepted.
std::vector<std::pair<std::wstring, std::string>>WritableFormats();

// Compression methods that the given format accepts, by engine method name.
// Empty means the format has a single method and the UI should leave the choice
// at its default.
std::vector<std::string> MethodsFor(const std::string &format);

BuildInfo QueryBuild();

// All readable formats, one human-readable line each, for the formats dialog.
std::vector<std::wstring> ListAllFormats();
std::vector<std::wstring> ListAllCodecs();

// Operations. Each returns false and fills `error` (already localised to a
// human-readable UTF-16 string, generally from az_last_error_message) on
// failure.
bool Compress(const Job &, const ProgressFn &, std::wstring &error);
bool Extract(const std::wstring &archive, const std::wstring &destDir,
             const std::string &password, bool testOnly,
             const ProgressFn &, std::wstring &error);
std::vector<Entry> ListEntries(const std::wstring &archive,
                               const std::string &password, std::wstring &error);

// Compresses a scratch buffer with every requested method and checks it comes
// back identical. Needs no user files, so it is the fastest "is this build sane"
// answer. `report` collects one line per case.
bool SelfTest(std::wstring &report, std::wstring &error);

// Verifies the archive `job.archivePath` that already exists: extract it into a
// scratch directory and compare every member byte for byte against the source it
// came from. `report` describes the comparison.
bool RoundTripFile(const Job &, std::wstring &report, std::wstring &error);

// UTF-8 <-> UTF-16 helpers shared with the dialog code.
std::string  ToUtf8(const std::wstring &s);
std::wstring ToWide(const char *utf8);

} // namespace azgui
