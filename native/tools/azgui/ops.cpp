// ops.cpp -- every andzip ABI call the GUI makes, with no windowing code.
//
// Keeping this separate means the ABI usage can be reviewed on its own, and the
// dialog procedures stay concerned only with controls. Strings that leave here
// are ASCII or UTF-16; the Chinese UI text lives in main.cpp.

#include "ops.h"

#include "andzip/andzip.h"

#include <windows.h>

#include <cstring>
#include <cstdio>
#include <filesystem>

namespace fs = std::filesystem;

namespace azgui {

std::string ToUtf8(const std::wstring &s)
{
  if (s.empty())
    return std::string();
  const int n = ::WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(),
                                      nullptr, 0, nullptr, nullptr);
  if (n <= 0)
    return std::string();
  std::string out((size_t)n, '\0');
  ::WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(), out.data(), n,
                        nullptr, nullptr);
  return out;
}

std::wstring ToWide(const char *utf8)
{
  if (!utf8 || !*utf8)
    return std::wstring();
  const int n = ::MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
  if (n <= 1)
    return std::wstring();
  std::wstring out((size_t)(n - 1), L'\0');
  ::MultiByteToWideChar(CP_UTF8, 0, utf8, -1, out.data(), n);
  return out;
}

namespace {

std::wstring LastError()
{
  std::wstring s = ToWide(az_last_error_message());
  return s.empty() ? L"unknown error" : s;
}

// Carries a managed callback through the ABI's void* context.
struct Bridge
{
  const ProgressFn *fn;
};

az_result OnProgress(void *ctx, const az_progress *p)
{
  Bridge *b = static_cast<Bridge *>(ctx);
  if (b && b->fn && *b->fn)
  {
    Progress out;
    out.entry = p->current_entry ? ToWide(p->current_entry) : std::wstring();
    out.done = p->bytes_in;
    out.total = p->total_in;
    (*b->fn)(out);
  }
  return AZ_OK;
}

// Fills in a callbacks struct whose progress hook runs only when the caller
// actually supplied one.
struct Callbacks
{
  az_callbacks Raw;
  Bridge Br;

  explicit Callbacks(const ProgressFn &fn)
  {
    std::memset(&Raw, 0, sizeof(Raw));
    Raw.struct_size = sizeof(Raw);
    Br.fn = fn ? &fn : nullptr;
    Raw.progress = fn ? OnProgress : nullptr;
    Raw.ctx = &Br;
  }
};

std::wstring WideFromU64(unsigned long long v)
{
  return std::to_wstring(v);
}

// The entry name a user expects: the item's own last path component, so that
// archiving C:\src\proj produces proj\... rather than a flattened pile.
std::wstring BaseName(const std::wstring &path)
{
  size_t cut = path.find_last_of(L"\\/");
  return (cut == std::wstring::npos || cut + 1 >= path.size()) ? path : path.substr(cut + 1);
}

bool ReadAllBytes(const fs::path &p, std::string &out)
{
  std::error_code ec;
  const auto sz = fs::file_size(p, ec);
  if (ec)
    return false;
  FILE *f = _wfopen(p.c_str(), L"rb");
  if (!f)
    return false;
  out.resize((size_t)sz);
  size_t got = out.empty() ? 0 : fread(out.data(), 1, out.size(), f);
  fclose(f);
  if (got != out.size())
  {
    out.clear();
    return false;
  }
  return true;
}

// Deterministic, moderately compressible payload: repeated phrases plus a
// byte-position term so a wrong-length or shifted result cannot pass.
std::string MakePayload(size_t n)
{
  static const char *kPhrases[] = {
    "andzip native compression core self test  ",
    "the quick brown fox jumps over the lazy dog  ",
    "7-Zip engine behind a flat C ABI  ",
  };
  std::string s;
  s.reserve(n);
  size_t i = 0;
  while (s.size() < n)
  {
    s += kPhrases[i % 3];
    s.push_back((char)('A' + (i % 26)));
    if ((i & 7) == 7)
      s.push_back('\n');
    i++;
  }
  s.resize(n);
  for (size_t k = 0; k < s.size(); k += 4096)
    s[k] = (char)(k / 4096);
  return s;
}

} // namespace

// ------------------------------------------------------------------ registry

BuildInfo QueryBuild()
{
  BuildInfo bi;
  az_build_info info;
  std::memset(&info, 0, sizeof(info));
  info.struct_size = sizeof(info);
  if (az_get_build_info(&info) != AZ_OK)
    return bi;
  bi.abiVersion = info.abi_version;
  bi.coreVersion = ToWide(info.core_version);
  bi.engine = ToWide(info.engine);
  bi.config = ToWide(info.build_config);
  bi.formatCount = info.format_count;
  bi.codecCount = info.codec_count;
  bi.zstdEncoder = info.has_zstd_encoder != 0;
  bi.lz4Encoder = info.has_lz4_encoder != 0;
  bi.brotliEncoder = info.has_brotli_encoder != 0;
  return bi;
}

std::vector<std::wstring> ListAllFormats()
{
  std::vector<std::wstring> rows;
  const uint32_t n = az_format_count();
  for (uint32_t i = 0; i < n; i++)
  {
    az_format_info f;
    std::memset(&f, 0, sizeof(f));
    f.struct_size = sizeof(f);
    if (az_format_get(i, &f) != AZ_OK)
      continue;
    std::wstring line = f.name ? ToWide(f.name) : L"?";
    line += L"  ";
    line += f.extension ? ToWide(f.extension) : L"-";
    while (line.size() < 34)
      line += L' ';
    line += f.can_read ? L"read " : L"     ";
    line += f.can_write ? L"write" : L"     ";
    rows.push_back(std::move(line));
    az_format_release(&f);
  }
  return rows;
}

std::vector<std::wstring> ListAllCodecs()
{
  std::vector<std::wstring> rows;
  const uint32_t n = az_codec_count();
  for (uint32_t i = 0; i < n; i++)
  {
    az_codec_info c;
    std::memset(&c, 0, sizeof(c));
    c.struct_size = sizeof(c);
    if (az_codec_get(i, &c) != AZ_OK)
      continue;
    std::wstring line = c.name ? ToWide(c.name) : L"?";
    while (line.size() < 18)
      line += L' ';
    line += c.can_encode ? L"enc " : L"    ";
    line += c.can_decode ? L"dec " : L"    ";
    if (c.is_filter)
      line += L"[filter]";
    if (c.id_hex)
    {
      line += L" id=0x";
      line += ToWide(c.id_hex);
    }
    rows.push_back(std::move(line));
    az_codec_release(&c);
  }
  return rows;
}

std::vector<std::pair<std::wstring, std::string>> WritableFormats()
{
  // The engine is asked which formats can write, then the list is put in an order
  // a person would expect -- 7z and zip first -- rather than the engine's own.
  // Names must be the canonical ones ("gzip", not "gz"), which is what
  // az_format_info::name returns and what az_create_options::format expects.
  static const char *kPreferred[] = { "7z", "zip", "tar", "gzip", "bzip2", "xz", "wim" };

  std::vector<std::pair<std::wstring, std::string>> out;
  std::vector<char> taken;
  const uint32_t n = az_format_count();
  taken.resize(n, 0);

  for (const char *want : kPreferred)
  {
    for (uint32_t i = 0; i < n; i++)
    {
      if (taken[i])
        continue;
      az_format_info f;
      std::memset(&f, 0, sizeof(f));
      f.struct_size = sizeof(f);
      if (az_format_get(i, &f) != AZ_OK)
        continue;
      const bool match = f.can_write && f.name && std::strcmp(f.name, want) == 0;
      if (match)
      {
        std::wstring label = ToWide(f.name);
        label += L"  (*.";
        label += f.extension ? ToWide(f.extension) : ToWide(f.name);
        label += L")";
        out.emplace_back(label, f.name);
        taken[i] = 1;
      }
      az_format_release(&f);
      if (match)
        break;
    }
  }

  // Anything else the engine can write stays available, after the familiar ones.
  for (uint32_t i = 0; i < n; i++)
  {
    if (taken[i])
      continue;
    az_format_info f;
    std::memset(&f, 0, sizeof(f));
    f.struct_size = sizeof(f);
    if (az_format_get(i, &f) != AZ_OK)
      continue;
    if (f.can_write && f.name)
    {
      std::wstring label = ToWide(f.name);
      label += L"  (*.";
      label += f.extension ? ToWide(f.extension) : ToWide(f.name);
      label += L")";
      out.emplace_back(label, f.name);
    }
    az_format_release(&f);
  }
  return out;
}

std::vector<std::string> MethodsFor(const std::string &format)
{
  // Method names are the engine's own; an empty result means the format has one
  // method and the UI should just leave the choice at its default.
  if (format == "7z")
    return { "LZMA2", "LZMA", "PPMd", "BZip2", "Deflate", "Copy" };
  if (format == "zip")
    return { "Deflate", "Deflate64", "BZip2", "LZMA", "LZMA2", "PPMd", "Copy" };
  if (format == "wim")
    return { "LZMS", "LZXC", "Xpress", "Xpress_Huff" };
  return {};
}

// ------------------------------------------------------------------ compress

bool Compress(const Job &job, const ProgressFn &fn, std::wstring &error)
{
  Callbacks cb(fn);

  az_create_options o;
  std::memset(&o, 0, sizeof(o));
  o.struct_size = sizeof(o);
  o.format = job.format.c_str();
  o.method = job.method.empty() ? nullptr : job.method.c_str();
  o.level = job.level;
  o.password = job.password.empty() ? nullptr : job.password.c_str();
  o.encryption = job.password.empty() ? AZ_ENC_NONE : AZ_ENC_DEFAULT;
  o.encrypt_headers = job.encryptHeaders ? 1 : 0;
  o.solid = job.solid ? 1 : 0;
  o.multi_thread = 1;
  o.callbacks = fn ? &cb.Raw : nullptr;

  const std::string archive = ToUtf8(job.archivePath);
  const std::string source = ToUtf8(job.sourcePath);

  std::wstring entryW = BaseName(job.sourcePath);
  if (!job.recurse)
  {
    std::error_code ec;
    if (fs::is_directory(job.sourcePath, ec))
    {
      error = L"a directory needs recursion turned on";
      return false;
    }
  }

  az_writer w = nullptr;
  az_result r = az_writer_open(archive.c_str(), &o, &w);
  if (r == AZ_OK)
    r = az_writer_add_file(w, source.c_str(), ToUtf8(entryW).c_str(), nullptr,
                          job.recurse ? 1 : 0);
  if (r == AZ_OK)
    r = az_writer_finish(w, nullptr, nullptr);

  if (w)
    az_writer_free(w);

  if (r != AZ_OK)
  {
    error = LastError();
    return false;
  }
  return true;
}

// ------------------------------------------------------------------- extract

bool Extract(const std::wstring &archive, const std::wstring &destDir,
             const std::string &password, bool testOnly,
             const ProgressFn &fn, std::wstring &error)
{
  Callbacks cb(fn);

  az_open_options oo;
  std::memset(&oo, 0, sizeof(oo));
  oo.struct_size = sizeof(oo);
  oo.password = password.empty() ? nullptr : password.c_str();
  oo.callbacks = fn ? &cb.Raw : nullptr;

  const std::string archiveU = ToUtf8(archive);
  az_archive h = nullptr;
  az_result r = az_archive_open(archiveU.c_str(), &oo, &h);

  if (r == AZ_OK)
  {
    az_extract_options eo;
    std::memset(&eo, 0, sizeof(eo));
    eo.struct_size = sizeof(eo);
    eo.password = password.empty() ? nullptr : password.c_str();
    eo.callbacks = fn ? &cb.Raw : nullptr;
    eo.overwrite = AZ_OVERWRITE_OVERWRITE;
    eo.preserve_mtime = 1;
    eo.test_only = testOnly ? 1 : 0;

    const std::string destU = ToUtf8(destDir);
    r = az_archive_extract(h, destU.c_str(), nullptr, 0, &eo);
    az_archive_close(h);
  }

  if (r != AZ_OK)
  {
    error = LastError();
    return false;
  }
  return true;
}

std::vector<Entry> ListEntries(const std::wstring &archive,
                               const std::string &password, std::wstring &error)
{
  std::vector<Entry> rows;

  az_open_options oo;
  std::memset(&oo, 0, sizeof(oo));
  oo.struct_size = sizeof(oo);
  oo.password = password.empty() ? nullptr : password.c_str();

  const std::string archiveU = ToUtf8(archive);
  az_archive h = nullptr;
  if (az_archive_open(archiveU.c_str(), &oo, &h) != AZ_OK)
  {
    error = LastError();
    return rows;
  }

  uint64_t count = 0;
  if (az_archive_entry_count(h, &count) != AZ_OK)
  {
    error = LastError();
    az_archive_close(h);
    return rows;
  }

  for (uint64_t i = 0; i < count; i++)
  {
    az_entry_info e;
    std::memset(&e, 0, sizeof(e));
    e.struct_size = sizeof(e);
    if (az_entry_get(h, i, &e) != AZ_OK)
      continue;
    Entry row;
    row.index = i;
    row.name = ToWide(e.name);
    row.method = ToWide(e.method);
    row.hasSize = e.size != (uint64_t)-1;
    row.hasPackedSize = e.packed_size != (uint64_t)-1;
    row.size = row.hasSize ? e.size : 0;
    row.packedSize = row.hasPackedSize ? e.packed_size : 0;
    row.crc = e.crc;
    row.isDir = e.is_dir != 0;
    row.encrypted = e.is_encrypted != 0;
    row.mtime = e.mtime_unix;
    rows.push_back(std::move(row));
    az_entry_release(&e);
  }

  az_archive_close(h);
  return rows;
}

// ------------------------------------------------------------------ self test

bool SelfTest(std::wstring &report, std::wstring &error)
{
  struct Case
  {
    const char *format;
    const char *method;
    bool password;
  };
  static const Case kCases[] = {
    { "7z",   "LZMA2", false },
    { "7z",   "LZMA",  false },
    { "7z",   "PPMd",  false },
    { "7z",   "BZip2", false },
    { "7z",   "Copy",  false },
    { "7z",   nullptr, true  },
    { "zip",  nullptr, false },
    { "zip",  "BZip2", false },
    { "zip",  "LZMA",  false },
    { "zip",  "Copy",  true  },
    { "tar",  nullptr, false },
    { "gz",   nullptr, false },
    { "bz2",  nullptr, false },
    { "xz",   nullptr, false },
  };

  const std::string payload = MakePayload(256 * 1024);
  const char *kPass = "az-self-test";

  std::wstring out;
  size_t passed = 0;

  for (const Case &c : kCases)
  {
    std::wstring line = ToWide(c.format);
    line += L" / ";
    line += ToWide(c.method ? c.method : "default");
    if (c.password)
      line += L" +AES";
    while (line.size() < 30)
      line += L' ';

    az_create_options o;
    std::memset(&o, 0, sizeof(o));
    o.struct_size = sizeof(o);
    o.format = c.format;
    o.method = c.method;
    o.level = 3;
    o.solid = 1;
    o.multi_thread = 1;
    if (c.password)
    {
      o.password = kPass;
      o.encryption = AZ_ENC_AES256;
      if (std::string(c.format) == "7z")
        o.encrypt_headers = 1;
    }

    az_writer w = nullptr;
    void *blob = nullptr;
    size_t blobSize = 0;
    az_result r = az_writer_open_memory(&o, &w);
    if (r == AZ_OK)
      r = az_writer_add_buffer(w, payload.data(), payload.size(), "payload.bin", nullptr);
    if (r == AZ_OK)
      r = az_writer_finish(w, &blob, &blobSize);

    if (r == AZ_OK)
    {
      az_open_options oo;
      std::memset(&oo, 0, sizeof(oo));
      oo.struct_size = sizeof(oo);
      oo.password = c.password ? kPass : nullptr;

      const std::string hint = std::string(c.format) + ".bin";
      az_archive h = nullptr;
      r = az_archive_open_memory(blob, blobSize, hint.c_str(), &oo, &h);
      if (r == AZ_OK)
      {
        uint64_t n = 0;
        az_archive_entry_count(h, &n);
        if (n != 1)
          r = AZ_ERROR_GENERIC;
      }
      if (r == AZ_OK)
      {
        std::string back;
        back.resize(payload.size());
        size_t written = 0;
        r = az_entry_read(h, 0, back.data(), back.size(), &written);
        if (r == AZ_OK && (written != payload.size() || back != payload))
          r = AZ_ERROR_GENERIC;
      }
      if (h)
        az_archive_close(h);
      az_free(blob);
    }
    if (w)
      az_writer_free(w);

    if (r == AZ_OK)
    {
      passed++;
      line += L"OK   " + WideFromU64(payload.size()) + L" -> " + WideFromU64(blobSize);
    }
    else
    {
      line += L"FAIL " + LastError();
    }
    out += line + L"\r\n";
    if (!c.password && r != AZ_OK)
      error = LastError();
  }

  {
    std::wstring sum = L"passed " + WideFromU64(passed) + L" of " +
                      WideFromU64(sizeof(kCases) / sizeof(kCases[0]));
    out += L"\r\n" + sum;
    report = out;
    return passed == sizeof(kCases) / sizeof(kCases[0]);
  }
}

bool RoundTripFile(const Job &job, std::wstring &report, std::wstring &error)
{
  // Verifies the archive the caller just produced, not a fresh temporary one:
  // extract job.archivePath into a scratch directory and compare every member
  // with the source it came from.
  std::error_code ec;
  const fs::path tmp = fs::temp_directory_path(ec) / "andzip-azgui-verify";
  if (ec)
  {
    error = L"no temporary directory";
    return false;
  }
  const fs::path archivePath(job.archivePath);
  const fs::path srcRoot(job.sourcePath);
  if (!fs::exists(archivePath, ec))
  {
    error = L"archive not found: " + archivePath.wstring();
    report = error;
    return false;
  }

  const fs::path outDir = tmp / L"out";
  fs::remove_all(outDir, ec);
  fs::create_directories(outDir, ec);

  unsigned long long srcSize = 0;
  unsigned long long srcFiles = 0;
  std::error_code sec;
  if (fs::is_directory(srcRoot))
  {
    // fs::file_size(path) rather than directory_entry::file_size(): the report is
    // the thing the user trusts to be right, so re-stat it instead of relying on
    // whatever the iterator cached.
    for (const auto &e : fs::recursive_directory_iterator(srcRoot))
      if (e.is_regular_file())
      {
        srcSize += (unsigned long long)fs::file_size(e.path(), sec);
        srcFiles++;
      }
  }
  else
    srcSize = (unsigned long long)fs::file_size(srcRoot, ec);

  std::wstring out;
  out += L"archive  " + archivePath.wstring() + L"\r\n";
  out += L"source   " + srcRoot.wstring() + L"\r\n";
  out += L"compare  extracted to " + outDir.wstring() + L"\r\n";
  out += L"sizes    " + WideFromU64(srcSize) + L" (" + WideFromU64(srcFiles) +
         L" file) -> " + WideFromU64((unsigned long long)fs::file_size(archivePath, ec)) + L"\r\n";

  ProgressFn none;
  if (!Extract(archivePath.wstring(), outDir.wstring(), job.password, false, none, error))
  {
    out += L"extract FAILED: " + error;
    report = out;
    return false;
  }

  // Entry names were built from the source's last path component, so the
  // extracted tree is <outDir>/<basename>/... for a directory source.
  const fs::path base = srcRoot.filename();
  size_t compared = 0, mismatched = 0;
  auto check = [&](const fs::path &file, const fs::path &rel) {
    compared++;
    std::string a, b;
    if (!ReadAllBytes(file, a) || !ReadAllBytes(outDir / base / rel, b) || a != b)
      mismatched++;
  };
  if (fs::is_directory(srcRoot))
  {
    for (const auto &e : fs::recursive_directory_iterator(srcRoot))
    {
      if (!e.is_regular_file())
        continue;
      check(e.path(), fs::relative(e.path(), srcRoot, ec));
    }
  }
  else
    check(srcRoot, L".");

  out += L"checked  " + WideFromU64(compared) + L" file(s), " +
         WideFromU64(mismatched) + L" mismatched";
  report = out;
  if (mismatched)
    error = L"extracted bytes differ from the source";
  return mismatched == 0;
}

} // namespace azgui
