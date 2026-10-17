// az_reader.cpp -- opening archives, reading entry metadata, extracting.
//
// Format detection mirrors what 7-Zip's own UI layer does (CPP/7zip/UI/Common/
// OpenArchive.cpp, deliberately not vendored here): score every handler with
// its IsArc probe against the first 64 KiB, prefer an extension match, then a
// short list of common formats, then everything else that can read.

#include "az_internal.h"

namespace az {

namespace {

const UInt32 kProbeSize = 1u << 16;

} // namespace

// The opaque handle the C ABI hands out.
struct ArchiveCtx
{
  CMyComPtr<IInArchive> Archive;
  CMyComPtr<IInStream> Stream;      // keeps the file (or the copied buffer) alive
  int FormatIndex;
  AString FormatName;
  UString PathHint;
  AString Password;
  az_callbacks Callbacks;
  UInt32 NumItems;

  bool ArcPropsLoaded;
  AString Comment;
  UInt64 PhySize;
  bool IsEncrypted;
  bool IsSolid;
  bool IsVolume;

  ArchiveCtx()
    : FormatIndex(-1), NumItems(0), ArcPropsLoaded(false), PhySize(0),
      IsEncrypted(false), IsSolid(false), IsVolume(false)
  { memset(&Callbacks, 0, sizeof(Callbacks)); }
};

namespace {

// ---------------------------------------------------------------------------
// Open callback: passwords, progress, split volumes.
// ---------------------------------------------------------------------------

class COpenCallback Z7_final
  : public IArchiveOpenCallback
  , public IArchiveOpenVolumeCallback
  , public ICryptoGetTextPassword
  , public CMyUnknownImp
{
  Z7_IFACES_IMP_UNK_3(IArchiveOpenCallback, IArchiveOpenVolumeCallback, ICryptoGetTextPassword)

  UString _baseDir;      // directory holding the archive, for sibling volumes
  UString _baseName;     // file name of the archive
  AString _password;
  az_callbacks _callbacks;
  ProgressSink _progress;
public:
  void Init(const UString &fullPath, const char *password, const az_callbacks *cb)
  {
    const std::filesystem::path p = UToPath(fullPath);
    std::filesystem::path dir = p.parent_path();
    if (dir.empty())
      dir = std::filesystem::path(".");
    _baseDir = PathToU(dir);
    _baseName = PathToU(p.filename());
    if (!_baseDir.IsEmpty() && _baseDir[_baseDir.Len() - 1] != WCHAR_PATH_SEPARATOR)
      _baseDir.Add_PathSepar();
    _password = password ? password : "";
    memset(&_callbacks, 0, sizeof(_callbacks));
    if (cb)
      _callbacks = *cb;
    _progress.Init(cb);
  }
};

// IArchiveOpenCallback
STDMETHODIMP COpenCallback::SetTotal(const UInt64 * /* files */, const UInt64 *bytes)
{
  if (bytes)
    _progress.SetTotal(*bytes);
  return S_OK;
}

STDMETHODIMP COpenCallback::SetCompleted(const UInt64 * /* files */, const UInt64 *bytes)
{
  if (bytes)
  {
    _progress.SetCompleted(*bytes);
    _progress.Report(AZ_OP_SCAN, 0, 0);
  }
  return S_OK;
}

// IArchiveOpenVolumeCallback -- lets a .7z.001 / .zip.001 set read as one
STDMETHODIMP COpenCallback::GetProperty(PROPID propID, PROPVARIANT *value)
{
  NWindows::NCOM::CPropVariant prop;
  switch (propID)
  {
    case kpidName:     prop = _baseName; break;
    case kpidIsVolume: prop = true; break;
    default: break;
  }
  prop.Detach(value);
  return S_OK;
}

STDMETHODIMP COpenCallback::GetStream(const wchar_t *name, IInStream **inStream)
{
  *inStream = NULL;
  if (!name)
    return S_FALSE;

  const UString full = _baseDir + UString(name);
  if (!NWindows::NFile::NFind::DoesFileExist_Raw(us2fs(full.Ptr())))
    return S_FALSE;

  CInFileStream *fileStream = new CInFileStream();
  CMyComPtr<IInStream> wrap(fileStream);
  if (!fileStream->Open(us2fs(full.Ptr())))
    return S_FALSE;

  *inStream = wrap.Detach();
  return S_OK;
}

// ICryptoGetTextPassword -- encrypted headers (7z with he=on, encrypted rar)
STDMETHODIMP COpenCallback::CryptoGetTextPassword(BSTR *password)
{
  Int32 defined = 0;
  return AcquirePassword(&_callbacks, _password.IsEmpty() ? NULL : _password.Ptr(),
                         &defined, password);
}

// ---------------------------------------------------------------------------
// Extract callback
// ---------------------------------------------------------------------------

class CExtractCallback Z7_final
  : public IArchiveExtractCallback
  , public IArchiveExtractCallbackMessage2
  , public ICryptoGetTextPassword
  , public CMyUnknownImp
{
  Z7_IFACES_IMP_UNK_3(IArchiveExtractCallback, IArchiveExtractCallbackMessage2, ICryptoGetTextPassword)
  Z7_IFACE_COM7_IMP(IProgress)

  IInArchive *_archive;
  UString _destDir;
  AString _password;
  az_callbacks _callbacks;
  ProgressSink _progress;

  az_overwrite _overwrite;
  bool _preserveMTime;
  bool _flatten;

  // Single-entry mode: the payload goes to one sink instead of the filesystem.
  ISequentialOutStream *_singleSink;
  bool _singleMode;

  UString _curEntry;                 // name as stored in the archive
  UString _curPath;                  // resolved destination path
  CMyComPtr<IOutStream> _curFileRef;
  COutFileStream *_curFile;          // raw view for SetMTime; owned by the ref
  Int64 _curMTime;
  bool _curIsDir;
  UInt64 _entryIndex;
  UInt64 _entryCount;

  void DescribeResult(Int32 opRes);

public:
  az_result Status;
  AString StatusDetail;

  CExtractCallback()
    : _archive(NULL), _overwrite(AZ_OVERWRITE_OVERWRITE), _preserveMTime(true),
      _flatten(false), _singleSink(NULL), _singleMode(false), _curFile(NULL),
      _curMTime(0), _curIsDir(false), _entryIndex(0), _entryCount(0),
      Status(AZ_OK) {}

  void Init(IInArchive *archive, const UString &destDir, const char *password,
             const az_extract_options *opts, const az_callbacks *fallbackCb)
  {
    _archive = archive;
    _destDir = destDir;
    _password = password ? password : "";
    _overwrite = AZ_OVERWRITE_OVERWRITE;
    _preserveMTime = true;
    _flatten = false;

    // The callback set captured at open time still applies to extraction
    // unless this call brings its own -- otherwise a password prompt
    // registered on open would be ignored when the archive is read.
    const az_callbacks *cb = fallbackCb;
    if (opts)
    {
      _overwrite = opts->overwrite;
      _preserveMTime = opts->preserve_mtime != 0;
      _flatten = opts->flatten != 0;
      if (opts->callbacks)
        cb = opts->callbacks;
    }
    memset(&_callbacks, 0, sizeof(_callbacks));
    if (cb)
      _callbacks = *cb;
    _progress.Init(cb);

    Status = AZ_OK;
    StatusDetail.Empty();
  }

  void SetEntryCount(UInt64 n) { _entryCount = n; }
  void SetSingleSink(ISequentialOutStream *sink) { _singleSink = sink; _singleMode = true; }
};

// IProgress
STDMETHODIMP CExtractCallback::SetTotal(UInt64 total)
{
  _progress.SetTotal(total);
  return S_OK;
}

STDMETHODIMP CExtractCallback::SetCompleted(const UInt64 *value)
{
  if (!value)
    return S_OK;
  _progress.SetCompleted(*value);
  if (_progress.Report(AZ_OP_EXTRACT, _entryIndex, _entryCount) != AZ_OK)
  {
    Status = AZ_ERROR_CANCELLED;
    StatusDetail = "cancelled by progress callback";
    return E_ABORT;
  }
  return S_OK;
}

// IArchiveExtractCallback
STDMETHODIMP CExtractCallback::PrepareOperation(Int32 askExtractMode)
{
  UNUSED_VAR(askExtractMode)
  return S_OK;
}

// IArchiveExtractCallbackMessage2
STDMETHODIMP CExtractCallback::ReportExtractResult(UInt32 indexType, UInt32 index, Int32 opRes)
{
  UNUSED_VAR(indexType)
  UNUSED_VAR(index)
  if (opRes != NArchive::NExtract::NOperationResult::kOK)
    SetOperationResult(opRes);
  return S_OK;
}

// ICryptoGetTextPassword
STDMETHODIMP CExtractCallback::CryptoGetTextPassword(BSTR *password)
{
  Int32 defined = 0;
  const HRESULT res = AcquirePassword(&_callbacks, _password.IsEmpty() ? NULL : _password.Ptr(),
                                      &defined, password);
  if (SUCCEEDED(res) && !defined)
  {
    // The engine treats a missing password as an empty one, which for an
    // encrypted member surfaces later as a confusing CRC failure.
    Status = AZ_ERROR_PASSWORD;
    StatusDetail = "archive is encrypted and no password was supplied";
  }
  return res;
}

void CExtractCallback::DescribeResult(Int32 opRes)
{
  using namespace NArchive::NExtract::NOperationResult;

  // For an encrypted member a data or CRC failure almost always means the key
  // was wrong: 7z without encrypted headers stores no separate password check,
  // so decrypting with the wrong key simply produces garbage that fails
  // verification later. Say "password" when that is the honest reading.
  bool encrypted = false;
  if (opRes == kDataError || opRes == kCRCError || opRes == kUnexpectedEnd)
  {
    PROPVARIANT prop;
    ::PropVariantInit(&prop);
    if (_archive
        && _archive->GetProperty((UInt32)_entryIndex, kpidEncrypted, &prop) == S_OK
        && prop.vt == VT_BOOL && prop.boolVal != VARIANT_FALSE)
      encrypted = true;
    ::PropVariantClear(&prop);
  }

  const AString where = _curEntry.IsEmpty() ? AString("?") : UToUtf8A(_curEntry);
  switch (opRes)
  {
    case kOK:                return;
    case kUnsupportedMethod: Status = AZ_ERROR_UNSUPPORTED;
                             StatusDetail = "unsupported compression method for '" + where + "'"; break;
    case kDataError:         Status = encrypted ? AZ_ERROR_PASSWORD : AZ_ERROR_BAD_FORMAT;
                             StatusDetail = (encrypted ? "wrong password for '" : "data error in '")
                                          + where + "'"; break;
    case kCRCError:          Status = encrypted ? AZ_ERROR_PASSWORD : AZ_ERROR_CRC;
                             StatusDetail = (encrypted ? "wrong password for '" : "CRC mismatch in '")
                                          + where + "'"; break;
    case kUnavailable:       Status = AZ_ERROR_NOT_FOUND;
                             StatusDetail = "data unavailable for '" + where + "'"; break;
    case kUnexpectedEnd:     Status = encrypted ? AZ_ERROR_PASSWORD : AZ_ERROR_BAD_FORMAT;
                             StatusDetail = (encrypted ? "wrong password for '" : "unexpected end of data in '")
                                          + where + "'"; break;
    case kDataAfterEnd:      Status = AZ_ERROR_BAD_FORMAT;
                             StatusDetail = "trailing data after '" + where + "'"; break;
    case kIsNotArc:          Status = AZ_ERROR_BAD_FORMAT;
                             StatusDetail = "not a recognised archive"; break;
    case kHeadersError:      Status = AZ_ERROR_BAD_FORMAT;
                             StatusDetail = "corrupt archive headers"; break;
    case kWrongPassword:     Status = AZ_ERROR_PASSWORD;
                             StatusDetail = "wrong password for '" + where + "'"; break;
    default:                 Status = AZ_ERROR_GENERIC;
                             StatusDetail = "extraction failed for '" + where + "'"; break;
  }
}

STDMETHODIMP CExtractCallback::GetStream(UInt32 index, ISequentialOutStream **outStream,
                                         Int32 askExtractMode) throw()
{
  *outStream = NULL;
  _curFileRef.Release();
  _curFile = NULL;
  _curIsDir = false;
  _curMTime = 0;
  _curEntry.Empty();
  _curPath.Empty();
  _entryIndex = index;

  if (Status == AZ_ERROR_CANCELLED)
    return E_ABORT;

  PROPVARIANT prop;
  ::PropVariantInit(&prop);

  if (_archive->GetProperty(index, kpidPath, &prop) == S_OK)
    _curEntry = PropToUString(prop);
  ::PropVariantClear(&prop);
  NormalizeEntryName(_curEntry);

  if (_curEntry.IsEmpty())
  {
    // Some containers (notably tar) carry no name for anonymous members.
    char buf[32];
    snprintf(buf, sizeof(buf), "%u", (unsigned)index);
    _curEntry = buf;
  }
  _progress.SetEntry(_curEntry);

  if (_archive->GetProperty(index, kpidIsDir, &prop) == S_OK && prop.vt == VT_BOOL)
    _curIsDir = (prop.boolVal != VARIANT_FALSE);
  ::PropVariantClear(&prop);
  if (!_curIsDir && EntryNameIsDir(_curEntry))
    _curIsDir = true;

  if (_archive->GetProperty(index, kpidMTime, &prop) == S_OK)
    _curMTime = FileTimePropToUnix(prop);
  ::PropVariantClear(&prop);

  if (askExtractMode != NArchive::NExtract::NAskMode::kExtract)
    return S_OK;   // kTest and kSkip need no output stream

  if (_singleMode)
  {
    // A COM [out] parameter: the handler takes one reference and drops it when
    // the item is done (the Zip decoder keeps it in a CMyComPtr and calls
    // Release() at the end of every file -- Archive/Zip/ZipHandler.cpp).
    // Handing back the raw pointer made that release the last one, so the sink
    // was deleted inside az_entry_read; the reader then asked a freed object
    // for its status and released it again, writing into memory the heap had
    // already recycled. A liveness counter measured that on every single-entry
    // read. What reaches the user is the second half: free-list damage paid for
    // on a later entry, as an access violation in an az_entry_read call whose
    // own entry had nothing wrong with it.
    _singleSink->AddRef();
    *outStream = _singleSink;
    return S_OK;
  }

  UString full;
  if (_flatten)
  {
    UString leaf = _curEntry;
    const int sep = leaf.ReverseFind_PathSepar();
    if (sep >= 0)
      leaf.Delete(0, (unsigned)sep + 1);
    if (leaf.IsEmpty())
      return S_OK;
    full = _destDir + WCHAR_PATH_SEPARATOR + leaf;
  }
  else if (!SafeJoin(_destDir, _curEntry, full))
    return S_OK;   // nothing usable after sanitising

  _curPath = full;

  std::error_code ec;
  if (_curIsDir)
  {
    std::filesystem::create_directories(UToPath(full), ec);
    return S_OK;
  }

  if (_overwrite != AZ_OVERWRITE_OVERWRITE && std::filesystem::exists(UToPath(full), ec))
  {
    if (_overwrite == AZ_OVERWRITE_SKIP)
      return S_OK;
    if (_overwrite == AZ_OVERWRITE_FAIL)
    {
      Status = AZ_ERROR_IO;
      StatusDetail = "target already exists: '" + UToUtf8A(full) + "'";
      return E_ABORT;
    }
    if (_overwrite == AZ_OVERWRITE_RENAME)
    {
      bool renamed = false;
      for (unsigned n = 1; n < 10000; n++)
      {
        char suffix[16];
        snprintf(suffix, sizeof(suffix), "~%u", n);
        const UString candidate = full + UString(suffix);
        ec.clear();
        if (!std::filesystem::exists(UToPath(candidate), ec))
        {
          full = candidate;
          _curPath = candidate;
          renamed = true;
          break;
        }
      }
      if (!renamed)
      {
        Status = AZ_ERROR_IO;
        StatusDetail = "no free name for '" + UToUtf8A(full) + "'";
        return E_ABORT;
      }
    }
  }

  const az_result mk = MakeDirTree(full);
  if (mk != AZ_OK)
  {
    Status = mk;
    StatusDetail = "cannot prepare directory for '" + UToUtf8A(full) + "'";
    return E_ABORT;
  }

  COutFileStream *fileStream = new COutFileStream();
  CMyComPtr<IOutStream> wrap(fileStream);
  if (!fileStream->Create_ALWAYS(us2fs(full.Ptr())))
  {
    Status = AZ_ERROR_IO;
    StatusDetail = "cannot create '" + UToUtf8A(full) + "'";
    return E_ABORT;
  }

  _curFileRef = wrap;
  _curFile = fileStream;
  *outStream = wrap.Detach();
  return S_OK;
}

STDMETHODIMP CExtractCallback::SetOperationResult(Int32 opRes) throw()
{
  if (Status == AZ_OK)
    DescribeResult(opRes);

  // Stamp mtime only once the stream has been fully written and closed.
  if (Status == AZ_OK && _preserveMTime && _curMTime != 0 && !_curIsDir && !_singleMode
      && _curFile)
  {
    FILETIME ft;
    if (NWindows::NTime::UnixTime64_To_FileTime(_curMTime, ft))
    {
      CFiTime t;
      AzFileTimeToFiTime(ft, t);
      _curFile->File.SetMTime(&t);
    }
  }

  _curFileRef.Release();
  _curFile = NULL;
  return (Status == AZ_ERROR_CANCELLED) ? E_ABORT : S_OK;
}

// ---------------------------------------------------------------------------
// Format detection
// ---------------------------------------------------------------------------

struct Candidate
{
  int index;
  int score;
};

// Scores every handler against the probe buffer, highest confidence first.
//
// Two separate conventions, both easy to get wrong:
//   - Format::SignatureOffset says where the signature bytes sit relative to
//     the archive start, so it applies to the memcmp only.
//   - IsArc(p, size) always receives p at the archive start; handlers reach
//     their own fields by internal offset (IsArc_Tar parses the header from p[0]
//     and IsArc_Ext jumps to the superblock at p+0x400). Handing it
//     p + SignatureOffset silently rejects tar, ext, ntfs, apfs and everything
//     else with a non-zero offset.
int ScoreOne(const Format &f, const Byte *buf, size_t size)
{
  // Raw signature comparison is the gate; IsArc is the confirmation on top.
  int sigScore = 0;
  if (f.SigSizes.Size() == 1 && f.SignatureOffset + f.SigSizes[0] <= size
      && memcmp(buf + f.SignatureOffset, (const Byte *)f.Signatures, f.SigSizes[0]) == 0)
    sigScore = 1 + (int)f.SigSizes[0];

  if (!f.IsArc || size == 0)
    return sigScore;

  // NEED_MORE on a 64 KiB probe means "plausible but unproven" -- keep it as a
  // weak candidate rather than dropping it, since a signature hit already
  // passed but IsArc may want a longer header.
  const UInt32 res = f.IsArc(buf, size);
  if (res == k_IsArc_Res_YES)
    return 1000 + sigScore;
  if (res == k_IsArc_Res_NEED_MORE && sigScore)
    return 500 + sigScore;
  if (res == k_IsArc_Res_NO && f.SigSizes.Size() == 0)
    return 0;
  return sigScore;
}

void ScoreFormats(const Byte *buf, size_t size, CObjectVector<Candidate> &scored)
{
  const CObjectVector<Format> &formats = Formats();
  FOR_VECTOR(i, formats)
  {
    const Format &f = formats[i];
    if (!f.CanRead)
      continue;

    // Handlers with no byte signature are content hazards: Base64 and Intel Hex
    // accept almost any text, so auto-detecting them turns a .txt file into a
    // one-member archive. 7-Zip's own front ends only reach those when the type
    // is forced or the extension matches, so require a signature here and let
    // BuildOpenOrder's extension pass handle the rest.
    if (f.Signatures.Size() == 0)
      continue;

    const int score = ScoreOne(f, buf, size);
    if (score > 0)
    {
      Candidate c;
      c.index = (int)i;
      c.score = score;
      scored.Add(c);
    }
  }
}

// True when `name` ends with `token`, on a dot boundary.
bool EndsWithExtension(const UString &name, const UString &token)
{
  if (token.IsEmpty() || name.Len() < token.Len())
    return false;
  if (MyStringCompareNoCase(name.RightPtr(token.Len()), token.Ptr()) != 0)
    return false;
  return name.Len() == token.Len() || name[name.Len() - token.Len() - 1] == L'.';
}

// Builds the order handlers are tried in.
//
// Only handlers that produced a positive signal get a chance to open the
// stream. That matches what 7-Zip's own front ends do, and it matters: handing
// an arbitrary file to all 60 registered handlers includes disk-image and
// filesystem parsers that read sizes straight out of the header, so the tail
// is both slow and a crash risk rather than a safety net.
void BuildOpenOrder(const UString &nameHint, const Byte *buf, size_t size,
                    CRecordVector<int> &order)
{
  const CObjectVector<Format> &formats = Formats();

  // CRecordVector has no membership test, and the list must stay in preference
  // order, so track what has been added separately.
  std::vector<char> seen(formats.Size() ? formats.Size() : 1, 0);
  auto add = [&](int idx) {
    if (idx < 0 || (unsigned)idx >= formats.Size() || seen[(unsigned)idx])
      return;
    seen[(unsigned)idx] = 1;
    order.Add(idx);
  };

  CObjectVector<Candidate> scored;
  ScoreFormats(buf, size, scored);

  // Longest signature match first: strongest signal available.
  for (unsigned a = 0; a < scored.Size(); a++)
    for (unsigned b = a + 1; b < scored.Size(); b++)
      if (scored[b].score > scored[a].score)
      {
        const Candidate t = scored[a];
        scored[a] = scored[b];
        scored[b] = t;
      }

  // Among signature hits, prefer the formats a user most often means.
  for (size_t k = 0; k < kFallbackFormatsCount; k++)
  {
    const int idx = FindFormatIndex(kFallbackFormats[k]);
    if (idx < 0)
      continue;
    for (unsigned i = 0; i < scored.Size(); i++)
      if (scored[i].index == idx)
      {
        add(idx);
        break;
      }
  }
  FOR_VECTOR(i, scored)
    add(scored[i].index);

  if (!order.IsEmpty())
    return;

  // Nothing scored, so fall back to the file name. Handlers that never see
  // their magic bytes still get a fair chance here.
  if (!nameHint.IsEmpty())
  {
    UString file = nameHint;
    const int sep = file.ReverseFind_PathSepar();
    if (sep >= 0)
      file.Delete(0, (unsigned)sep + 1);
    file.MakeLower_Ascii();

    FOR_VECTOR(i, formats)
    {
      if (!formats[i].CanRead || seen[i])
        continue;

      UString ext = formats[i].Ext;
      ext.MakeLower_Ascii();
      unsigned pos = 0;
      for (;;)
      {
        const int space = ext.Find(L' ', pos);
        UString token;
        if (space < 0) token = ext.Ptr(pos);
        else           token = ext.Mid(pos, (unsigned)space - pos);
        if (EndsWithExtension(file, token))
        {
          add((int)i);
          break;
        }
        if (space < 0)
          break;
        pos = (unsigned)space + 1;
        if (pos >= ext.Len())
          break;
      }
    }
  }
}

size_t ReadProbe(IInStream *stream, CByteBuffer &probe)
{
  UInt64 pos = 0;
  if (stream->Seek(0, STREAM_SEEK_SET, &pos) != S_OK)
    return 0;
  probe.Alloc(kProbeSize);
  UInt32 processed = 0;
  if (stream->Read(probe, kProbeSize, &processed) != S_OK)
    return 0;
  return processed;
}

az_result OpenStreamInternal(IInStream *stream, const UString &nameHint,
                             const UString &fullPathForVolumes,
                             const az_open_options *opts, ArchiveCtx **out)
{
  if (opts && opts->struct_size != sizeof(az_open_options))
    return Fail(AZ_ERROR_INVALID_ARG,
                "az_open_options.struct_size is %u, expected %u (ABI mismatch)",
                (unsigned)opts->struct_size, (unsigned)sizeof(az_open_options));

  CRecordVector<int> order;
  if (opts && opts->forced_format && *opts->forced_format)
  {
    const int idx = FindFormatIndex(opts->forced_format);
    if (idx < 0)
      return Fail(AZ_ERROR_UNSUPPORTED, "unknown format '%s'", opts->forced_format);
    order.Add(idx);
  }
  else
  {
    CByteBuffer probe;
    const size_t probeSize = ReadProbe(stream, probe);
    BuildOpenOrder(nameHint, probe, probeSize, order);
  }

  const char *password = (opts && opts->password) ? opts->password : NULL;
  const az_callbacks *cb = opts ? opts->callbacks : NULL;
  if (!CheckCallbacks(cb))
    return Fail(AZ_ERROR_INVALID_ARG,
                "az_callbacks.struct_size is %u, expected %u (ABI mismatch)",
                (unsigned)cb->struct_size, (unsigned)sizeof(az_callbacks));

  COpenCallback *openCb = new COpenCallback();
  CMyComPtr<IArchiveOpenCallback> openCbRef(openCb);
  openCb->Init(fullPathForVolumes.IsEmpty() ? nameHint : fullPathForVolumes, password, cb);

  HRESULT lastHardError = S_FALSE;
  AString lastHardContext;

  FOR_VECTOR(i, order)
  {
    CMyComPtr<IInArchive> arc;
    const HRESULT create = CreateInArchive(order[i], arc);
    if (create != S_OK)
      continue;

    UInt64 pos = 0;
    stream->Seek(0, STREAM_SEEK_SET, &pos);

    const UInt64 maxCheckStart = 0;
    const HRESULT res = arc->Open(stream, &maxCheckStart, openCb);
    if (res == S_OK)
    {
      ArchiveCtx *ctx = new ArchiveCtx();
      ctx->Archive = arc;
      ctx->Stream = stream;
      ctx->FormatIndex = order[i];
      ctx->FormatName = UToUtf8A(Formats()[order[i]].Name);
      ctx->PathHint = nameHint;
      if (password)
        ctx->Password = password;
      if (cb)
        ctx->Callbacks = *cb;

      UInt32 numItems = 0;
      if (arc->GetNumberOfItems(&numItems) != S_OK)
        numItems = 0;
      ctx->NumItems = numItems;

      *out = ctx;
      return Ok();
    }

    // S_FALSE means "not my format"; anything else is a real failure on a
    // format that did look right, which is the more useful thing to report.
    if (res != S_FALSE)
    {
      lastHardError = res;
      lastHardContext = UToUtf8A(Formats()[order[i]].Name);
    }
  }

  if (lastHardError != S_FALSE)
  {
    const AString name = UToUtf8A(nameHint);
    Fail(HrToAz(lastHardError), "cannot open '%s': the %s handler reported HRESULT 0x%08lX",
         name.Ptr(), lastHardContext.Ptr(), (unsigned long)lastHardError);
    return HrToAz(lastHardError);
  }

  return Fail(AZ_ERROR_BAD_FORMAT, "cannot open '%s': not a recognised archive format",
              UToUtf8A(nameHint).Ptr());
}

void LoadArchiveProps(ArchiveCtx *ctx)
{
  if (ctx->ArcPropsLoaded)
    return;
  ctx->ArcPropsLoaded = true;

  PROPVARIANT prop;
  ::PropVariantInit(&prop);

  if (ctx->Archive->GetArchiveProperty(kpidComment, &prop) == S_OK)
    ctx->Comment = UToUtf8A(PropToUString(prop));
  ::PropVariantClear(&prop);

  if (ctx->Archive->GetArchiveProperty(kpidPhySize, &prop) == S_OK && prop.vt == VT_UI8)
    ctx->PhySize = prop.uhVal.QuadPart;
  ::PropVariantClear(&prop);

  if (ctx->Archive->GetArchiveProperty(kpidEncrypted, &prop) == S_OK && prop.vt == VT_BOOL)
    ctx->IsEncrypted = (prop.boolVal != VARIANT_FALSE);
  ::PropVariantClear(&prop);

  if (ctx->Archive->GetArchiveProperty(kpidSolid, &prop) == S_OK && prop.vt == VT_BOOL)
    ctx->IsSolid = (prop.boolVal != VARIANT_FALSE);
  ::PropVariantClear(&prop);

  if (ctx->Archive->GetArchiveProperty(kpidIsVolume, &prop) == S_OK && prop.vt == VT_BOOL)
    ctx->IsVolume = (prop.boolVal != VARIANT_FALSE);
  ::PropVariantClear(&prop);

  // Encrypted headers leave kpidEncrypted unset; scan the members instead.
  if (!ctx->IsEncrypted)
  {
    for (UInt32 i = 0; i < ctx->NumItems; i++)
    {
      if (ctx->Archive->GetProperty(i, kpidEncrypted, &prop) == S_OK
          && prop.vt == VT_BOOL && prop.boolVal != VARIANT_FALSE)
      {
        ctx->IsEncrypted = true;
        ::PropVariantClear(&prop);
        break;
      }
      ::PropVariantClear(&prop);
    }
  }
}

} // namespace
} // namespace az

// ---------------------------------------------------------------------------
// C ABI -- open / close
// ---------------------------------------------------------------------------

AZ_API az_result AZ_CALL az_archive_open(const char *path_utf8,
                                         const az_open_options *opts,
                                         az_archive *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_open: out is NULL");
  *out = NULL;
  if (!path_utf8 || !*path_utf8)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_open: path is NULL or empty");

  const UString path = az::Utf8ToU(path_utf8);

  CInFileStream *fileStream = new CInFileStream();
  CMyComPtr<IInStream> stream(fileStream);
  if (!fileStream->Open(us2fs(path.Ptr())))
  {
    // The smart pointer owns fileStream; deleting it here would leave it
    // releasing a freed object on scope exit.
    return az::Fail(AZ_ERROR_NOT_FOUND, "cannot open '%s'", path_utf8);
  }

  az::ArchiveCtx *ctx = NULL;
  const az_result r = az::OpenStreamInternal(stream, path, path, opts, &ctx);
  if (r != AZ_OK)
    return r;

  *out = (az_archive)ctx;
  return AZ_OK;
}

AZ_API az_result AZ_CALL az_archive_open_memory(const void *data, size_t size,
                                                const char *name_hint_utf8,
                                                const az_open_options *opts,
                                                az_archive *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_open_memory: out is NULL");
  *out = NULL;
  if (!data || size == 0)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_open_memory: empty buffer");

  // Copy the payload so the caller may free its buffer as soon as we return.
  // CReferenceBuf owns the bytes; CBufInStream keeps it alive through `ref`.
  // The smart pointer holds IUnknown, not CReferenceBuf: 7-Zip's class macro
  // makes AddRef/Release private on the concrete type.
  CReferenceBuf *bufRef = new CReferenceBuf();
  CMyComPtr<IUnknown> bufOwner(bufRef);
  try
  {
    bufRef->Buf.CopyFrom((const Byte *)data, size);
  }
  catch (const std::bad_alloc &)
  {
    return az::Fail(AZ_ERROR_OUT_OF_MEMORY, "cannot buffer %llu bytes", (unsigned long long)size);
  }

  CBufInStream *memIn = new CBufInStream();
  CMyComPtr<IInStream> stream(memIn);
  memIn->Init(bufRef);

  const UString hint = az::Utf8ToU(name_hint_utf8 ? name_hint_utf8 : "");
  az::ArchiveCtx *ctx = NULL;
  const az_result r = az::OpenStreamInternal(stream, hint, UString(), opts, &ctx);
  if (r != AZ_OK)
    return r;

  *out = (az_archive)ctx;
  return AZ_OK;
}

AZ_API void AZ_CALL az_archive_close(az_archive archive)
{
  if (!archive)
    return;
  az::ArchiveCtx *ctx = (az::ArchiveCtx *)archive;
  if (ctx->Archive)
    ctx->Archive->Close();
  delete ctx;
}

AZ_API const char *AZ_CALL az_archive_format_name(az_archive archive)
{
  if (!archive)
    return "";
  return ((az::ArchiveCtx *)archive)->FormatName.Ptr();
}

AZ_API az_result AZ_CALL az_archive_entry_count(az_archive archive, uint64_t *out)
{
  if (!archive || !out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_entry_count: NULL argument");
  *out = ((az::ArchiveCtx *)archive)->NumItems;
  return az::Ok();
}

AZ_API az_result AZ_CALL az_archive_comment(az_archive archive, const char **out_utf8)
{
  if (!archive || !out_utf8)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_comment: NULL argument");
  az::ArchiveCtx *ctx = (az::ArchiveCtx *)archive;
  az::LoadArchiveProps(ctx);
  *out_utf8 = ctx->Comment.IsEmpty() ? NULL : ctx->Comment.Ptr();
  return az::Ok();
}

AZ_API az_result AZ_CALL az_archive_physical_size(az_archive archive, uint64_t *out)
{
  if (!archive || !out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_physical_size: NULL argument");
  az::ArchiveCtx *ctx = (az::ArchiveCtx *)archive;
  az::LoadArchiveProps(ctx);
  *out = ctx->PhySize;
  return az::Ok();
}

AZ_API az_result AZ_CALL az_archive_is_encrypted(az_archive archive, int32_t *out)
{
  if (!archive || !out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_is_encrypted: NULL argument");
  az::ArchiveCtx *ctx = (az::ArchiveCtx *)archive;
  az::LoadArchiveProps(ctx);
  *out = ctx->IsEncrypted ? 1 : 0;
  return az::Ok();
}

AZ_API az_result AZ_CALL az_archive_is_solid(az_archive archive, int32_t *out)
{
  if (!archive || !out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_is_solid: NULL argument");
  az::ArchiveCtx *ctx = (az::ArchiveCtx *)archive;
  az::LoadArchiveProps(ctx);
  *out = ctx->IsSolid ? 1 : 0;
  return az::Ok();
}

AZ_API az_result AZ_CALL az_archive_is_volume(az_archive archive, int32_t *out)
{
  if (!archive || !out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_is_volume: NULL argument");
  az::ArchiveCtx *ctx = (az::ArchiveCtx *)archive;
  az::LoadArchiveProps(ctx);
  *out = ctx->IsVolume ? 1 : 0;
  return az::Ok();
}

// ---------------------------------------------------------------------------
// C ABI -- entries
// ---------------------------------------------------------------------------

AZ_API az_result AZ_CALL az_entry_get(az_archive archive, uint64_t index, az_entry_info *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_entry_get: out is NULL");
  if (out->struct_size != sizeof(az_entry_info))
    return az::Fail(AZ_ERROR_INVALID_ARG,
                    "az_entry_info.struct_size is %u, expected %u (ABI mismatch)",
                    (unsigned)out->struct_size, (unsigned)sizeof(az_entry_info));
  if (!archive)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_entry_get: archive is NULL");

  az::ArchiveCtx *ctx = (az::ArchiveCtx *)archive;
  if (index >= ctx->NumItems)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_entry_get: index %llu out of range (%u entries)",
                    (unsigned long long)index, (unsigned)ctx->NumItems);

  std::unique_ptr<az::StringBag> bag(new az::StringBag());
  memset(out, 0, sizeof(*out));
  out->struct_size = sizeof(az_entry_info);
  out->index = index;
  out->size = UINT64_MAX;
  out->packed_size = UINT64_MAX;

  const UInt32 i = (UInt32)index;
  PROPVARIANT prop;
  ::PropVariantInit(&prop);

  auto get = [&](PROPID id) -> bool {
    ::PropVariantClear(&prop);
    return ctx->Archive->GetProperty(i, id, &prop) == S_OK;
  };

  UString name;
  if (get(kpidPath))
    name = az::PropToUString(prop);
  if (name.IsEmpty() && get(kpidName))
    name = az::PropToUString(prop);
  az::NormalizeEntryName(name);
  out->name = bag->Add(az::UToStd(name));

  if (get(kpidMethod))
    out->method = bag->AddOptional(az::PropToUString(prop));
  if (get(kpidComment))
    out->comment = bag->AddOptional(az::PropToUString(prop));

  if (get(kpidSize) && prop.vt == VT_UI8)
    out->size = prop.uhVal.QuadPart;
  else if (prop.vt == VT_UI4)
    out->size = prop.ulVal;

  if (get(kpidPackSize) && prop.vt == VT_UI8)
    out->packed_size = prop.uhVal.QuadPart;
  else if (prop.vt == VT_UI4)
    out->packed_size = prop.ulVal;

  if (get(kpidCRC) && prop.vt == VT_UI4)
    out->crc = prop.ulVal;

  bool isDir = false;
  if (get(kpidIsDir) && prop.vt == VT_BOOL)
    isDir = (prop.boolVal != VARIANT_FALSE);
  if (!isDir && az::EntryNameIsDir(name))
    isDir = true;
  out->is_dir = isDir ? 1 : 0;

  if (get(kpidEncrypted) && prop.vt == VT_BOOL)
    out->is_encrypted = (prop.boolVal != VARIANT_FALSE) ? 1 : 0;
  if (get(kpidIsAnti) && prop.vt == VT_BOOL)
    out->is_anti = (prop.boolVal != VARIANT_FALSE) ? 1 : 0;

  if (get(kpidAttrib) && prop.vt == VT_UI4)
  {
    out->attributes = prop.ulVal;
    out->has_attributes = 1;
  }
  if (get(kpidPosixAttrib) && prop.vt == VT_UI4)
    out->posix_mode = prop.ulVal;

  if (get(kpidMTime))
    out->mtime_unix = az::FileTimePropToUnix(prop);
  if (get(kpidCTime))
    out->ctime_unix = az::FileTimePropToUnix(prop);
  if (get(kpidATime))
    out->atime_unix = az::FileTimePropToUnix(prop);

  if (get(kpidNumSubFiles) && prop.vt == VT_UI8)
    out->num_sub_files = prop.uhVal.QuadPart;

  ::PropVariantClear(&prop);
  out->_bag = bag.release();
  return az::Ok();
}

AZ_API void AZ_CALL az_entry_release(az_entry_info *info)
{
  if (!info)
    return;
  delete static_cast<az::StringBag *>(info->_bag);
  info->_bag = NULL;
}

// ---------------------------------------------------------------------------
// C ABI -- extraction
// ---------------------------------------------------------------------------

AZ_API az_result AZ_CALL az_archive_extract(az_archive archive, const char *dest_dir_utf8,
                                            const uint64_t *indices, size_t count,
                                            const az_extract_options *opts)
{
  if (!archive)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_extract: archive is NULL");

  az::ArchiveCtx *ctx = (az::ArchiveCtx *)archive;
  if (opts && opts->struct_size != sizeof(az_extract_options))
    return az::Fail(AZ_ERROR_INVALID_ARG,
                    "az_extract_options.struct_size is %u, expected %u (ABI mismatch)",
                    (unsigned)opts->struct_size, (unsigned)sizeof(az_extract_options));
  if (opts && !az::CheckCallbacks(opts->callbacks))
    return az::Fail(AZ_ERROR_INVALID_ARG,
                    "az_callbacks.struct_size is %u, expected %u (ABI mismatch)",
                    (unsigned)opts->callbacks->struct_size,
                    (unsigned)sizeof(az_callbacks));

  const bool testOnly = opts && opts->test_only;
  UString destDir;
  if (!testOnly)
  {
    if (!dest_dir_utf8 || !*dest_dir_utf8)
      return az::Fail(AZ_ERROR_INVALID_ARG, "az_archive_extract: destination directory is empty");
    destDir = az::Utf8ToU(dest_dir_utf8);
    std::error_code ec;
    if (!std::filesystem::exists(az::UToPath(destDir), ec))
      std::filesystem::create_directories(az::UToPath(destDir), ec);
  }

  const char *password = (opts && opts->password) ? opts->password
                         : (ctx->Password.IsEmpty() ? NULL : ctx->Password.Ptr());

  az::CExtractCallback *cb = new az::CExtractCallback();
  CMyComPtr<IArchiveExtractCallback> cbRef(cb);
  cb->Init(ctx->Archive, destDir, password, opts, &ctx->Callbacks);

  // Build the index list. Solid archives are cheapest to walk in order.
  CRecordVector<UInt32> wanted;
  if (indices && count > 0)
  {
    for (size_t i = 0; i < count; i++)
    {
      if (indices[i] >= ctx->NumItems)
        return az::Fail(AZ_ERROR_INVALID_ARG,
                        "az_archive_extract: index %llu out of range (%u entries)",
                        (unsigned long long)indices[i], (unsigned)ctx->NumItems);
      wanted.Add((UInt32)indices[i]);
    }
    // Solid archives are cheapest to walk in ascending order.
    if (wanted.Size() > 1)
      std::sort(&wanted[0], &wanted[0] + wanted.Size());
    cb->SetEntryCount(wanted.Size());
  }
  else
    cb->SetEntryCount(ctx->NumItems);

  const UInt32 numItems = wanted.IsEmpty() ? (UInt32)(Int32)-1 : (UInt32)wanted.Size();
  const HRESULT res = ctx->Archive->Extract(wanted.IsEmpty() ? NULL : &wanted[0],
                                            numItems, testOnly ? 1 : 0, cb);

  if (cb->Status != AZ_OK)
    return az::Fail(cb->Status, "%s", cb->StatusDetail.Ptr());
  if (res != S_OK)
    return az::FailHr(res, AZ_ERROR_GENERIC, "extracting archive");
  return az::Ok();
}

namespace {

struct BufferSinkCtx
{
  Byte *buf;
  size_t cap;
  size_t written;
};

az_result BufferSink(void *ctx, const void *data, size_t size)
{
  BufferSinkCtx *s = (BufferSinkCtx *)ctx;
  if (s->written + size > s->cap)
  {
    az::SetError(AZ_ERROR_BUFFER_SMALL,
                 "entry does not fit: needed at least %llu more bytes than the %llu byte buffer",
                 (unsigned long long)(s->written + size - s->cap), (unsigned long long)s->cap);
    return AZ_ERROR_BUFFER_SMALL;
  }
  memcpy(s->buf + s->written, data, size);
  s->written += size;
  return AZ_OK;
}

az_result ExtractSingleEntry(az::ArchiveCtx *ctx, uint64_t index,
                             az::CSinkOutStream *sink, const char *passwordOverride)
{
  if (index >= ctx->NumItems)
    return az::Fail(AZ_ERROR_INVALID_ARG, "entry index %llu out of range (%u entries)",
                    (unsigned long long)index, (unsigned)ctx->NumItems);

  const char *password = passwordOverride ? passwordOverride
                         : (ctx->Password.IsEmpty() ? NULL : ctx->Password.Ptr());

  az::CExtractCallback *cb = new az::CExtractCallback();
  CMyComPtr<IArchiveExtractCallback> cbRef(cb);
  cb->Init(ctx->Archive, UString(), password, NULL, &ctx->Callbacks);
  cb->SetSingleSink(sink);
  cb->SetEntryCount(1);

  const UInt32 idx = (UInt32)index;
  const HRESULT res = ctx->Archive->Extract(&idx, 1, 0, cb);

  // A sink that refused the payload -- a caller buffer that turned out to be
  // too small, for instance -- knows the real reason. The engine's E_ABORT is
  // only the echo of that, so report the sink's status first and keep the
  // message the sink already recorded.
  if (sink->Status() != AZ_OK)
    return sink->Status();
  if (cb->Status != AZ_OK)
    return az::Fail(cb->Status, "%s", cb->StatusDetail.Ptr());
  if (res != S_OK)
    return az::FailHr(res, AZ_ERROR_GENERIC, "extracting entry");
  return az::Ok();
}

} // namespace

AZ_API az_result AZ_CALL az_entry_read(az_archive archive, uint64_t index,
                                       void *buffer, size_t capacity, size_t *written_out)
{
  if (!archive)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_entry_read: archive is NULL");
  if (!buffer && capacity != 0)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_entry_read: buffer is NULL");
  if (written_out)
    *written_out = 0;

  az::ArchiveCtx *ctx = (az::ArchiveCtx *)archive;

  BufferSinkCtx sink;
  sink.buf = (Byte *)buffer;
  sink.cap = capacity;
  sink.written = 0;

  az::CSinkOutStream *stream = new az::CSinkOutStream();
  CMyComPtr<ISequentialOutStream> streamRef(stream);
  stream->Init(BufferSink, &sink);

  const az_result r = ExtractSingleEntry(ctx, index, stream, NULL);
  if (written_out)
    *written_out = sink.written;
  if (r != AZ_OK)
    return r;
  if (stream->Status() != AZ_OK)
    return stream->Status();
  return az::Ok();
}

AZ_API az_result AZ_CALL az_entry_read_stream(az_archive archive, uint64_t index,
                                              az_write_fn sink_fn, void *sink_ctx)
{
  if (!archive)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_entry_read_stream: archive is NULL");
  if (!sink_fn)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_entry_read_stream: sink is NULL");

  az::CSinkOutStream *stream = new az::CSinkOutStream();
  CMyComPtr<ISequentialOutStream> streamRef(stream);
  stream->Init(sink_fn, sink_ctx);

  const az_result r = ExtractSingleEntry((az::ArchiveCtx *)archive, index, stream, NULL);
  if (r != AZ_OK)
    return r;
  if (stream->Status() != AZ_OK)
    return stream->Status();
  return az::Ok();
}

// ---------------------------------------------------------------------------
// The write side of an already-open archive.
// ---------------------------------------------------------------------------

namespace az {

az_result OpenForUpdate(const UString &path, const char *passwordUtf8,
                        az_archive *outHandle, CMyComPtr<IOutArchive> &outArchive,
                        UInt32 *outItemCount, AString *outFormatName)
{
  *outHandle = NULL;
  outArchive.Release();
  if (outItemCount)
    *outItemCount = 0;
  if (outFormatName)
    outFormatName->Empty();

  CInFileStream *fileStream = new CInFileStream();
  CMyComPtr<IInStream> stream(fileStream);
  if (!fileStream->Open(us2fs(path.Ptr())))
    return Fail(AZ_ERROR_NOT_FOUND, "cannot open '%s' for update", UToStd(path).c_str());

  // The password has to be in place before Open, not after: an encrypted 7z asks
  // for it while the header is being decrypted, so a handle opened without it is
  // already unusable rather than merely unreadable.
  az_open_options openOpts;
  memset(&openOpts, 0, sizeof(openOpts));
  openOpts.struct_size = sizeof(openOpts);
  openOpts.password = passwordUtf8;

  ArchiveCtx *ctx = NULL;
  const az_result r = OpenStreamInternal(stream, path, path, &openOpts, &ctx);
  if (r != AZ_OK)
    return r;

  // One instance, two interfaces: that is how 7-Zip's handlers are built, and it
  // is the only way to rewrite a file without opening it twice.
  const HRESULT qi = ctx->Archive.QueryInterface(IID_IOutArchive, &outArchive);
  if (qi != S_OK || !outArchive)
  {
    const AString name = ctx->FormatName;
    delete ctx;
    return Fail(AZ_ERROR_UNSUPPORTED,
                "format '%s' cannot update an existing archive in place", name.Ptr());
  }

  *outHandle = (az_archive)ctx;
  if (outItemCount)
    *outItemCount = ctx->NumItems;
  if (outFormatName)
    *outFormatName = ctx->FormatName;
  return Ok();
}

} // namespace az
