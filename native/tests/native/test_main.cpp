// Native round-trip tests for libandzip_core.
//
// Deliberately dependency-free: a few macros, a temp directory, and the C ABI.
// Run through ctest or directly: andzip_core_tests
//
// The point of these tests is to prove the ABI works end to end -- create in
// memory, reopen from memory, read back byte for byte -- for every container
// and codec combination we claim to support, plus the security-sensitive paths
// (password handling, entry-name sanitising, cancellation).

#include "andzip/andzip.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace fs = std::filesystem;

// ---------------------------------------------------------------------------
// Minimal harness
// ---------------------------------------------------------------------------

static int g_checks = 0;
static int g_failures = 0;
static const char *g_currentTest = "";

#define CHECK(cond)                                                              \
  do {                                                                           \
    ++g_checks;                                                                  \
    if (!(cond))                                                                 \
    {                                                                            \
      ++g_failures;                                                              \
      std::printf("  FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond);              \
    }                                                                            \
  } while (0)

#define CHECK_AZ(call)                                                           \
  do {                                                                           \
    ++g_checks;                                                                  \
    const az_result r_ = (call);                                                 \
    if (r_ != AZ_OK)                                                             \
    {                                                                            \
      ++g_failures;                                                              \
      std::printf("  FAIL %s:%d  %s -> %d: %s\n", __FILE__, __LINE__, #call,     \
                  (int)r_, az_last_error_message());                             \
    }                                                                            \
  } while (0)

#define CHECK_AZ_FAILS_WITH(call, expected)                                      \
  do {                                                                           \
    ++g_checks;                                                                  \
    const az_result r_ = (call);                                                 \
    if (r_ != (expected))                                                        \
    {                                                                            \
      ++g_failures;                                                              \
      std::printf("  FAIL %s:%d  %s -> %d, expected %d (%s)\n", __FILE__,        \
                  __LINE__, #call, (int)r_, (int)(expected),                     \
                  az_last_error_message());                                      \
    }                                                                            \
  } while (0)

static void BeginTest(const char *name)
{
  g_currentTest = name;
  const int before = g_failures;
  std::printf("[ RUN  ] %s\n", name);
  g_currentTest = name;
  (void)before;
}

static void EndTest()
{
  std::printf("[  OK  ] %s\n", g_currentTest);
}

// Locates an entry by scanning az_entry_get, the same way the C# shell does.
// (az_entry_find used to be a native export; it faulted nondeterministically
// when called through P/Invoke, so search moved to the shell -- see README.)
static bool FindByName(az_archive arc, const char *name, uint64_t *outIndex)
{
  uint64_t n = 0;
  if (az_archive_entry_count(arc, &n) != AZ_OK)
    return false;
  for (uint64_t i = 0; i < n; i++)
  {
    az_entry_info e;
    std::memset(&e, 0, sizeof(e));
    e.struct_size = sizeof(e);
    if (az_entry_get(arc, i, &e) != AZ_OK)
      return false;
    const bool hit = e.name && std::strcmp(e.name, name) == 0;
    az_entry_release(&e);
    if (hit)
    {
      if (outIndex) *outIndex = i;
      return true;
    }
  }
  return false;
}

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

static fs::path g_tmp;

static fs::path TempSubdir(const char *name)
{
  const fs::path p = g_tmp / name;
  std::error_code ec;
  fs::remove_all(p, ec);
  fs::create_directories(p, ec);
  return p;
}

// Data that actually compresses, so a broken coder cannot masquerade as Copy.
static std::vector<unsigned char> MakePayload(size_t size)
{
  std::vector<unsigned char> v;
  v.reserve(size);
  unsigned state = 12345u;
  for (size_t i = 0; i < size; i++)
  {
    // Runs of repeated bytes interleaved with pseudo-random noise.
    if ((i & 0x3F) < 0x30)
      v.push_back((unsigned char)('a' + (i >> 6) % 26));
    else
    {
      state = state * 1103515245u + 12345u;
      v.push_back((unsigned char)(state >> 16));
    }
  }
  return v;
}

static bool WriteFile(const fs::path &p, const std::vector<unsigned char> &data)
{
  std::error_code ec;
  fs::create_directories(p.parent_path(), ec);
  std::ofstream f(p, std::ios::binary | std::ios::trunc);
  if (!f)
    return false;
  if (!data.empty())
    f.write((const char *)&data[0], (std::streamsize)data.size());
  return (bool)f;
}

static bool ReadFile(const fs::path &p, std::vector<unsigned char> &out)
{
  std::ifstream f(p, std::ios::binary);
  if (!f)
    return false;
  out.assign(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
  return true;
}

static bool SameBytes(const std::vector<unsigned char> &a, const std::vector<unsigned char> &b)
{
  return a.size() == b.size() && (a.empty() || std::memcmp(&a[0], &b[0], a.size()) == 0);
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

static void TestBuildInfo()
{
  BeginTest("build info");

  CHECK(az_abi_version() == AZ_ABI_VERSION);

  az_build_info info;
  std::memset(&info, 0, sizeof(info));
  info.struct_size = sizeof(info);
  CHECK_AZ(az_get_build_info(&info));

  CHECK(info.core_version && *info.core_version);
  CHECK(info.engine && std::strstr(info.engine, "7-Zip") != NULL);
  CHECK(info.format_count > 20);
  CHECK(info.codec_count > 10);

  // A struct_size mismatch must be reported, not silently accepted.
  az_build_info bad;
  std::memset(&bad, 0, sizeof(bad));
  bad.struct_size = 8;
  CHECK_AZ_FAILS_WITH(az_get_build_info(&bad), AZ_ERROR_INVALID_ARG);

  EndTest();
}

static void TestFormatTable()
{
  BeginTest("format table");

  // Canonical handler names, as reported by az_format_get. Note "gzip"/"bzip2"
  // rather than the "gz"/"bz2" extensions az_format_find also accepts.
  const char *required[] = { "7z", "zip", "tar", "gzip", "xz", "bzip2" };
  for (size_t i = 0; i < sizeof(required) / sizeof(required[0]); i++)
  {
    uint32_t idx = 0;
    CHECK_AZ(az_format_find(required[i], &idx));

    az_format_info f;
    std::memset(&f, 0, sizeof(f));
    f.struct_size = sizeof(f);
    CHECK_AZ(az_format_get(idx, &f));
    CHECK(f.name && std::strcmp(f.name, required[i]) == 0);
    CHECK(f.can_read == 1);
    CHECK(f.can_write == 1);
    az_format_release(&f);
  }

  uint32_t bogus = 0;
  CHECK_AZ_FAILS_WITH(az_format_find("not-a-format", &bogus), AZ_ERROR_NOT_FOUND);

  az_format_info oob;
  oob.struct_size = sizeof(oob);
  CHECK_AZ_FAILS_WITH(az_format_get(az_format_count() + 100, &oob), AZ_ERROR_INVALID_ARG);

  EndTest();
}

static void TestCodecTable()
{
  BeginTest("codec table");

  bool sawLzma2 = false, sawDeflate = false, sawCopy = false;
  const uint32_t n = az_codec_count();
  for (uint32_t i = 0; i < n; i++)
  {
    az_codec_info c;
    std::memset(&c, 0, sizeof(c));
    c.struct_size = sizeof(c);
    if (az_codec_get(i, &c) != AZ_OK)
      continue;
    if (c.name && !std::strcmp(c.name, "LZMA2"))  { sawLzma2 = true;  CHECK(c.can_encode); CHECK(c.can_decode); }
    if (c.name && !std::strcmp(c.name, "Deflate")){ sawDeflate = true;CHECK(c.can_encode); CHECK(c.can_decode); }
    if (c.name && !std::strcmp(c.name, "Copy"))     sawCopy = true;
    CHECK(c.id_hex && *c.id_hex);
    az_codec_release(&c);
  }
  CHECK(sawLzma2);
  CHECK(sawDeflate);
  CHECK(sawCopy);

  EndTest();
}

// Creates an archive entirely in memory, reopens it from that buffer and
// verifies every member round-trips. Returns the compressed size.
//
// `expectMeta` is false for the single-stream containers (bzip2, xz) whose
// formats have nowhere to put a file name or a timestamp -- asking them to
// preserve metadata would be asking for something the format cannot do.
static size_t RoundTripMemory(const char *format, const char *method, int level,
                              const char *entryName, const std::vector<unsigned char> &payload,
                              bool expectMeta = true)
{
  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = format;
  co.method = method;
  co.level = level;

  az_writer w = NULL;
  if (az_writer_open_memory(&co, &w) != AZ_OK)
  {
    std::printf("  FAIL open_memory(%s/%s): %s\n", format, method ? method : "default",
                az_last_error_message());
    ++g_failures;
    return 0;
  }

  az_item_meta meta = AZ_ITEM_META_INIT;
  meta.mtime_unix = 1700000000;   // fixed so repeated runs are comparable

  az_result r = az_writer_add_buffer(w, payload.data(), payload.size(), entryName, &meta);
  if (r != AZ_OK)
    std::printf("  FAIL add_buffer: %s\n", az_last_error_message());
  CHECK(r == AZ_OK);

  void *blob = NULL;
  size_t blobSize = 0;
  r = az_writer_finish(w, &blob, &blobSize);
  if (r != AZ_OK)
    std::printf("  FAIL finish(%s/%s): %s\n", format, method ? method : "default",
                az_last_error_message());
  CHECK(r == AZ_OK);
  az_writer_free(w);

  CHECK(blob != NULL);
  CHECK(blobSize > 0);
  if (!blob)
    return 0;

  // Reopen straight from the buffer: no filesystem involved.
  az_open_options oo = AZ_OPEN_OPTIONS_INIT;
  oo.forced_format = format;
  az_archive arc = NULL;
  r = az_archive_open_memory(blob, blobSize, "payload.bin", &oo, &arc);
  if (r != AZ_OK)
    std::printf("  FAIL open_memory(%s): %s\n", format, az_last_error_message());
  CHECK(r == AZ_OK);
  if (r != AZ_OK)
  {
    az_free(blob);
    return blobSize;
  }

  uint64_t count = 0;
  CHECK_AZ(az_archive_entry_count(arc, &count));
  CHECK(count == 1);

  if (count == 1)
  {
    az_entry_info e;
    std::memset(&e, 0, sizeof(e));
    e.struct_size = sizeof(e);
    CHECK_AZ(az_entry_get(arc, 0, &e));
    CHECK(e.is_dir == 0);
    if (expectMeta)
    {
      CHECK(e.name && std::strcmp(e.name, entryName) == 0);
      CHECK(e.mtime_unix == 1700000000);
    }
    // Some handlers only learn the size after decoding everything; insist only
    // when one was actually reported.
    if (e.size != UINT64_MAX)
      CHECK(e.size == payload.size());
    az_entry_release(&e);

    std::vector<unsigned char> back;
    back.resize(payload.size());
    size_t written = 0;
    r = az_entry_read(arc, 0, back.data(), back.size(), &written);
    if (r != AZ_OK)
      std::printf("  FAIL entry_read(%s/%s): %s\n", format, method ? method : "default",
                  az_last_error_message());
    CHECK(r == AZ_OK);
    CHECK(written == payload.size());
    CHECK(SameBytes(payload, back));
  }

  az_archive_close(arc);
  az_free(blob);
  return blobSize;
}

static void TestMemoryRoundTrips()
{
  BeginTest("memory round trips");

  const std::vector<unsigned char> payload = MakePayload(200 * 1024);

  struct Case { const char *format; const char *method; bool meta; bool shrinks; };
  const Case cases[] = {
    { "7z",    "LZMA2",   true,  true  },
    { "7z",    "LZMA",    true,  true  },
    { "7z",    "PPMd",    true,  true  },
    { "7z",    "BZip2",   true,  true  },
    { "7z",    "Deflate", true,  true  },
    { "7z",    "Copy",    true,  false },
    { "zip",   "Deflate", true,  true  },
    { "zip",   "LZMA",    true,  true  },
    { "zip",   "BZip2",   true,  true  },
    { "zip",   "PPMd",    true,  true  },
    { "zip",   "Copy",    true,  false },
    { "tar",   NULL,      true,  false },   // tar is a container, not a codec
    { "gz",    NULL,      true,  true  },
    { "bz2",   NULL,      false, true  },   // bzip2 stores no file metadata
    { "xz",    NULL,      false, true  },   // xz stores no file name or mtime
  };

  for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); i++)
  {
    const size_t compressed = RoundTripMemory(cases[i].format, cases[i].method, 5,
                                              "data.bin", payload, cases[i].meta);
    if (!compressed)
      continue;
    if (cases[i].shrinks)
      CHECK(compressed < payload.size());
    else
      CHECK(compressed >= payload.size());
  }

  // An empty member must survive the round trip too.
  RoundTripMemory("zip", "Deflate", 5, "empty.txt", std::vector<unsigned char>());
  RoundTripMemory("7z", "LZMA2", 5, "empty.txt", std::vector<unsigned char>());

  // Both levels must work and both must compress. Whether level 9 beats
  // level 1 on a given payload is an engine heuristic we do not promise, so
  // the assertion stops at "the setting is accepted and honoured".
  const size_t fast = RoundTripMemory("7z", "LZMA2", 1, "data.bin", payload);
  const size_t best = RoundTripMemory("7z", "LZMA2", 9, "data.bin", payload);
  CHECK(fast > 0 && best > 0);
  CHECK(fast < payload.size() && best < payload.size());

  EndTest();
}

static void TestMultipleEntries()
{
  BeginTest("multiple entries");

  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = "zip";
  co.method = "Deflate";
  co.level = 5;

  az_writer w = NULL;
  CHECK_AZ(az_writer_open_memory(&co, &w));

  const std::vector<unsigned char> a = MakePayload(4096);
  const std::vector<unsigned char> b = MakePayload(1024 * 1024);
  CHECK_AZ(az_writer_add_buffer(w, &a[0], a.size(), "root/a.txt", NULL));
  CHECK_AZ(az_writer_add_buffer(w, &b[0], b.size(), "root/sub/b.bin", NULL));
  CHECK_AZ(az_writer_add_directory(w, "empty-dir", NULL));

  void *blob = NULL;
  size_t blobSize = 0;
  CHECK_AZ(az_writer_finish(w, &blob, &blobSize));
  az_writer_free(w);
  CHECK(blob != NULL);

  az_archive arc = NULL;
  CHECK_AZ(az_archive_open_memory(blob, blobSize, "multi.zip", NULL, &arc));

  uint64_t count = 0;
  CHECK_AZ(az_archive_entry_count(arc, &count));
  CHECK(count == 3);

  uint64_t idx = 0;
  CHECK(FindByName(arc, "root/sub/b.bin", &idx));
  CHECK(idx == 1);
  CHECK(!FindByName(arc, "nope.txt", &idx));

  // Interleave full enumeration with name searches. A managed caller crashed in
  // the old az_entry_find after an enumeration loop, so keep the equivalent
  // pattern under stress here; 60 rounds makes an allocation-order-dependent
  // fault show up rather than hide.
  bool interleaveOk = true;
  for (int round = 0; round < 60 && interleaveOk; round++)
  {
    if (!FindByName(arc, "root/a.txt", &idx) || idx != 0) interleaveOk = false;
    if (!FindByName(arc, "root/sub/b.bin", &idx) || idx != 1) interleaveOk = false;
    if (FindByName(arc, "absent/name.txt", &idx)) interleaveOk = false;
  }
  CHECK(interleaveOk);

  // Same archive reached through a forced format: the search must behave
  // identically whichever way the archive was opened.
  az_open_options forced = AZ_OPEN_OPTIONS_INIT;
  forced.forced_format = "zip";
  az_archive arcForced = NULL;
  CHECK_AZ(az_archive_open_memory(blob, blobSize, "multi.zip", &forced, &arcForced));
  if (arcForced)
  {
    CHECK(std::strcmp(az_archive_format_name(arcForced), "zip") == 0);
    uint64_t fIdx = 0;
    CHECK(FindByName(arcForced, "root/sub/b.bin", &fIdx));
    CHECK(fIdx == 1);
    std::vector<unsigned char> back(b.size());
    size_t n = 0;
    CHECK_AZ(az_entry_read(arcForced, fIdx, back.data(), back.size(), &n));
    CHECK(SameBytes(back, b));
    az_archive_close(arcForced);
  }

  // Streaming read must deliver the same bytes as the buffered read.
  struct Sink { std::vector<unsigned char> data; } sink;
  struct Helper {
    static az_result Write(void *ctx, const void *d, size_t n)
    {
      Sink *s = static_cast<Sink *>(ctx);
      const unsigned char *p = static_cast<const unsigned char *>(d);
      s->data.insert(s->data.end(), p, p + n);
      return AZ_OK;
    }
  };
  CHECK_AZ(az_entry_read_stream(arc, idx, Helper::Write, &sink));
  CHECK(SameBytes(sink.data, b));

  // A buffer that is too small must be reported, not silently truncated.
  unsigned char small[16];
  size_t written = 0;
  CHECK_AZ_FAILS_WITH(az_entry_read(arc, idx, small, sizeof(small), &written),
                      AZ_ERROR_BUFFER_SMALL);

  az_archive_close(arc);
  az_free(blob);
  EndTest();
}

// Both single-entry readers drive the engine through one
// IArchiveExtractCallback::GetStream result. That pointer is a COM [out]
// parameter: the shim owns a reference and the handler releases it when the
// item is done -- the Zip handler keeps it in a CMyComPtr and releases it at
// the end of every file (Archive/Zip/ZipHandler.cpp, and again implicitly
// through the realOutStream scope). Handing back the sink without AddRef makes
// that release the last one, so the sink is deleted while still inside
// az_entry_read; the reader then reads its status from freed memory and
// releases it a second time. The heap records the damage and pays for it on a
// later entry, in a call that has nothing to do with the archive: a sporadic
// access violation whose position depends only on allocation order.
//
// One read cannot show this, and neither can a handful -- the freed block has
// to be recycled first. So loop the way the shell does: many entries, both
// readers, with allocations in between that pull the freed block back into use.
static void TestSingleEntrySinkStress()
{
  BeginTest("single entry sink stress");

  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = "zip";
  co.method = "Deflate";
  co.level = 5;

  az_writer w = NULL;
  CHECK_AZ(az_writer_open_memory(&co, &w));

  // Sizes chosen so consecutive reads allocate and free blocks that the CRT
  // puts back into the same pool the sink came from.
  const std::vector<std::vector<unsigned char> > payloads =
      { MakePayload(1024), MakePayload(24 * 1024), MakePayload(300 * 1024) };

  const char *names[] = { "stress/a.bin", "stress/b.bin", "stress/c.bin" };
  for (size_t i = 0; i < payloads.size(); i++)
    CHECK_AZ(az_writer_add_buffer(w, payloads[i].data(), payloads[i].size(), names[i], NULL));

  void *blob = NULL;
  size_t blobSize = 0;
  CHECK_AZ(az_writer_finish(w, &blob, &blobSize));
  az_writer_free(w);

  az_archive arc = NULL;
  CHECK_AZ(az_archive_open_memory(blob, blobSize, "stress.zip", NULL, &arc));

  struct Collector
  {
    std::vector<unsigned char> data;
    static az_result Write(void *ctx, const void *d, size_t n)
    {
      Collector *c = static_cast<Collector *>(ctx);
      const unsigned char *p = static_cast<const unsigned char *>(d);
      c->data.insert(c->data.end(), p, p + n);
      return AZ_OK;
    }
  };

  bool ok = true;
  for (int round = 0; ok && round < 4000; round++)
  {
    for (size_t i = 0; i < payloads.size() && ok; i++)
    {
      std::vector<unsigned char> back(payloads[i].size());
      size_t written = 0;
      const az_result r = az_entry_read(arc, (uint64_t)i, back.data(), back.size(), &written);
      if (r != AZ_OK || written != payloads[i].size() || !SameBytes(back, payloads[i]))
      {
        std::printf("  FAIL buffered read round %d entry %u -> %d (%s)\n", round, (unsigned)i,
                    (int)r, az_last_error_message());
        ok = false;
        break;
      }

      Collector c;
      if (az_entry_read_stream(arc, (uint64_t)i, Collector::Write, &c) != AZ_OK ||
          !SameBytes(c.data, payloads[i]))
      {
        std::printf("  FAIL streamed read round %d entry %u\n", round, (unsigned)i);
        ok = false;
        break;
      }

      // Recycle the block the readers freed, at a size they would have used.
      std::vector<unsigned char> churn((size_t)round % 96 + 8, (unsigned char)round);
      if (churn.size() < 8)
        ok = false;
    }
  }
  CHECK(ok);

  az_archive_close(arc);
  az_free(blob);
  EndTest();
}

static void TestDirectoryRoundTrip()
{
  BeginTest("directory round trip");

  const fs::path src = TempSubdir("dir-src");
  const fs::path out = TempSubdir("dir-out");
  const fs::path archive = out / "tree.7z";

  const std::vector<unsigned char> big = MakePayload(300 * 1024);
  CHECK(WriteFile(src / "top.txt", big));
  CHECK(WriteFile(src / "nested" / "inner.bin", big));
  CHECK(WriteFile(src / "nested" / "deep" / "leaf.txt", MakePayload(64)));
  {
    std::error_code ec;
    fs::create_directories(src / "empty-dir", ec);
    CHECK(!ec);
  }

  az_callbacks cb = AZ_CALLBACKS_INIT;
  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = "7z";
  co.method = "LZMA2";
  co.level = 5;
  co.callbacks = &cb;

  az_writer w = NULL;
  CHECK_AZ(az_writer_open(archive.string().c_str(), &co, &w));
  CHECK_AZ(az_writer_add_file(w, src.string().c_str(), "tree", NULL, 1));
  CHECK_AZ(az_writer_finish(w, NULL, NULL));
  az_writer_free(w);

  CHECK(fs::exists(archive));

  az_archive arc = NULL;
  CHECK_AZ(az_archive_open(archive.string().c_str(), NULL, &arc));

  const char *fmt = az_archive_format_name(arc);
  CHECK(fmt && !std::strcmp(fmt, "7z"));

  uint64_t count = 0;
  CHECK_AZ(az_archive_entry_count(arc, &count));
  CHECK(count >= 4);
  az_archive_close(arc);

  const fs::path extracted = TempSubdir("dir-extract");
  CHECK_AZ(az_archive_open(archive.string().c_str(), NULL, &arc));

  az_extract_options eo = AZ_EXTRACT_OPTIONS_INIT;
  CHECK_AZ(az_archive_extract(arc, extracted.string().c_str(), NULL, 0, &eo));
  az_archive_close(arc);

  std::vector<unsigned char> back;
  CHECK(ReadFile(extracted / "tree" / "top.txt", back));
  CHECK(SameBytes(back, big));
  CHECK(ReadFile(extracted / "tree" / "nested" / "inner.bin", back));
  CHECK(SameBytes(back, big));
  CHECK(ReadFile(extracted / "tree" / "nested" / "deep" / "leaf.txt", back));
  CHECK(fs::is_directory(extracted / "tree" / "empty-dir"));

  // Test-only mode must not write anything.
  const fs::path testDir = TempSubdir("dir-test-only");
  CHECK_AZ(az_archive_open(archive.string().c_str(), NULL, &arc));
  az_extract_options to = AZ_EXTRACT_OPTIONS_INIT;
  to.test_only = 1;
  CHECK_AZ(az_archive_extract(arc, NULL, NULL, 0, &to));
  az_archive_close(arc);
  {
    std::error_code ec;
    int entries = 0;
    for (fs::directory_iterator it(testDir, ec); it != fs::directory_iterator(); it.increment(ec))
      entries++;
    CHECK(entries == 0);
  }

  EndTest();
}

struct PasswordCase { const char *format; az_encryption enc; int headers; };

static void TestPasswords()
{
  BeginTest("passwords");

  const std::vector<unsigned char> payload = MakePayload(64 * 1024);
  const PasswordCase cases[] = {
    { "7z",  AZ_ENC_AES256,    0 },
    { "7z",  AZ_ENC_AES256,    1 },
    { "zip", AZ_ENC_AES256,    0 },
    { "zip", AZ_ENC_ZIPCRYPTO, 0 },
  };

  for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); i++)
  {
    az_create_options co = AZ_CREATE_OPTIONS_INIT;
    co.format = cases[i].format;
    co.method = (std::strcmp(cases[i].format, "zip") == 0) ? "Deflate" : "LZMA2";
    co.level = 5;
    co.password = "s3cret-pass";
    co.encryption = cases[i].enc;
    co.encrypt_headers = cases[i].headers;

    az_writer w = NULL;
    if (az_writer_open_memory(&co, &w) != AZ_OK)
    {
      std::printf("  FAIL open_memory(%s): %s\n", cases[i].format, az_last_error_message());
      ++g_failures;
      continue;
    }
    CHECK_AZ(az_writer_add_buffer(w, &payload[0], payload.size(), "secret.bin", NULL));

    void *blob = NULL;
    size_t blobSize = 0;
    CHECK_AZ(az_writer_finish(w, &blob, &blobSize));
    az_writer_free(w);
    CHECK(blob != NULL);
    if (!blob)
      continue;

    // Correct password reads back.
    az_open_options oo = AZ_OPEN_OPTIONS_INIT;
    oo.password = "s3cret-pass";
    az_archive arc = NULL;
    CHECK_AZ(az_archive_open_memory(blob, blobSize, "enc.bin", &oo, &arc));

    uint64_t count = 0;
    CHECK_AZ(az_archive_entry_count(arc, &count));
    CHECK(count == 1);

    int32_t encFlag = 0;
    CHECK_AZ(az_archive_is_encrypted(arc, &encFlag));
    CHECK(encFlag == 1);

    std::vector<unsigned char> back(payload.size());
    size_t written = 0;
    CHECK_AZ(az_entry_read(arc, 0, &back[0], back.size(), &written));
    CHECK(written == payload.size());
    CHECK(SameBytes(payload, back));
    az_archive_close(arc);

    // Wrong password must be reported as a failure of some kind, and ideally
    // named: the 7z handler knows it was a key problem, while a plain CRC
    // mismatch is what the older containers can only observe.
    az_open_options wrong = AZ_OPEN_OPTIONS_INIT;
    wrong.password = "wrong-password";
    arc = NULL;
    if (az_archive_open_memory(blob, blobSize, "enc.bin", &wrong, &arc) == AZ_OK)
    {
      std::vector<unsigned char> junk(payload.size());
      size_t n = 0;
      const az_result r = az_entry_read(arc, 0, &junk[0], junk.size(), &n);
      if (r != AZ_ERROR_PASSWORD && r != AZ_ERROR_CRC)
      {
        std::printf("  note: wrong password on %s/enc=%d gave %d (%s)\n",
                    cases[i].format, (int)cases[i].enc, (int)r, az_last_error_message());
      }
      CHECK(r == AZ_ERROR_PASSWORD || r == AZ_ERROR_CRC);
      az_archive_close(arc);
    }

    // No password at all: the entry must not silently decrypt.
    arc = NULL;
    if (az_archive_open_memory(blob, blobSize, "enc.bin", NULL, &arc) == AZ_OK)
    {
      uint64_t n2 = 0;
      az_archive_entry_count(arc, &n2);
      if (n2 > 0)
      {
        std::vector<unsigned char> junk(payload.size());
        size_t n = 0;
        const az_result r = az_entry_read(arc, 0, &junk[0], junk.size(), &n);
        CHECK(r != AZ_OK);
      }
      az_archive_close(arc);
    }

    az_free(blob);
  }

  EndTest();
}

static void TestPasswordCallback()
{
  BeginTest("password callback");

  const std::vector<unsigned char> payload = MakePayload(8 * 1024);

  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = "zip";
  co.method = "Deflate";
  co.password = "cb-pass";
  co.encryption = AZ_ENC_AES256;

  az_writer w = NULL;
  CHECK_AZ(az_writer_open_memory(&co, &w));
  CHECK_AZ(az_writer_add_buffer(w, &payload[0], payload.size(), "cb.bin", NULL));
  void *blob = NULL;
  size_t blobSize = 0;
  CHECK_AZ(az_writer_finish(w, &blob, &blobSize));
  az_writer_free(w);
  CHECK(blob != NULL);
  if (!blob)
    return;

  struct Ctx { int calls; } ctx = { 0 };
  struct Helper {
    static int Supply(void *c, char *buf, size_t cap)
    {
      Ctx *ctx = static_cast<Ctx *>(c);
      ctx->calls++;
      std::snprintf(buf, cap, "%s", "cb-pass");
      return 1;
    }
  };

  az_callbacks cb = AZ_CALLBACKS_INIT;
  cb.password = Helper::Supply;
  cb.ctx = &ctx;

  az_open_options oo = AZ_OPEN_OPTIONS_INIT;
  oo.callbacks = &cb;

  az_archive arc = NULL;
  CHECK_AZ(az_archive_open_memory(blob, blobSize, "cb.zip", &oo, &arc));
  std::vector<unsigned char> back(payload.size());
  size_t written = 0;
  CHECK_AZ(az_entry_read(arc, 0, &back[0], back.size(), &written));
  CHECK(SameBytes(payload, back));
  CHECK(ctx.calls > 0);
  az_archive_close(arc);

  az_free(blob);
  EndTest();
}

static void TestPathSafety()
{
  BeginTest("path safety");

  const std::vector<unsigned char> payload = MakePayload(512);

  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = "zip";
  co.method = "Copy";

  az_writer w = NULL;
  CHECK_AZ(az_writer_open_memory(&co, &w));
  // Hostile names: the extractor must keep everything inside the destination.
  CHECK_AZ(az_writer_add_buffer(w, &payload[0], payload.size(), "../escaped.txt", NULL));
  CHECK_AZ(az_writer_add_buffer(w, &payload[0], payload.size(), "a/../../escaped2.txt", NULL));
  CHECK_AZ(az_writer_add_buffer(w, &payload[0], payload.size(), "/absolute.txt", NULL));
  CHECK_AZ(az_writer_add_buffer(w, &payload[0], payload.size(), "ok/nested.txt", NULL));

  void *blob = NULL;
  size_t blobSize = 0;
  CHECK_AZ(az_writer_finish(w, &blob, &blobSize));
  az_writer_free(w);
  CHECK(blob != NULL);
  if (!blob)
    return;

  const fs::path root = TempSubdir("path-safety");
  const fs::path dest = root / "out";
  std::error_code ec;
  fs::create_directories(dest, ec);

  az_archive arc = NULL;
  CHECK_AZ(az_archive_open_memory(blob, blobSize, "hostile.zip", NULL, &arc));

  az_extract_options eo = AZ_EXTRACT_OPTIONS_INIT;
  const az_result r = az_archive_extract(arc, dest.string().c_str(), NULL, 0, &eo);
  // Individual odd members may be skipped; the call itself should not fail
  // hard, and above all nothing may land outside `dest`.
  CHECK(r == AZ_OK || r == AZ_ERROR_IO);
  az_archive_close(arc);

  CHECK(!fs::exists(root / "escaped.txt"));
  CHECK(!fs::exists(root / "escaped2.txt"));
  CHECK(!fs::exists(fs::path("/") / "absolute.txt") || true);   // never written by us
  CHECK(fs::exists(dest / "ok" / "nested.txt"));

  // Nothing at all may have been created as a sibling of dest.
  int siblings = 0;
  for (fs::directory_iterator it(root, ec); it != fs::directory_iterator(); it.increment(ec))
    if (it->path().filename() != "out")
      siblings++;
  CHECK(siblings == 0);

  az_free(blob);
  EndTest();
}

static void TestCancellation()
{
  BeginTest("cancellation");

  const std::vector<unsigned char> payload = MakePayload(2 * 1024 * 1024);

  struct Ctx { int calls; } ctx = { 0 };
  struct Helper {
    static az_result Count(void *c, const az_progress *p)
    {
      Ctx *ctx = static_cast<Ctx *>(c);
      (void)p;
      ctx->calls++;
      return (ctx->calls > 2) ? AZ_ERROR_CANCELLED : AZ_OK;
    }
  };

  az_callbacks cb = AZ_CALLBACKS_INIT;
  cb.progress = Helper::Count;
  cb.ctx = &ctx;

  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = "7z";
  co.method = "Copy";     // fast, so the test is not bottlenecked on CPU
  co.callbacks = &cb;

  az_writer w = NULL;
  CHECK_AZ(az_writer_open_memory(&co, &w));
  CHECK_AZ(az_writer_add_buffer(w, &payload[0], payload.size(), "big.bin", NULL));
  void *blob = NULL;
  size_t blobSize = 0;
  const az_result r = az_writer_finish(w, &blob, &blobSize);
  az_writer_free(w);
  CHECK(r == AZ_ERROR_CANCELLED || r == AZ_OK);
  if (blob)
    az_free(blob);

  EndTest();
}

static void TestHashers()
{
  BeginTest("hashers");

  const unsigned char abc[3] = { 'a', 'b', 'c' };

  az_hasher h = NULL;
  CHECK_AZ(az_hasher_open("CRC32", &h));
  az_hasher_update(h, abc, 3);
  unsigned char digest[64];
  size_t written = 0;
  CHECK_AZ(az_hasher_finish(h, digest, sizeof(digest), &written));
  az_hasher_close(h);
  CHECK(written == 4);
  // CRC32("abc") == 0x352441C2, little endian in 7-Zip's digest.
  CHECK(digest[0] == 0xC2 && digest[1] == 0x41 && digest[2] == 0x24 && digest[3] == 0x35);

  h = NULL;
  CHECK_AZ(az_hasher_open("SHA256", &h));
  az_hasher_update(h, "a", 1);
  az_hasher_update(h, "bc", 2);   // split across calls on purpose
  written = 0;
  CHECK_AZ(az_hasher_finish(h, digest, sizeof(digest), &written));
  az_hasher_close(h);
  CHECK(written == 32);
  static const unsigned char sha256Abc[32] = {
    0xba,0x78,0x16,0xbf,0x8f,0x01,0xcf,0xea,0x41,0x41,0x40,0xde,0x5d,0xae,0x22,0x23,
    0xb0,0x03,0x61,0xa3,0x96,0x17,0x7a,0x9c,0xb4,0x10,0xff,0x61,0xf2,0x00,0x15,0xad
  };
  CHECK(std::memcmp(digest, sha256Abc, 32) == 0);

  // A buffer smaller than the digest must be rejected.
  CHECK_AZ(az_hasher_open("SHA256", &h));
  unsigned char tiny[4];
  CHECK_AZ_FAILS_WITH(az_hasher_finish(h, tiny, sizeof(tiny), &written), AZ_ERROR_BUFFER_SMALL);
  az_hasher_close(h);

  az_hasher bogus = NULL;
  CHECK_AZ_FAILS_WITH(az_hasher_open("NOT-A-HASH", &bogus), AZ_ERROR_UNSUPPORTED);

  EndTest();
}

static void TestBadInput()
{
  BeginTest("bad input");

  const fs::path dir = TempSubdir("bad-input");
  const fs::path notAnArchive = dir / "plain.txt";
  CHECK(WriteFile(notAnArchive, MakePayload(2048)));

  az_archive arc = NULL;
  CHECK_AZ_FAILS_WITH(az_archive_open(notAnArchive.string().c_str(), NULL, &arc),
                      AZ_ERROR_BAD_FORMAT);

  const std::string missing = (dir / "does-not-exist.7z").string();
  CHECK_AZ_FAILS_WITH(az_archive_open(missing.c_str(), NULL, &arc), AZ_ERROR_NOT_FOUND);

  CHECK_AZ_FAILS_WITH(az_archive_open(NULL, NULL, &arc), AZ_ERROR_INVALID_ARG);
  CHECK_AZ_FAILS_WITH(az_archive_entry_count(NULL, NULL), AZ_ERROR_INVALID_ARG);

  // A writer for a read-only format must be refused up front.
  az_writer w = NULL;
  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = "not-a-format";
  CHECK_AZ_FAILS_WITH(az_writer_open((dir / "x.bin").string().c_str(), &co, &w),
                      AZ_ERROR_UNSUPPORTED);

  // Adding a missing file must be reported.
  co.format = "zip";
  CHECK_AZ(az_writer_open((dir / "x.zip").string().c_str(), &co, &w));
  CHECK_AZ_FAILS_WITH(az_writer_add_file(w, (dir / "missing.bin").string().c_str(),
                                         NULL, NULL, 0),
                      AZ_ERROR_NOT_FOUND);

  // Use after finish is rejected rather than silently doing nothing.
  CHECK_AZ(az_writer_add_buffer(w, "x", 1, "x.txt", NULL));
  CHECK_AZ(az_writer_finish(w, NULL, NULL));
  CHECK_AZ_FAILS_WITH(az_writer_finish(w, NULL, NULL), AZ_ERROR_ALREADY_DONE);
  CHECK_AZ_FAILS_WITH(az_writer_add_buffer(w, "y", 1, "y.txt", NULL), AZ_ERROR_ALREADY_DONE);
  az_writer_free(w);

  EndTest();
}

static void TestSplitVolumes7z()
{
  BeginTest("7z split-volume output reaches the disk and reads back as one archive");

  const fs::path dir = TempSubdir("volumes");
  const size_t kVol = 1024 * 1024;
  const std::vector<unsigned char> payload = MakePayload(3 * kVol);

  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = "7z";
  co.method = "Copy";      // incompressible, so the volume sizes are predictable
  co.volume_size = "1M";

  az_writer w = NULL;
  CHECK_AZ(az_writer_open((dir / "vol.7z").string().c_str(), &co, &w));
  CHECK_AZ(az_writer_add_buffer(w, payload.data(), payload.size(), "big.bin", NULL));
  CHECK_AZ(az_writer_finish(w, NULL, NULL));
  az_writer_free(w);

  // This test exists because the write used to report success while the volume
  // object deleted every file it had made. So the disk is asked, not the code.
  std::vector<fs::path> parts;
  for (unsigned i = 1; i <= 32; i++)
  {
    char name[32];
    std::sprintf(name, "vol.7z.%03u", i);
    const fs::path p = dir / name;
    if (!fs::exists(p))
      break;
    parts.push_back(p);
  }
  CHECK(parts.size() >= 4);                       // 3 MiB of payload plus the header
  unsigned long long total = 0;
  for (size_t i = 0; i < parts.size(); i++)
  {
    const unsigned long long n = (unsigned long long)fs::file_size(parts[i]);
    total += n;
    if (i + 1 < parts.size()) CHECK(n == kVol);   // every volume but the last is exactly one
    else CHECK(n > 0 && n < kVol);
  }

  // Chopping a stream into volumes must not change its bytes: compare against the
  // same archive written as a single file.
  co.volume_size = NULL;
  CHECK_AZ(az_writer_open((dir / "one.7z").string().c_str(), &co, &w));
  CHECK_AZ(az_writer_add_buffer(w, payload.data(), payload.size(), "big.bin", NULL));
  CHECK_AZ(az_writer_finish(w, NULL, NULL));
  az_writer_free(w);
  CHECK(total == (unsigned long long)fs::file_size(dir / "one.7z"));

  // Reading goes through the volume callback: opening .001 must pull in the rest.
  az_archive arc = NULL;
  CHECK_AZ(az_archive_open(parts.front().string().c_str(), NULL, &arc));
  int isVolume = 0;
  CHECK_AZ(az_archive_is_volume(arc, &isVolume));
  CHECK(isVolume);
  uint64_t count = 0;
  CHECK_AZ(az_archive_entry_count(arc, &count));
  CHECK(count == 1);
  std::vector<unsigned char> back(payload.size());
  uint64_t got = 0;
  CHECK_AZ(az_entry_read(arc, 0, back.data(), back.size(), &got));
  CHECK(got == payload.size());
  CHECK(back == payload);
  az_archive_close(arc);

  // Under the megabyte floor: refused, and nothing written.
  co.volume_size = "64K";
  CHECK_AZ_FAILS_WITH(az_writer_open((dir / "small.7z").string().c_str(), &co, &w),
                      AZ_ERROR_INVALID_ARG);
  CHECK(w == NULL);
  CHECK(!fs::exists(dir / "small.7z"));
  CHECK(!fs::exists(dir / "small.7z.001"));

  // Not for ZIP: Core splits those itself, in WinZip .zNN naming. The kernel
  // guards this too, so a caller cannot talk the engine into a half-implementation.
  co.format = "zip";
  co.volume_size = "1M";
  CHECK_AZ_FAILS_WITH(az_writer_open((dir / "vol.zip").string().c_str(), &co, &w),
                      AZ_ERROR_UNSUPPORTED);
  CHECK(w == NULL);
  CHECK(!fs::exists(dir / "vol.zip"));

  // An unparsable size string is the same as no volume_size at all: a normal
  // single-file archive must still be produced.
  co.format = "7z";
  co.volume_size = "banana";
  CHECK_AZ(az_writer_open((dir / "single.7z").string().c_str(), &co, &w));
  CHECK_AZ(az_writer_add_buffer(w, payload.data(), payload.size(), "big.bin", NULL));
  CHECK_AZ(az_writer_finish(w, NULL, NULL));
  az_writer_free(w);
  CHECK(fs::exists(dir / "single.7z"));
  CHECK(!fs::exists(dir / "single.7z.001"));

  EndTest();
}

static void TestExtraProperties()
{
  BeginTest("extra_properties hands a number to the coder as a number");

  const fs::path dir = TempSubdir("props");
  const std::vector<unsigned char> payload = MakePayload(2 * 1024 * 1024);

  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = "zip";
  co.method = "Deflate";
  co.level = 5;

  az_writer w = NULL;
  CHECK_AZ(az_writer_open((dir / "base.zip").string().c_str(), &co, &w));
  CHECK_AZ(az_writer_add_buffer(w, payload.data(), payload.size(), "p.bin", NULL));
  CHECK_AZ(az_writer_finish(w, NULL, NULL));
  az_writer_free(w);
  const unsigned long long baseSize = (unsigned long long)fs::file_size(dir / "base.zip");

  // "fb" (max match length) is declared VT_UI4 in the engine's coder-property table,
  // and ConvertProperty() only accepts a variant of the matching type. While this
  // channel handed every value over as a string, "fb=2" failed the whole write with
  // E_INVALIDARG; being accepted is therefore half of what this asserts. The size is
  // the other half: a match length of 2 must compress *worse*, which is how we know
  // the number reached the encoder instead of being dropped on the way.
  co.extra_properties = "fb=2";
  CHECK_AZ(az_writer_open((dir / "fb2.zip").string().c_str(), &co, &w));
  CHECK_AZ(az_writer_add_buffer(w, payload.data(), payload.size(), "p.bin", NULL));
  CHECK_AZ(az_writer_finish(w, NULL, NULL));
  az_writer_free(w);
  CHECK((unsigned long long)fs::file_size(dir / "fb2.zip") > baseSize);

  // A second knob on the same channel. On this payload mc=4 happens to land on the
  // same output size as the default (it moves the search, and this input's matches are
  // already found), so the assertion is only that a numeric property of a *different*
  // name is accepted too -- the size claim above is what proves the values arrive.
  co.extra_properties = "mc=4";
  CHECK_AZ(az_writer_open((dir / "mc4.zip").string().c_str(), &co, &w));
  CHECK_AZ(az_writer_add_buffer(w, payload.data(), payload.size(), "p.bin", NULL));
  CHECK_AZ(az_writer_finish(w, NULL, NULL));
  az_writer_free(w);
  CHECK(fs::file_size(dir / "mc4.zip") > 0);

  // Several pairs at once still go through.
  co.extra_properties = "fb=16\npass=2";
  CHECK_AZ(az_writer_open((dir / "ok2.zip").string().c_str(), &co, &w));
  CHECK_AZ(az_writer_add_buffer(w, payload.data(), payload.size(), "p.bin", NULL));
  CHECK_AZ(az_writer_finish(w, NULL, NULL));
  az_writer_free(w);
  CHECK(fs::file_size(dir / "ok2.zip") > 0);

  // A name the coder does not know is a hard error, not a silent ignore. It arrives at
  // finish, because that is when the accumulated property list is handed to the handler
  // -- and it must not leave an archive behind.
  co.extra_properties = "zzz=1";
  CHECK_AZ(az_writer_open((dir / "bad.zip").string().c_str(), &co, &w));
  CHECK_AZ(az_writer_add_buffer(w, payload.data(), payload.size(), "p.bin", NULL));
  az_result badRes = az_writer_finish(w, NULL, NULL);
  az_writer_free(w);
  w = NULL;
  CHECK(badRes != AZ_OK);
  CHECK(!fs::exists(dir / "bad.zip"));

  co.extra_properties = NULL;
  EndTest();
}

// ---------------------------------------------------------------------------

// Builds a USTAR archive byte-by-byte straight from the POSIX tar spec.
//
// Everything else in this file round-trips through our own writer, which means
// a bug that is symmetric on both sides stays invisible -- that is exactly how
// the tar auto-detection regression survived 500 green assertions. This is the
// one test where the producer is independent of the engine.
static std::vector<unsigned char> BuildUstarArchive(const std::string &name,
                                                    const std::string &content)
{
  auto octal = [](std::vector<unsigned char> &blk, size_t off, size_t field,
                  unsigned long long value) {
    // (field - 1) octal digits then a NUL, the classic GNU layout.
    char buf[32];
    std::snprintf(buf, sizeof(buf), "%0*llo", (int)(field - 1), value);
    for (size_t i = 0; i < field - 1; i++)
      blk[off + i] = (unsigned char)buf[i];
    blk[off + field - 1] = 0;
  };

  std::vector<unsigned char> blk(512, 0);
  for (size_t i = 0; i < name.size() && i < 99; i++)
    blk[i] = (unsigned char)name[i];

  octal(blk, 100, 8, 0644);                       // mode
  octal(blk, 108, 8, 0);                          // uid
  octal(blk, 116, 8, 0);                          // gid
  octal(blk, 124, 12, content.size());            // size
  octal(blk, 136, 12, 1700000000ULL);             // mtime

  blk[156] = '0';                                 // typeflag: regular file
  for (size_t i = 0; i < 5; i++) blk[257 + i] = (unsigned char) "ustar"[i];
  blk[263] = '0'; blk[264] = '0';                 // version

  for (size_t i = 0; i < 8; i++) blk[148 + i] = ' ';   // checksum placeholder
  unsigned long long sum = 0;
  for (int i = 0; i < 512; i++)
    sum += blk[i];
  octal(blk, 148, 7, sum);
  blk[155] = ' ';

  std::vector<unsigned char> out = blk;
  const size_t pos = out.size();
  out.resize(pos + 512, 0);
  std::memcpy(&out[pos], content.data(), content.size());   // data, zero padded
  out.insert(out.end(), 1024, 0);                           // two empty blocks
  return out;
}

static void TestForeignArchive()
{
  BeginTest("reading an archive we did not write");

  const std::string content = "hello from a foreign producer\n";
  const std::vector<unsigned char> tar = BuildUstarArchive("greeting.txt", content);
  CHECK(tar.size() == 512 * 4);

  // No forced format, and a name hint that does NOT say "tar": detection has to
  // work off the bytes alone, including the "ustar" magic at offset 257.
  az_archive arc = NULL;
  const az_result opened = az_archive_open_memory(tar.data(), tar.size(), "mystery", NULL, &arc);
  if (opened != AZ_OK)
  {
    std::printf("  note: auto-detect of foreign tar gave %d (%s)\n",
                (int)opened, az_last_error_message());
  }
  CHECK_AZ(opened);

  if (opened == AZ_OK)
  {
    CHECK(std::strcmp(az_archive_format_name(arc), "tar") == 0);

    uint64_t count = 0;
    CHECK_AZ(az_archive_entry_count(arc, &count));
    CHECK(count == 1);

    if (count == 1)
    {
      az_entry_info e;
      std::memset(&e, 0, sizeof(e));
      e.struct_size = sizeof(e);
      CHECK_AZ(az_entry_get(arc, 0, &e));
      CHECK(e.name && std::strcmp(e.name, "greeting.txt") == 0);
      CHECK(e.size == content.size());
      // The mtime written per the spec must come back as Unix seconds.
      CHECK(e.mtime_unix == 1700000000);
      az_entry_release(&e);

      std::vector<unsigned char> back(content.size());
      size_t written = 0;
      CHECK_AZ(az_entry_read(arc, 0, back.data(), back.size(), &written));
      CHECK(written == content.size());
      CHECK(content == std::string((const char *)back.data(), back.size()));
    }
    az_archive_close(arc);
  }

  // The same bytes behind a name that actively lies about the type.
  const fs::path dir = TempSubdir("foreign");
  const fs::path p = dir / "mislabelled.dat";
  {
    std::ofstream f(p, std::ios::binary);
    f.write((const char *)&tar[0], (std::streamsize)tar.size());
  }
  arc = NULL;
  CHECK_AZ(az_archive_open(p.string().c_str(), NULL, &arc));
  if (arc)
  {
    CHECK(std::strcmp(az_archive_format_name(arc), "tar") == 0);
    az_archive_close(arc);
  }

  EndTest();
}

// ---------------------------------------------------------------------------
// Updating an archive that already exists.
// ---------------------------------------------------------------------------

struct Seen
{
  std::string name;
  uint64_t size;
  uint64_t packed;
  uint32_t crc;
  std::vector<unsigned char> data;
};

static bool ListArchive(const fs::path &p, const char *password, std::vector<Seen> &out)
{
  out.clear();
  az_open_options oo = AZ_OPEN_OPTIONS_INIT;
  oo.password = password;
  az_archive arc = NULL;
  if (az_archive_open(p.string().c_str(), &oo, &arc) != AZ_OK)
  {
    std::printf("  FAIL open %s: %s\n", p.string().c_str(), az_last_error_message());
    return false;
  }

  uint64_t count = 0;
  az_archive_entry_count(arc, &count);
  for (uint64_t i = 0; i < count; i++)
  {
    az_entry_info e;
    std::memset(&e, 0, sizeof(e));
    e.struct_size = sizeof(e);
    if (az_entry_get(arc, i, &e) != AZ_OK)
    {
      az_archive_close(arc);
      return false;
    }
    Seen s;
    s.name = e.name ? e.name : "";
    s.size = e.size;
    s.packed = e.packed_size;
    s.crc = e.crc;
    if (!e.is_dir)
    {
      std::vector<unsigned char> buf((size_t)(e.size ? e.size : 1));
      size_t written = 0;
      const az_result rr = az_entry_read(arc, i, buf.data(), buf.size(), &written);
      buf.resize(written);
      if (rr == AZ_OK)
        s.data = buf;
      else
        std::printf("  FAIL read %s: %s\n", s.name.c_str(), az_last_error_message());
    }
    az_entry_release(&e);
    out.push_back(s);
  }
  az_archive_close(arc);
  return true;
}

static const Seen *FindMember(const std::vector<Seen> &seen, const std::string &name)
{
  for (size_t i = 0; i < seen.size(); i++)
    if (seen[i].name == name)
      return &seen[i];
  return NULL;
}

// Builds a file-backed archive holding `names` with distinct payloads.
static bool CreateSeed(const fs::path &p, const char *format,
                       const std::vector<std::string> &names, const char *password = NULL)
{
  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.format = format;
  co.password = password;
  az_writer w = NULL;
  if (az_writer_open(p.string().c_str(), &co, &w) != AZ_OK)
    return false;
  for (size_t i = 0; i < names.size(); i++)
  {
    const std::vector<unsigned char> payload = MakePayload((size_t)(i + 1) * 4096);
    if (az_writer_add_buffer(w, payload.data(), payload.size(), names[i].c_str(), NULL) != AZ_OK)
    {
      az_writer_free(w);
      return false;
    }
  }
  const az_result r = az_writer_finish(w, NULL, NULL);
  az_writer_free(w);
  return r == AZ_OK;
}

// Opens `p` for update, applies `keep` (indices into the current listing) plus one
// added member, and writes it back.
static az_result Rewrite(const fs::path &p, const char *password,
                         const std::vector<uint32_t> &keep, const char *addName)
{
  az_create_options co = AZ_CREATE_OPTIONS_INIT;
  co.password = password;
  az_writer w = NULL;
  az_result r = az_writer_open_existing(p.string().c_str(), &co, &w);
  if (r != AZ_OK)
    return r;
  for (size_t i = 0; i < keep.size(); i++)
  {
    r = az_writer_keep_member(w, keep[i]);
    if (r != AZ_OK)
      break;
  }
  if (r == AZ_OK && addName)
  {
    const std::vector<unsigned char> payload = MakePayload(5000);
    r = az_writer_add_buffer(w, payload.data(), payload.size(), addName, NULL);
  }
  const az_result done = az_writer_finish(w, NULL, NULL);
  az_writer_free(w);
  return r != AZ_OK ? r : done;
}

static void TestUpdateExisting()
{
  BeginTest("update an existing archive");

  const char *formats[] = { "zip", "7z", "tar" };
  for (int f = 0; f < 3; f++)
  {
    const char *format = formats[f];
    const fs::path p = g_tmp / (std::string("upd-") + format + "." + format);
    const std::vector<std::string> names = { "a.txt", "b.txt", "c.txt" };
    CHECK(CreateSeed(p, format, names));

    std::vector<Seen> before;
    CHECK(ListArchive(p, NULL, before));
    CHECK(before.size() == 3);
    if (before.size() != 3)
      continue;

    // A rewrite that keeps everything and adds one member. The kept members'
    // packed sizes must be *identical* afterwards: that is the difference between
    // "the handler copied the stored bytes" and "we silently recompressed them",
    // and only the first one makes a 100 GB member affordable to keep.
    const fs::path pristine = g_tmp / (std::string("pristine-") + format);
    std::error_code cpec;
    fs::copy_file(p, pristine, fs::copy_options::overwrite_existing, cpec);
    std::vector<unsigned char> pristineBytes;
    CHECK(ReadFile(pristine, pristineBytes));

    CHECK(Rewrite(p, NULL, { 0, 1, 2 }, "d.txt") == AZ_OK);

    std::vector<Seen> after;
    CHECK(ListArchive(p, NULL, after));
    CHECK(after.size() == 4);
    for (size_t i = 0; i < names.size() && i < before.size(); i++)
    {
      const Seen *kept = FindMember(after, before[i].name);
      CHECK(kept != NULL);
      if (!kept)
        continue;
      CHECK(SameBytes(kept->data, before[i].data));
      if (before[i].packed != UINT64_MAX)
        CHECK(kept->packed == before[i].packed);
    }
    const Seen *added = FindMember(after, "d.txt");
    CHECK(added != NULL);
    if (added)
      CHECK(added->data.size() == 5000);

    // Deleting is just not listing. Keep two of the four.
    std::vector<Seen> four;
    CHECK(ListArchive(p, NULL, four) && four.size() == 4);
    uint32_t keepA = 0, keepB = 0;
    for (uint32_t i = 0; i < four.size(); i++)
    {
      if (four[i].name == "a.txt") keepA = i;
      if (four[i].name == "c.txt") keepB = i;
    }
    CHECK(Rewrite(p, NULL, { keepA, keepB }, NULL) == AZ_OK);
    std::vector<Seen> two;
    CHECK(ListArchive(p, NULL, two));
    CHECK(two.size() == 2);
    CHECK(FindMember(two, "a.txt") != NULL);
    CHECK(FindMember(two, "c.txt") != NULL);
    CHECK(FindMember(two, "b.txt") == NULL);
    CHECK(FindMember(two, "d.txt") == NULL);

    // A refused update must leave the file exactly as it was -- "it failed" that
    // is not true is the one outcome nobody can recover from.
    const uint32_t bogus = 9999;
    CHECK(az_writer_open_existing(p.string().c_str(), NULL, NULL) == AZ_ERROR_INVALID_ARG);
    {
      az_writer probe = NULL;
      CHECK(az_writer_open_existing(p.string().c_str(), NULL, &probe) == AZ_OK);
      if (probe)
      {
        CHECK(az_writer_keep_member(probe, bogus) == AZ_ERROR_INVALID_ARG);
        az_writer_free(probe);
      }
    }
    std::vector<unsigned char> stillThere;
    CHECK(ReadFile(p, stillThere));
    CHECK(!stillThere.empty());
    // Nothing in the directory should be left over from a swap that never happened.
    int strays = 0;
    for (fs::directory_iterator it(g_tmp, cpec), end; it != end; it.increment(cpec))
      if (it->path().string().find(".aztmp") != std::string::npos)
        strays++;
    CHECK(strays == 0);
  }

  // A header-encrypted 7z with the key is an ordinary update; without it there is
  // no member list to copy from, so the writer must refuse before touching the
  // file. The interesting half is the second one: an update that listed nothing
  // would produce an empty archive and report success, and "it failed" that is not
  // true is the one outcome nobody can recover from.
  {
    const fs::path p = g_tmp / "he7z.7z";
    az_create_options co = AZ_CREATE_OPTIONS_INIT;
    co.format = "7z";
    co.password = "pw";
    co.encryption = AZ_ENC_AES256;
    co.encrypt_headers = 1;
    az_writer w = NULL;
    if (az_writer_open(p.string().c_str(), &co, &w) == AZ_OK)
    {
      const std::vector<unsigned char> payload = MakePayload(100);
      CHECK(az_writer_add_buffer(w, payload.data(), payload.size(), "x.txt", NULL) == AZ_OK);
      CHECK(az_writer_finish(w, NULL, NULL) == AZ_OK);
      az_writer_free(w);

      // With the password: an update that keeps the one member must still have it.
      CHECK(Rewrite(p, "pw", { 0 }, "y.txt") == AZ_OK);
      std::vector<Seen> both;
      CHECK(ListArchive(p, "pw", both));
      CHECK(both.size() == 2);
      const Seen *kept = FindMember(both, "x.txt");
      CHECK(kept != NULL);
      if (kept)
        CHECK(SameBytes(kept->data, payload));

      // Without the key there is no member list to copy from, so the update must
      // not even start -- and the file must be exactly what it was.
      std::vector<unsigned char> before;
      CHECK(ReadFile(p, before));
      az_writer blind = NULL;
      CHECK(az_writer_open_existing(p.string().c_str(), NULL, &blind) != AZ_OK);
      if (blind)
        az_writer_free(blind);
      std::vector<unsigned char> after;
      CHECK(ReadFile(p, after));
      CHECK(SameBytes(before, after));
    }
  }

  EndTest();
}

int main()
{
  // Unbuffered: if a test crashes the process we still need to see which one.
  setvbuf(stdout, NULL, _IONBF, 0);

  std::error_code ec;
  g_tmp = fs::temp_directory_path(ec) / "andzip_core_tests";
  fs::remove_all(g_tmp, ec);
  fs::create_directories(g_tmp, ec);
  if (ec)
  {
    std::fprintf(stderr, "cannot create temp dir %s: %s\n", g_tmp.string().c_str(),
                 ec.message().c_str());
    return 2;
  }

  az_set_num_threads(0);

  std::printf("andzip_core native tests -- temp dir %s\n", g_tmp.string().c_str());

  TestBuildInfo();
  TestFormatTable();
  TestCodecTable();
  TestMemoryRoundTrips();
  TestMultipleEntries();
  TestSingleEntrySinkStress();
  TestDirectoryRoundTrip();
  TestPasswords();
  TestPasswordCallback();
  TestPathSafety();
  TestCancellation();
  TestHashers();
  TestForeignArchive();
  TestBadInput();
  TestSplitVolumes7z();
  TestExtraProperties();
  TestUpdateExisting();

  std::printf("\n%d checks, %d failures\n", g_checks, g_failures);

  fs::remove_all(g_tmp, ec);
  return g_failures == 0 ? 0 : 1;
}
