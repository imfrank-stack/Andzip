// azcore -- a thin command line front end over the andzip.h C ABI.
//
// It exists so the native layer can be exercised without going through .NET,
// and as a worked example of the ABI. It is not a product CLI.
//
//   azcore version
//   azcore formats
//   azcore codecs
//   azcore list    <archive> [-p pass]
//   azcore info    <archive> [-p pass]
//   azcore test    <archive> [-p pass]
//   azcore extract <archive> <dir> [-p pass] [--flatten] [--skip]
//   azcore create  <archive> [-f fmt] [-m method] [-x level] [-d dictMiB]
//                   [-p pass] [-v volSize] [-t threads] [-e name=value]...
//                   [--solid=on|off] [--headers] <input>...

#include "andzip/andzip.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace {

int Fail(const char *what, az_result r)
{
  std::fprintf(stderr, "azcore: %s failed (%d): %s\n", what, (int)r, az_last_error_message());
  return 1;
}

void Warn(const char *what, az_result r)
{
  // The engine also reports on the success path: a member that could not be read
  // is skipped and named, and the archive itself is fine. az_last_error_message()
  // carries that note either way, so print whenever there is one.
  const char *msg = az_last_error_message();
  if (msg && *msg)
    std::fprintf(stderr, "azcore: note on %s: %s\n", what, msg);
  else if (r != AZ_OK)
    std::fprintf(stderr, "azcore: %s failed (%d)\n", what, (int)r);
}

struct ProgressState
{
  uint64_t lastPercent;
};

az_result OnProgress(void *ctx, const az_progress *p)
{
  ProgressState *st = static_cast<ProgressState *>(ctx);
  uint64_t pct = 0;
  if (p->total_in)
    pct = p->bytes_in * 100 / p->total_in;
  if (pct != st->lastPercent && pct % 10 == 0)
  {
    st->lastPercent = pct;
    std::fprintf(stderr, "\r  %3llu%%  %s",
                 (unsigned long long)pct, p->current_entry ? p->current_entry : "");
  }
  return AZ_OK;
}

int CmdVersion()
{
  az_build_info info;
  info.struct_size = sizeof(info);
  const az_result r = az_get_build_info(&info);
  if (r != AZ_OK)
    return Fail("az_get_build_info", r);

  std::printf("andzip_core %s (ABI %u)\n", info.core_version, info.abi_version);
  std::printf("  engine      : %s\n", info.engine);
  std::printf("  config      : %s\n", info.build_config);
  std::printf("  formats     : %u\n", info.format_count);
  std::printf("  codecs      : %u\n", info.codec_count);
  std::printf("  zstd encoder: %s\n", info.has_zstd_encoder ? "yes" : "no");
  std::printf("  lz4 encoder : %s\n", info.has_lz4_encoder ? "yes" : "no");
  std::printf("  brotli enc  : %s\n", info.has_brotli_encoder ? "yes" : "no");
  return 0;
}

int CmdFormats()
{
  const uint32_t n = az_format_count();
  std::printf("%-14s %-6s %-6s %s\n", "NAME", "READ", "WRITE", "EXTENSION");
  for (uint32_t i = 0; i < n; i++)
  {
    az_format_info f;
    f.struct_size = sizeof(f);
    if (az_format_get(i, &f) != AZ_OK)
      continue;
    std::printf("%-14s %-6s %-6s %s\n", f.name, f.can_read ? "yes" : "-",
                f.can_write ? "yes" : "-", f.extension ? f.extension : "");
    az_format_release(&f);
  }
  return 0;
}

int CmdCodecs()
{
  const uint32_t n = az_codec_count();
  std::printf("%-16s %-10s %-6s %-6s %s\n", "NAME", "ID", "ENC", "DEC", "FILTER");
  for (uint32_t i = 0; i < n; i++)
  {
    az_codec_info c;
    c.struct_size = sizeof(c);
    if (az_codec_get(i, &c) != AZ_OK)
      continue;
    std::printf("%-16s %-10s %-6s %-6s %s\n", c.name, c.id_hex,
                c.can_encode ? "yes" : "-", c.can_decode ? "yes" : "-",
                c.is_filter ? "yes" : "-");
    az_codec_release(&c);
  }
  return 0;
}

int PrintEntries(az_archive arc)
{
  uint64_t count = 0;
  az_result r = az_archive_entry_count(arc, &count);
  if (r != AZ_OK)
    return Fail("az_archive_entry_count", r);

  std::printf("%-10s %-10s %-6s %-14s %s\n", "SIZE", "PACKED", "CRC", "METHOD", "NAME");
  for (uint64_t i = 0; i < count; i++)
  {
    az_entry_info e;
    e.struct_size = sizeof(e);
    r = az_entry_get(arc, i, &e);
    if (r != AZ_OK)
    {
      az_entry_release(&e);
      return Fail("az_entry_get", r);
    }

    char sizeBuf[24], packBuf[24];
    if (e.size == UINT64_MAX)        std::snprintf(sizeBuf, sizeof(sizeBuf), "?");
    else                             std::snprintf(sizeBuf, sizeof(sizeBuf), "%llu", (unsigned long long)e.size);
    if (e.packed_size == UINT64_MAX) std::snprintf(packBuf, sizeof(packBuf), "?");
    else                             std::snprintf(packBuf, sizeof(packBuf), "%llu", (unsigned long long)e.packed_size);

    std::printf("%-10s %-10s %08X %-14s %s%s%s\n", sizeBuf, packBuf, e.crc,
                e.method ? e.method : "-", e.name,
                e.is_encrypted ? " [encrypted]" : "", e.is_dir ? " [dir]" : "");
    az_entry_release(&e);
  }
  std::printf("%llu entries\n", (unsigned long long)count);
  return 0;
}

az_result OpenArchive(const char *path, const char *password, az_archive *out)
{
  az_callbacks cb = AZ_CALLBACKS_INIT;
  ProgressState st;
  st.lastPercent = 1000;
  cb.progress = OnProgress;
  cb.ctx = &st;

  az_open_options opts = AZ_OPEN_OPTIONS_INIT;
  opts.password = password;
  opts.callbacks = &cb;

  const az_result r = az_archive_open(path, &opts, out);
  std::fprintf(stderr, "\n");
  return r;
}

int CmdInfo(const char *path, const char *password)
{
  az_archive arc = NULL;
  az_result r = OpenArchive(path, password, &arc);
  if (r != AZ_OK)
    return Fail("az_archive_open", r);

  uint64_t count = 0, phy = 0;
  int32_t enc = 0, solid = 0, volume = 0;
  const char *comment = NULL;
  az_archive_entry_count(arc, &count);
  az_archive_physical_size(arc, &phy);
  az_archive_is_encrypted(arc, &enc);
  az_archive_is_solid(arc, &solid);
  az_archive_is_volume(arc, &volume);
  az_archive_comment(arc, &comment);

  std::printf("path       : %s\n", path);
  std::printf("format     : %s\n", az_archive_format_name(arc));
  std::printf("entries    : %llu\n", (unsigned long long)count);
  std::printf("phy size   : %llu\n", (unsigned long long)phy);
  std::printf("encrypted  : %s\n", enc ? "yes" : "no");
  std::printf("solid      : %s\n", solid ? "yes" : "no");
  std::printf("volume     : %s\n", volume ? "yes" : "no");
  if (comment && *comment)
    std::printf("comment    : %s\n", comment);

  az_archive_close(arc);
  return 0;
}

int CmdList(const char *path, const char *password)
{
  az_archive arc = NULL;
  az_result r = OpenArchive(path, password, &arc);
  if (r != AZ_OK)
    return Fail("az_archive_open", r);

  std::printf("format: %s\n", az_archive_format_name(arc));
  const int rc = PrintEntries(arc);
  az_archive_close(arc);
  return rc;
}

int CmdTest(const char *path, const char *password)
{
  az_archive arc = NULL;
  az_result r = OpenArchive(path, password, &arc);
  if (r != AZ_OK)
    return Fail("az_archive_open", r);

  az_callbacks cb = AZ_CALLBACKS_INIT;
  ProgressState st;
  st.lastPercent = 1000;
  cb.progress = OnProgress;
  cb.ctx = &st;

  az_extract_options opts = AZ_EXTRACT_OPTIONS_INIT;
  opts.test_only = 1;
  opts.callbacks = &cb;

  r = az_archive_extract(arc, NULL, NULL, 0, &opts);
  std::fprintf(stderr, "\n");
  az_archive_close(arc);
  if (r != AZ_OK)
    return Fail("az_archive_extract(test)", r);

  std::printf("ok: all members decompressed and verified\n");
  return 0;
}

int CmdExtract(const char *path, const char *dir, const char *password,
               bool flatten, bool skip)
{
  az_archive arc = NULL;
  az_result r = OpenArchive(path, password, &arc);
  if (r != AZ_OK)
    return Fail("az_archive_open", r);

  az_callbacks cb = AZ_CALLBACKS_INIT;
  ProgressState st;
  st.lastPercent = 1000;
  cb.progress = OnProgress;
  cb.ctx = &st;

  az_extract_options opts = AZ_EXTRACT_OPTIONS_INIT;
  opts.password = password;
  opts.callbacks = &cb;
  opts.flatten = flatten ? 1 : 0;
  if (skip)
    opts.overwrite = AZ_OVERWRITE_SKIP;

  r = az_archive_extract(arc, dir, NULL, 0, &opts);
  std::fprintf(stderr, "\n");
  Warn("extract", r);
  az_archive_close(arc);
  if (r != AZ_OK)
    return Fail("az_archive_extract", r);

  std::printf("extracted to %s\n", dir);
  return 0;
}

struct CreateArgs
{
  const char *format;
  const char *method;
  int level;
  const char *password;
  uint32_t dictMiB;
  const char *volumeSize;
  int solid;
  int headers;
  int prealloc;
  int shareLocked;
  uint32_t threads;
  // Newline separated "name=value" straight into the engine property table,
  // so the CLI can try a 7-Zip property that has no structured option yet.
  std::string extra;
};

int CmdCreate(const char *dest, const CreateArgs &args, const std::vector<const char *> &inputs)
{
  az_callbacks cb = AZ_CALLBACKS_INIT;
  ProgressState st;
  st.lastPercent = 1000;
  cb.progress = OnProgress;
  cb.ctx = &st;

  az_create_options opts = AZ_CREATE_OPTIONS_INIT;
  opts.format = args.format;
  opts.method = args.method;
  opts.level = args.level;
  opts.dict_size_mib = args.dictMiB;
  opts.volume_size = args.volumeSize;
  opts.solid = args.solid;
  opts.preallocate = args.prealloc;
  opts.share_locked = args.shareLocked;
  opts.num_threads = args.threads;
  opts.extra_properties = args.extra.empty() ? NULL : args.extra.c_str();
  opts.callbacks = &cb;
  if (args.password && *args.password)
  {
    opts.password = args.password;
    opts.encryption = AZ_ENC_DEFAULT;
    opts.encrypt_headers = args.headers;
  }

  az_writer w = NULL;
  az_result r = az_writer_open(dest, &opts, &w);
  if (r != AZ_OK)
    return Fail("az_writer_open", r);

  for (size_t i = 0; i < inputs.size(); i++)
  {
    r = az_writer_add_file(w, inputs[i], NULL, NULL, 1);
    if (r != AZ_OK)
    {
      az_writer_free(w);
      return Fail(inputs[i], r);
    }
  }

  r = az_writer_finish(w, NULL, NULL);
  std::fprintf(stderr, "\n");
  Warn("create", r);
  az_writer_free(w);
  if (r != AZ_OK)
    return Fail("az_writer_finish", r);

  std::printf("created %s\n", dest);
  return 0;
}

void Usage()
{
  std::fprintf(stderr,
    "usage:\n"
    "  azcore version | formats | codecs\n"
    "  azcore list    <archive> [-p pass]\n"
    "  azcore info    <archive> [-p pass]\n"
    "  azcore test    <archive> [-p pass]\n"
    "  azcore extract <archive> <dir> [-p pass] [--flatten] [--skip]\n"
    "  azcore create  <archive> [-f fmt] [-m method] [-x level] [-d dictMiB]\n"
    "                           [-p pass] [--headers] [-v size] [--solid=on|off]\n"
    "                           [-t threads] [-e name=value]... \n"
    "                           [--prealloc] [--share-locked] <input>...\n"
    "  -e passes a 7-Zip property straight through (repeatable), e.g.\n"
    "     -e fb=273 -e mf=bt4. -t sets num_threads (0 = engine default).\n");
}

} // namespace

int main(int argc, char **argv)
{
  if (argc < 2)
  {
    Usage();
    return 2;
  }

  const std::string cmd = argv[1];

  if (cmd == "version")  return CmdVersion();
  if (cmd == "formats")  return CmdFormats();
  if (cmd == "codecs")   return CmdCodecs();

  if (cmd == "list" || cmd == "info" || cmd == "test")
  {
    if (argc < 3)
    {
      Usage();
      return 2;
    }
    const char *password = NULL;
    for (int i = 3; i < argc; i++)
      if (!std::strcmp(argv[i], "-p") && i + 1 < argc)
        password = argv[++i];

    if (cmd == "list") return CmdList(argv[2], password);
    if (cmd == "info") return CmdInfo(argv[2], password);
    return CmdTest(argv[2], password);
  }

  if (cmd == "extract")
  {
    if (argc < 4)
    {
      Usage();
      return 2;
    }
    const char *password = NULL;
    bool flatten = false, skip = false;
    for (int i = 4; i < argc; i++)
    {
      if (!std::strcmp(argv[i], "-p") && i + 1 < argc) password = argv[++i];
      else if (!std::strcmp(argv[i], "--flatten"))     flatten = true;
      else if (!std::strcmp(argv[i], "--skip"))         skip = true;
    }
    return CmdExtract(argv[2], argv[3], password, flatten, skip);
  }

  if (cmd == "create")
  {
    if (argc < 4)
    {
      Usage();
      return 2;
    }
    CreateArgs args;
    args.format = "7z";
    args.method = NULL;
    args.level = -1;
    args.password = NULL;
    args.dictMiB = 0;
    args.volumeSize = NULL;
    args.solid = 1;
    args.headers = 0;
    args.prealloc = 0;
    args.shareLocked = 0;
    args.threads = 0;

    std::vector<const char *> inputs;
    for (int i = 3; i < argc; i++)
    {
      const char *a = argv[i];
      if (!std::strcmp(a, "-f") && i + 1 < argc)            args.format = argv[++i];
      else if (!std::strcmp(a, "-m") && i + 1 < argc)      args.method = argv[++i];
      else if (!std::strcmp(a, "-x") && i + 1 < argc)      args.level = std::atoi(argv[++i]);
      else if (!std::strcmp(a, "-d") && i + 1 < argc)      args.dictMiB = (uint32_t)std::atoi(argv[++i]);
      else if (!std::strcmp(a, "-p") && i + 1 < argc)      args.password = argv[++i];
      else if (!std::strcmp(a, "-v") && i + 1 < argc)      args.volumeSize = argv[++i];
      else if (!std::strcmp(a, "--headers"))               args.headers = 1;
      else if (!std::strcmp(a, "--prealloc"))              args.prealloc = 1;
      else if (!std::strcmp(a, "--share-locked"))          args.shareLocked = 1;
      else if (!std::strncmp(a, "--solid=", 8))           args.solid = std::strcmp(a + 8, "off") != 0;
      else if (!std::strcmp(a, "-t") && i + 1 < argc)     args.threads = (uint32_t)std::atoi(argv[++i]);
      // repeatable: -e fb=273 -e mf=bt4  ->  "fb=273\nmf=bt4"
      else if (!std::strcmp(a, "-e") && i + 1 < argc)
      {
        if (!args.extra.empty())
          args.extra += '\n';
        args.extra += argv[++i];
      }
      else                                                 inputs.push_back(a);
    }

    if (inputs.empty())
    {
      Usage();
      return 2;
    }
    return CmdCreate(argv[2], args, inputs);
  }

  Usage();
  return 2;
}
