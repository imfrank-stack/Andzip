// az_writer.cpp -- archive creation, and rewriting an archive that exists.
//
// Drives IOutArchive::UpdateItems with our own IArchiveUpdateCallback. 7-Zip
// does the same from CPP/7zip/UI/Common/Update.cpp, which is not vendored; the
// logic here is the subset a library needs.
//
// Updating is not a second code path: it is the same UpdateItems call, with the
// members to keep listed alongside the new ones and identified by their index in
// the source. The handlers ask the callback per item -- GetUpdateItemInfo -- and
// "not new, not changed, index k" is what makes them copy that member's stored
// bytes instead of asking for a stream and recompressing it.
//
// Codec settings reach the handler through ISetProperties using the same names
// the 7-Zip command line uses, because that is what the handlers parse:
//   "m"       method name                ("LZMA2", "Deflate", "Zstd", "PPMd", ...)
//   "x"       level 0..9
//   "d"       dictionary size            ("64m", "8m")
//   "mt"      worker threads
//   "memuse"  memory ceiling             ("1g")
//   "s"/"he"  solid / encrypt headers    (7z only)
//   "em"      zip encryption method      ("AES256", "ZipCrypto")

#include "az_internal.h"

#include <algorithm>
#include <atomic>
#include <ctime>

namespace az {

namespace {

enum ItemKind
{
  kItemFile = 0,
  kItemBuffer,
  kItemDir,
  // A member already in the archive that the rewrite should carry over untouched.
  // Carries no payload and no properties: see CUpdateCallback::GetUpdateItemInfo.
  kItemKeep
};

struct Item
{
  ItemKind Kind;
  UString Name;         // entry name, '/' separated, no trailing slash
  FString SourcePath;   // kItemFile / kItemDir
  UInt64 Size;
  bool IsDir;
  bool HasAttrib;
  UInt32 Attrib;
  bool HasPosixMode;
  UInt32 PosixMode;
  Int64 MTime;          // unix seconds, 0 = unset
  Int64 CTime;
  Int64 ATime;
  // kItemBuffer payload. CMyComPtr must hold an interface type: 7-Zip's
  // Z7_CLASS_IMP_COM_* makes AddRef/Release private on the concrete class.
  CMyComPtr<IUnknown> BufferOwner;
  const Byte *BufferData;
  // kItemKeep only: which entry of the source archive this is.
  Int32 IndexInArchive;

  Item() : Kind(kItemFile), Size(0), IsDir(false), HasAttrib(false), Attrib(0),
           HasPosixMode(false), PosixMode(0), MTime(0), CTime(0), ATime(0),
           BufferData(NULL), IndexInArchive(-1) {}
};

bool ReadFileInfo(const UString &path, UInt64 &size, bool &isDir,
                  Int64 &mtime, UInt32 &attrib)
{
  NWindows::NFile::NFind::CFileInfo fi;
  if (!fi.Find(us2fs(path.Ptr())))
    return false;

  size = fi.Size;
  isDir = fi.IsDir();
  attrib = fi.GetWinAttrib();

  FILETIME ft;
  AzFiTimeToFileTime(fi.MTime, ft);
  mtime = NWindows::NTime::FileTime_To_UnixTime64(ft);
  return true;
}

// Entry names always use '/', which is what every 7-Zip handler expects.
UString GenericPathToU(const std::filesystem::path &p)
{
  #ifdef _WIN32
  return UString(p.generic_wstring().c_str());
  #else
  return Utf8ToU(p.generic_string().c_str());
  #endif
}

// Reserving clusters up front. 7-Zip's CFileBase exposes its handle publicly on
// Windows; the POSIX side keeps _handle protected, so reaching a descriptor there
// would mean patching vendored source (vendor-sources.ps1 deletes that tree) -- and
// opening the path a second time would race the writer's own handle. So on POSIX
// this is a no-op that reports success: nothing was promised, nothing failed.
bool PreallocateOutFile(const NWindows::NFile::NIO::COutFile &file, UInt64 bytes)
{
  if (bytes == 0)
    return false;
  #ifdef _WIN32
  // FileAllocationInfo, NOT SetFileValidData: the latter needs SE_RENAME_NAME and
  // hands back clusters that still hold somebody else's deleted data.
  FILE_ALLOCATION_INFO info;
  info.AllocationSize.QuadPart = (LONGLONG)bytes;
  return ::SetFileInformationByHandle(file.GetHandle(), FileAllocationInfo,
      &info, sizeof(info)) != FALSE;
  #else
  (void)file;
  return true;
  #endif
}

// What preallocation reserves: the uncompressed total. The compressed payload can
// never exceed it (a coder that gains nothing falls back to stored), so this is a
// true upper bound -- which is the point. If the volume cannot hold even the worst
// case, we learn that before spending minutes compressing instead of at 90%.
UInt64 SourceBytesTotal(const CObjectVector<Item> &items)
{
  UInt64 total = 0;
  for (unsigned i = 0; i < items.Size(); i++)
    total += items[i].Size;
  return total;
}

// ---------------------------------------------------------------------------
// Update callback
// ---------------------------------------------------------------------------

class CUpdateCallback Z7_final
  : public IArchiveUpdateCallback
  , public ICryptoGetTextPassword2
  , public CMyUnknownImp
{
  Z7_IFACES_IMP_UNK_2(IArchiveUpdateCallback, ICryptoGetTextPassword2)
  Z7_IFACE_COM7_IMP(IProgress)

  const CObjectVector<Item> *_items;
  // Decoded once at Init: the handler wants a BSTR, and converting per call kept
  // a UTF-8 round trip alive in the hot path of a non-ASCII password.
  UString _password;
  bool _passwordDefined;
  bool _shareLocked;
  ProgressSink _progress;

public:
  az_result Status;
  AString StatusDetail;

  // Members the engine could not read. Counted separately from Status because the
  // archive itself succeeded -- this is a warning for the caller, not a failure.
  UInt32 SkippedCount;
  AString SkippedNames;   // first few only; a folder of locked files must not blow up the message

  void AddSkipped(const UString &name)
  {
    SkippedCount++;
    if (SkippedNames.Len() >= 240)
      return;
    if (SkippedCount > 1)
      SkippedNames += ", ";
    SkippedNames += UToUtf8A(name);
  }

  CUpdateCallback()
    : _items(NULL), _passwordDefined(false), _shareLocked(false), Status(AZ_OK),
      SkippedCount(0) {}

  void Init(const CObjectVector<Item> *items, const char *password,
            const az_callbacks *cb, bool shareLocked)
  {
    _items = items;
    _passwordDefined = (password && *password);
    _password = _passwordDefined ? Utf8ToU(password) : UString();
    _shareLocked = shareLocked;
    _progress.Init(cb);
    Status = AZ_OK;
    StatusDetail.Empty();
    SkippedCount = 0;
    SkippedNames.Empty();
  }
};

// Z7_IFACES_IMP_UNK_3 above already declares every interface method, so the
// definitions live out of line -- declaring them again in the class body is a
// redefinition. This is 7-Zip's own convention (see CLocalProgress).

// IProgress
STDMETHODIMP CUpdateCallback::SetTotal(UInt64 total)
{
  _progress.SetTotal(total);
  return S_OK;
}

STDMETHODIMP CUpdateCallback::SetCompleted(const UInt64 *value)
{
  if (!value)
    return S_OK;
  _progress.SetCompleted(*value);
  if (_progress.Report(AZ_OP_COMPRESS, 0, _items ? _items->Size() : 0) != AZ_OK)
  {
    Status = AZ_ERROR_CANCELLED;
    StatusDetail = "cancelled by progress callback";
    return E_ABORT;
  }
  return S_OK;
}

// IArchiveUpdateCallback -- new members carry new data and new properties; a
// kItemKeep entry answers "not new, not changed, and here is which member it is",
// which is the branch the handlers read as "copy the stored bytes verbatim".
STDMETHODIMP CUpdateCallback::GetUpdateItemInfo(UInt32 index, Int32 *newData, Int32 *newProps,
                                                UInt32 *indexInArchive)
{
  if (!_items || index >= _items->Size())
    return E_INVALIDARG;

  const Item &item = (*_items)[index];
  if (item.Kind == kItemKeep)
  {
    if (item.IndexInArchive < 0)
      return E_INVALIDARG;
    if (newData)        *newData = 0;
    if (newProps)       *newProps = 0;
    if (indexInArchive) *indexInArchive = (UInt32)item.IndexInArchive;
    return S_OK;
  }

  // newData must stay 1 for everything else: with 0 the handlers try to copy the
  // payload from indexInArchive, which is "not in the archive" for an added item.
  // Directories therefore have to answer kpidSize -- see GetProperty below.
  if (newData)        *newData = 1;
  if (newProps)       *newProps = 1;
  if (indexInArchive) *indexInArchive = (UInt32)(Int32)-1;
  return S_OK;
}

STDMETHODIMP CUpdateCallback::SetOperationResult(Int32 operationResult)
{
  if (operationResult != NArchive::NUpdate::NOperationResult::kOK)
  {
    Status = AZ_ERROR_GENERIC;
    StatusDetail = "the engine rejected an input item";
  }
  return S_OK;
}

// ICryptoGetTextPassword2 -- how the handler learns the write password
STDMETHODIMP CUpdateCallback::CryptoGetTextPassword2(Int32 *passwordIsDefined, BSTR *password)
{
  *passwordIsDefined = 0;
  *password = NULL;
  if (!_passwordDefined)
    return S_OK;
  *password = ::SysAllocString(_password.Ptr());
  if (!*password)
    return E_OUTOFMEMORY;
  *passwordIsDefined = 1;
  return S_OK;
}

STDMETHODIMP CUpdateCallback::GetProperty(UInt32 index, PROPID propID, PROPVARIANT *value) throw()
{
  NWindows::NCOM::PropVariant_Clear(value);
  if (!_items || index >= _items->Size())
    return E_INVALIDARG;

  const Item &item = (*_items)[index];

  // A kept member declared itself "properties unchanged", so the handler is
  // supposed to read them off the archive. Answering anyway would mean inventing
  // a size or a name, and a wrong one is silent corruption -- say it loudly.
  if (item.Kind == kItemKeep)
  {
    Status = AZ_ERROR_UNSUPPORTED;
    StatusDetail = "the handler asked for properties of a member it was told to keep";
    return E_FAIL;
  }

  NWindows::NCOM::CPropVariant prop;

  switch (propID)
  {
    case kpidPath:
      prop = item.Name;
      break;
    case kpidIsDir:
      prop = item.IsDir;
      break;
    case kpidSize:
      // Always VT_UI8, directories included: the tar handler rejects an empty
      // size with E_INVALIDARG once it has been told the item carries new data,
      // which silently broke "add a directory tree to a .tar".
      prop = item.IsDir ? (UInt64)0 : item.Size;
      break;
    case kpidAttrib:
      if (item.HasAttrib)
        prop = item.Attrib;
      break;
    case kpidPosixAttrib:
      if (item.HasPosixMode)
        prop = item.PosixMode;
      break;
    case kpidMTime:
      if (item.MTime != 0)
      {
        FILETIME ft;
        if (UnixToFileTimeProp(item.MTime, ft))
          prop = ft;
      }
      break;
    case kpidCTime:
      if (item.CTime != 0)
      {
        FILETIME ft;
        if (UnixToFileTimeProp(item.CTime, ft))
          prop = ft;
      }
      break;
    case kpidATime:
      if (item.ATime != 0)
      {
        FILETIME ft;
        if (UnixToFileTimeProp(item.ATime, ft))
          prop = ft;
      }
      break;
    default:
      break;
  }

  prop.Detach(value);
  return S_OK;
}

STDMETHODIMP CUpdateCallback::GetStream(UInt32 index, ISequentialInStream **inStream) throw()
{
  *inStream = NULL;
  if (!_items || index >= _items->Size())
    return E_INVALIDARG;

  const Item &item = (*_items)[index];

  // Same contract as GetProperty: a kept member is copied out of the archive by
  // the handler, never handed over as a stream. Both GetStream call sites in
  // ZipHandlerOut.cpp sit inside `if (ui.NewData)`, so reaching here means a
  // handler disagrees with that reading -- fail the write instead of shipping a
  // member whose payload came from somewhere else.
  if (item.Kind == kItemKeep)
  {
    Status = AZ_ERROR_UNSUPPORTED;
    StatusDetail = "the handler asked for a stream for a member it was told to keep";
    return E_FAIL;
  }

  _progress.SetEntry(item.Name);

  if (item.IsDir)
    return S_OK;   // directories carry no payload

  if (item.Kind == kItemBuffer)
  {
    CBufInStream *mem = new CBufInStream();
    CMyComPtr<IInStream> wrap(mem);
    mem->Init(item.BufferData, (size_t)item.Size, item.BufferOwner);
    *inStream = wrap.Detach();
    return S_OK;
  }

  CInFileStream *fileStream = new CInFileStream();
  CMyComPtr<IInStream> wrap(fileStream);
  // The plain Open asks for FILE_SHARE_READ only, so it fails on anything another
  // process holds open for writing. Sharing the write side is what "pack locked
  // files" means; a member that still cannot be opened is skipped below exactly as
  // before. Note the trade-off: a file that is still being written can be captured
  // mid-update, so the member is that instant of the file, not a consistent copy.
  const bool opened = _shareLocked
      ? fileStream->OpenShared(item.SourcePath, true)
      : fileStream->Open(item.SourcePath);
  if (!opened)
  {
    // Not fatal, and it must not be reported as if it were: S_FALSE makes the
    // handler drop this one member and the archive is still written. Raising
    // AZ_ERROR_IO here used to report the whole create as failed even though the
    // .7z on disk was fine -- so the caller now gets the names as a warning.
    // (Windows gives no way around a file another process holds without sharing:
    // FILE_FLAG_BACKUP_SEMANTICS still returns ERROR_SHARING_VIOLATION, measured.)
    AddSkipped(item.Name);
    return S_FALSE;
  }

  *inStream = wrap.Detach();
  return S_OK;
}

// ---------------------------------------------------------------------------
// Handler properties
// ---------------------------------------------------------------------------

struct PropEntry
{
  UString Name;
  PROPVARIANT Value;

  // CObjectVector::AddNew() does `new T`, which only default-initialises: without
  // this constructor the POD PROPVARIANT stays garbage, and PropVariantClear()
  // dispatches on vt -- it would free a wild pointer.
  PropEntry() { ::PropVariantInit(&Value); }
};

UInt64 ParseVolumeSize(const char *spec)
{
  // "100M", "700m", "4G", "1024" (bytes); also accepts k/m/g/t and a 'b' suffix.
  if (!spec || !*spec)
    return 0;

  const char *p = spec;
  UInt64 value = 0;
  bool anyDigit = false;
  while (*p >= '0' && *p <= '9')
  {
    value = value * 10 + (UInt64)(*p - '0');
    anyDigit = true;
    p++;
  }
  if (!anyDigit)
    return 0;

  while (*p == ' ')
    p++;

  UInt64 mult = 1;
  switch (*p)
  {
    case 0:                 break;
    case 'b': case 'B':     break;
    case 'k': case 'K':     mult = 1024ull; break;
    case 'm': case 'M':     mult = 1024ull * 1024; break;
    case 'g': case 'G':     mult = 1024ull * 1024 * 1024; break;
    case 't': case 'T':     mult = 1024ull * 1024 * 1024 * 1024; break;
    default:                return 0;
  }
  return value * mult;
}

void AddStringProp(CObjectVector<PropEntry> &props, const char *name, const UString &value)
{
  PropEntry &e = props.AddNew();
  e.Name = name;
  ::PropVariantInit(&e.Value);
  e.Value.vt = VT_BSTR;
  e.Value.bstrVal = ::SysAllocString(value.Ptr());
}

void AddUInt32Prop(CObjectVector<PropEntry> &props, const char *name, UInt32 value)
{
  PropEntry &e = props.AddNew();
  e.Name = name;
  ::PropVariantInit(&e.Value);
  e.Value.vt = VT_UI4;
  e.Value.ulVal = value;
}

void AddBoolProp(CObjectVector<PropEntry> &props, const char *name, bool value)
{
  PropEntry &e = props.AddNew();
  e.Name = name;
  ::PropVariantInit(&e.Value);
  e.Value.vt = VT_BOOL;
  e.Value.boolVal = value ? VARIANT_TRUE : VARIANT_FALSE;
}

void FreeProps(CObjectVector<PropEntry> &props)
{
  FOR_VECTOR(i, props)
    NWindows::NCOM::PropVariant_Clear(&props[i].Value);
}

HRESULT ApplyProperties(ISetProperties *setProps, CObjectVector<PropEntry> &props)
{
  if (!setProps || props.IsEmpty())
    return S_OK;

  CRecordVector<const wchar_t *> names;
  CRecordVector<PROPVARIANT> values;
  names.ClearAndReserve(props.Size());
  values.ClearAndReserve(props.Size());
  FOR_VECTOR(i, props)
  {
    names.Add(props[i].Name.Ptr());
    values.Add(props[i].Value);
  }

  return setProps->SetProperties(&names[0], &values[0], (UInt32)props.Size());
}

} // namespace

// ---------------------------------------------------------------------------
// Writer handle
// ---------------------------------------------------------------------------

struct WriterCtx
{
  CMyComPtr<IOutArchive> OutArchive;
  CMyComPtr<ISetProperties> SetProps;
  // Smart pointers are held through interface types; the raw pointers give
  // access to the concrete classes' own members.
  CMyComPtr<IArchiveUpdateCallback> CallbackRef;
  CUpdateCallback *Callback;
  CObjectVector<Item> Items;

  int FormatIndex;
  UString FormatName;
  UString DestPath;

  az_create_options Opts;   // POD copy; OwnedStrings backs every const char*
  // deque, not vector: push_back must not invalidate the c_str() pointers
  // already stored in Opts.
  std::deque<std::string> OwnedStrings;

  UInt64 VolumeSize;
  bool ToMemory;
  bool Finished;
  bool Failed;

  // Our own copy of the caller's callback set, with Opts.callbacks repointed at
  // it. Compression runs in az_writer_finish, long after the az_create_options
  // passed to az_writer_open has gone out of scope in the caller.
  az_callbacks Callbacks;
  bool HasCallbacks;

  CMyComPtr<IOutStream> MemOutRef;
  az::CMemOutStream *MemOut;
  CMyComPtr<IOutStream> FileOutRef;
  COutFileStream *FileOut;

  // Update-in-place. SourceArchive is a handle this writer opened and owns: the
  // handler holding the parsed database *is* the writer, and it keeps reading
  // member bytes off that stream while UpdateItems runs. SourceItemCount bounds
  // az_writer_keep_member -- ZipHandlerOut indexes m_Items[indexInArc] with no
  // bounds check of its own, so an out-of-range index here is an out-of-bounds
  // read inside the handler, not a graceful failure.
  az_archive SourceArchive;
  UInt32 SourceItemCount;
  bool IsUpdate;
  UString TempPath;   // where finish writes before swapping it over DestPath

  WriterCtx() : Callback(NULL), FormatIndex(-1), VolumeSize(0), ToMemory(false),
                Finished(false), Failed(false), HasCallbacks(false),
                MemOut(NULL), FileOut(NULL), SourceArchive(NULL), SourceItemCount(0),
                IsUpdate(false)
  {
    memset(&Opts, 0, sizeof(Opts));
    memset(&Callbacks, 0, sizeof(Callbacks));
    Callbacks.struct_size = sizeof(az_callbacks);
  }

  const char *Own(const char *s)
  {
    if (!s)
      return NULL;
    OwnedStrings.push_back(s);
    return OwnedStrings.back().c_str();
  }

  ~WriterCtx()
  {
    // An update that never reached its swap leaves two things behind that nobody
    // else can clean up: a read handle still locking the original, and a
    // half-written temporary beside it.
    if (SourceArchive)
      az_archive_close(SourceArchive);
    if (!TempPath.IsEmpty())
    {
      std::error_code ignored;
      std::filesystem::remove(az::UToPath(TempPath), ignored);
    }
  }
};

namespace {

// Turns az_create_options into the handler's property list. `problems` collects
// settings this particular format cannot honour, so the caller can report them
// without failing the operation.
void BuildProperties(const WriterCtx *ctx, CObjectVector<PropEntry> &props, AString &problems)
{
  const az_create_options &o = ctx->Opts;

  UString fmtLower = ctx->FormatName;
  fmtLower.MakeLower_Ascii();
  const bool is7z  = (fmtLower == L"7z");
  const bool isZip = (fmtLower == L"zip");

  if (o.level >= 0)
    AddUInt32Prop(props, "x", (UInt32)o.level);

  UString methodLower;
  if (o.method && *o.method)
  {
    AddStringProp(props, "m", Utf8ToU(o.method));
    methodLower = Utf8ToU(o.method);
    methodLower.MakeLower_Ascii();
  }

  if (o.dict_size_mib)
  {
    UString v;
    v.Add_UInt32(o.dict_size_mib);
    v += L'm';
    AddStringProp(props, "d", v);
  }

  if (o.word_size)
  {
    UString v;
    v.Add_UInt32(o.word_size);
    if (methodLower.Find(L"ppmd") >= 0)
      AddStringProp(props, "o", v);
    else if (methodLower.Find(L"lzma") >= 0)
      AddStringProp(props, "lc", v);
    else if (methodLower.Find(L"bzip2") >= 0)
      AddStringProp(props, "pass", v);
    else
      problems += "word_size applies only to PPMd, LZMA and BZip2; ignored. ";
  }

  const UInt32 threads = o.num_threads ? o.num_threads : NumThreads();
  if (threads)
    AddUInt32Prop(props, "mt", threads);

  const UInt64 memLimit = o.memory_limit_mib ? o.memory_limit_mib : MemoryLimitMiB();
  if (memLimit)
  {
    UString v;
    v.Add_UInt64(memLimit);
    v += L'm';
    AddStringProp(props, "memuse", v);
  }

  if (o.encryption != AZ_ENC_NONE)
  {
    if (!o.password || !*o.password)
      problems += "encryption requested but no password given; ignored. ";
    else if (isZip)
      AddStringProp(props, "em",
                    Utf8ToU((o.encryption == AZ_ENC_ZIPCRYPTO) ? "ZipCrypto" : "AES256"));
    else if (is7z)
    {
      // 7z has exactly one scheme (AES-256 with a SHA-256 KDF).
      if (o.encryption == AZ_ENC_ZIPCRYPTO)
        problems += "7z does not support ZipCrypto; using AES-256. ";
      if (o.encrypt_headers)
        AddBoolProp(props, "he", true);
    }
    else
    {
      problems += "format '";
      problems += UToUtf8A(ctx->FormatName);
      problems += "' cannot store a password; ignored. ";
    }
  }
  else if (o.encrypt_headers)
    problems += "encrypt_headers needs a password and the 7z format; ignored. ";

  if (is7z)
    AddBoolProp(props, "s", o.solid != 0);
  else if (o.solid && !is7z)
    problems += "solid mode applies to 7z only; ignored. ";

  if (isZip)
  {
    // ZIP entry-name encoding. The default is CP_OEMCP: non-ASCII names get
    // converted to the local code page with no UTF-8 flag set, so WinZip,
    // SharpZipLib and the BCL all read Chinese names as mojibake. cp=65001 makes
    // 7-Zip write UTF-8 names and set the language encoding flag (using "cu=on"
    // on its own had no effect in this version).
    AddBoolProp(props, "cu", true);
    AddUInt32Prop(props, "cp", 65001);   // CP_UTF8
  }

  if (o.comment && *o.comment)
  {
    // The zip and 7z out-handlers have no "c" in their property tables at all:
    // SetProperties reports E_INVALIDARG for an unknown name and the whole write
    // then fails. Archive comments cannot be set through this engine, so report
    // "ignored" instead of passing a property that would break the operation.
    problems += "archive comments are not supported by the engine; ignored. ";
  }

  // Passthrough for anything the structured options do not cover, e.g.
  // "mf=bt4\nfb=273" or "tc=off".
  if (o.extra_properties && *o.extra_properties)
  {
    const AString extra(o.extra_properties);
    unsigned pos = 0;
    for (;;)
    {
      const int eol = extra.Find('\n', pos);
      AString line = (eol < 0) ? extra.Mid(pos, extra.Len() - pos)
                               : extra.Mid(pos, (unsigned)eol - pos);
      line.Trim();
      const int eq = line.Find('=');
      if (!line.IsEmpty() && eq > 0)
      {
        const AString propName = line.Left((unsigned)eq);
        AddStringProp(props, propName.Ptr(), Utf8ToU(line.Ptr(eq + 1)));
      }
      if (eol < 0)
        break;
      pos = (unsigned)eol + 1;
      if (pos >= (unsigned)extra.Len())
        break;
    }
  }
}

az_result AddTreeRecursive(WriterCtx *ctx, const UString &rootU, const UString &baseName)
{
  std::error_code ec;
  const std::filesystem::path root = UToPath(rootU);

  std::vector<std::filesystem::path> stack;
  stack.push_back(root);

  while (!stack.empty())
  {
    const std::filesystem::path dir = stack.back();
    stack.pop_back();

    std::vector<std::filesystem::path> entries;
    {
      std::error_code iterEc;
      std::filesystem::directory_iterator it(dir, iterEc);
      if (iterEc)
        return Fail(AZ_ERROR_IO, "cannot list directory: %s", iterEc.message().c_str());
      for (; it != std::filesystem::directory_iterator(); it.increment(iterEc))
      {
        if (iterEc)
          break;
        entries.push_back(it->path());
      }
      if (iterEc)
        return Fail(AZ_ERROR_IO, "cannot list directory: %s", iterEc.message().c_str());
    }
    // Stable order so repeated runs produce comparable archives.
    std::sort(entries.begin(), entries.end());

    for (size_t i = 0; i < entries.size(); i++)
    {
      const std::filesystem::path &p = entries[i];

      std::error_code relEc;
      UString rel = GenericPathToU(std::filesystem::relative(p, root, relEc));
      if (relEc || rel.IsEmpty())
        rel = GenericPathToU(p.filename());

      const UString fullPath = PathToU(p);

      UInt64 size = 0;
      bool isDir = false;
      Int64 mtime = 0;
      UInt32 attrib = 0;
      const bool statOk = ReadFileInfo(fullPath, size, isDir, mtime, attrib);

      Item &item = ctx->Items.AddNew();
      item.Name = baseName + L"/" + rel;
      item.SourcePath = us2fs(fullPath.Ptr());
      item.Kind = isDir ? kItemDir : kItemFile;
      item.IsDir = isDir;
      item.Size = isDir ? 0 : size;
      item.MTime = mtime;
      item.Attrib = attrib;
      item.HasAttrib = (attrib != 0);

      if (!statOk)
        item.HasAttrib = false;

      if (isDir)
        stack.push_back(p);
    }
  }
  return AZ_OK;
}

} // namespace
} // namespace az

// ---------------------------------------------------------------------------
// C ABI -- writer
// ---------------------------------------------------------------------------

namespace {

// The real entry point behind az_writer_open / az_writer_open_memory /
// az_writer_open_existing. `updatePath` is NULL unless this writer rewrites an
// archive that is already on disk.
az_result WriterOpenInternal(const char *path_utf8, const az_create_options *opts,
                             bool toMemory, az_writer *out, const char *updatePath)
{
  *out = NULL;

  az_create_options resolved;
  if (opts)
  {
    if (opts->struct_size != sizeof(az_create_options))
      return az::Fail(AZ_ERROR_INVALID_ARG,
                      "az_create_options.struct_size is %u, expected %u "
                      "(ABI mismatch -- rebuild against the current andzip.h)",
                      (unsigned)opts->struct_size, (unsigned)sizeof(az_create_options));
    resolved = *opts;
  }
  else
  {
    memset(&resolved, 0, sizeof(resolved));
    resolved.format = "7z";
    resolved.level = -1;
    resolved.encryption = AZ_ENC_NONE;
    resolved.solid = 1;
    resolved.multi_thread = 1;
  }

  std::unique_ptr<az::WriterCtx> ctx(new az::WriterCtx());
  ctx->ToMemory = toMemory;

  // Which handler, and where the bytes go. Creating an archive asks the engine for
  // a handler by name; updating one opens the file on disk and takes that same
  // COM instance, because for these formats it answers both sides.
  CMyComPtr<IOutArchive> outArchive;
  AString formatNameA;

  if (updatePath)
  {
    az_archive handle = NULL;
    UInt32 itemCount = 0;
    AString fmt;
    const az_result opened = az::OpenForUpdate(az::Utf8ToU(updatePath), resolved.password,
                                               &handle, outArchive, &itemCount, &fmt);
    if (opened != AZ_OK)
      return opened;

    ctx->IsUpdate = true;
    ctx->SourceArchive = handle;
    ctx->SourceItemCount = itemCount;
    ctx->DestPath = az::Utf8ToU(updatePath);
    ctx->FormatIndex = az::FindFormatIndex(fmt.Ptr());
    formatNameA = fmt;
    ctx->FormatName = az::Utf8ToU(fmt.Ptr());
  }
  else
  {
    const char *formatName = (resolved.format && *resolved.format) ? resolved.format : "7z";

    uint32_t fmtIndex = 0;
    if (az_format_find(formatName, &fmtIndex) != AZ_OK)
      return az::Fail(AZ_ERROR_UNSUPPORTED, "unknown archive format '%s'", formatName);

    const CObjectVector<az::Format> &formats = az::Formats();
    if (!formats[fmtIndex].CanWrite)
      return az::Fail(AZ_ERROR_UNSUPPORTED, "format '%s' cannot be written", formatName);

    ctx->FormatIndex = (int)fmtIndex;
    ctx->FormatName = formats[fmtIndex].Name;
    ctx->DestPath = toMemory ? UString() : az::Utf8ToU(path_utf8);
    formatNameA = az::UToUtf8A(ctx->FormatName);

    const HRESULT created = az::CreateOutArchive((int)fmtIndex, outArchive);
    if (created != S_OK)
      return az::FailHr(created, AZ_ERROR_GENERIC, "creating the archive handler");
  }

  ctx->OutArchive = outArchive;

  // Copy every string the caller passed, so the options struct may die.
  resolved.format          = ctx->Own(formatNameA.Ptr());
  resolved.method          = ctx->Own(resolved.method);
  resolved.password        = ctx->Own(resolved.password);
  resolved.volume_size     = ctx->Own(resolved.volume_size);
  resolved.comment         = ctx->Own(resolved.comment);
  resolved.extra_properties= ctx->Own(resolved.extra_properties);

  if (resolved.callbacks && resolved.callbacks->struct_size != sizeof(az_callbacks))
    return az::Fail(AZ_ERROR_INVALID_ARG,
                    "az_callbacks.struct_size is %u, expected %u (ABI mismatch)",
                    (unsigned)resolved.callbacks->struct_size,
                    (unsigned)sizeof(az_callbacks));

  // Own the callback set too, and repoint the stored options at our copy.
  // Compression happens in az_writer_finish, by which time the caller's
  // az_create_options has certainly gone away.
  ctx->HasCallbacks = (resolved.callbacks != NULL);
  if (ctx->HasCallbacks)
    ctx->Callbacks = *resolved.callbacks;
  resolved.callbacks = ctx->HasCallbacks ? &ctx->Callbacks : NULL;

  ctx->Opts = resolved;

  {
    CMyComPtr<ISetProperties> setProps;
    outArchive.QueryInterface(IID_ISetProperties, &setProps);
    ctx->SetProps = setProps;
  }

  ctx->VolumeSize = az::ParseVolumeSize(resolved.volume_size);
  if (ctx->VolumeSize != 0)
    return az::Fail(AZ_ERROR_UNSUPPORTED,
                    "split-volume output is not supported yet (volume_size='%s'); "
                    "reading split archives is supported", resolved.volume_size);
  ctx->VolumeSize = 0;

  az::CUpdateCallback *cb = new az::CUpdateCallback();
  ctx->CallbackRef = cb;
  ctx->Callback = cb;
  cb->Init(&ctx->Items, resolved.password, resolved.callbacks, resolved.share_locked != 0);

  *out = (az_writer)ctx.release();
  return az::Ok();
}

} // namespace

AZ_API az_result AZ_CALL az_writer_open(const char *path_utf8,
                                        const az_create_options *opts,
                                        az_writer *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_open: out is NULL");
  if (!path_utf8 || !*path_utf8)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_open: path is empty");
  return WriterOpenInternal(path_utf8, opts, false, out, NULL);
}

AZ_API az_result AZ_CALL az_writer_open_memory(const az_create_options *opts, az_writer *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_open_memory: out is NULL");
  return WriterOpenInternal(NULL, opts, true, out, NULL);
}

AZ_API az_result AZ_CALL az_writer_open_existing(const char *path_utf8,
                                                 const az_create_options *opts,
                                                 az_writer *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_open_existing: out is NULL");
  if (!path_utf8 || !*path_utf8)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_open_existing: path is empty");
  return WriterOpenInternal(NULL, opts, false, out, path_utf8);
}

AZ_API az_result AZ_CALL az_writer_keep_member(az_writer writer, uint32_t index_in_archive)
{
  if (!writer)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_keep_member: writer is NULL");

  az::WriterCtx *ctx = (az::WriterCtx *)writer;
  if (ctx->Finished || ctx->Failed)
    return az::Fail(AZ_ERROR_ALREADY_DONE, "writer is already finished");
  if (!ctx->IsUpdate)
    return az::Fail(AZ_ERROR_INVALID_ARG,
                    "az_writer_keep_member: this writer is creating a new archive, "
                    "there is nothing already in it to keep");

  // The handlers index their own item table on this number without checking it,
  // so the bound has to be enforced here or an out-of-range index becomes a
  // wild read inside the handler.
  if (index_in_archive >= ctx->SourceItemCount)
    return az::Fail(AZ_ERROR_INVALID_ARG, "member %u is out of range (%u members)",
                    index_in_archive, ctx->SourceItemCount);

  az::Item &item = ctx->Items.AddNew();
  item.Kind = az::kItemKeep;
  item.IndexInArchive = (Int32)index_in_archive;
  item.IsDir = false;
  item.Size = 0;
  return az::Ok();
}

AZ_API az_result AZ_CALL az_writer_add_file(az_writer writer, const char *source_path_utf8,
                                            const char *entry_name_utf8,
                                            const az_item_meta *meta, int32_t recurse)
{
  if (!writer)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_file: writer is NULL");
  if (meta && meta->struct_size != sizeof(az_item_meta))
    return az::Fail(AZ_ERROR_INVALID_ARG,
                    "az_item_meta.struct_size is %u, expected %u",
                    (unsigned)meta->struct_size, (unsigned)sizeof(az_item_meta));
  if (!source_path_utf8 || !*source_path_utf8)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_file: source path is empty");

  az::WriterCtx *ctx = (az::WriterCtx *)writer;
  if (ctx->Finished || ctx->Failed)
    return az::Fail(AZ_ERROR_ALREADY_DONE, "writer is already finished");

  const UString src = az::Utf8ToU(source_path_utf8);

  UInt64 size = 0;
  bool isDir = false;
  Int64 mtime = 0;
  UInt32 attrib = 0;
  if (!az::ReadFileInfo(src, size, isDir, mtime, attrib))
    return az::Fail(AZ_ERROR_NOT_FOUND, "cannot stat '%s'", source_path_utf8);

  UString baseName;
  if (entry_name_utf8 && *entry_name_utf8)
    baseName = az::Utf8ToU(entry_name_utf8);
  else
  {
    const std::filesystem::path p = az::UToPath(src);
    baseName = az::GenericPathToU(p.filename());
    if (baseName.IsEmpty() || baseName == L"." || baseName == L"..")
      baseName = az::GenericPathToU(p);
  }
  baseName.Replace(WCHAR_PATH_SEPARATOR, L'/');
  while (!baseName.IsEmpty() && baseName[baseName.Len() - 1] == L'/')
    baseName.DeleteBack();
  if (baseName.IsEmpty())
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_file: cannot derive an entry name");

  if (meta && meta->is_dir >= 0)
    isDir = (meta->is_dir != 0);

  auto applyMeta = [&](az::Item &item) {
    if (meta)
    {
      if (meta->mtime_unix)  item.MTime = meta->mtime_unix;
      if (meta->ctime_unix)  item.CTime = meta->ctime_unix;
      if (meta->atime_unix)  item.ATime = meta->atime_unix;
      if (meta->attributes)  { item.Attrib = meta->attributes;    item.HasAttrib = true; }
      if (meta->posix_mode)  { item.PosixMode = meta->posix_mode; item.HasPosixMode = true; }
    }
    if (item.MTime == 0)
      item.MTime = mtime;
    if (!item.HasAttrib && attrib != 0)
    {
      item.Attrib = attrib;
      item.HasAttrib = true;
    }
  };

  az::Item &item = ctx->Items.AddNew();
  item.Name = baseName;
  item.SourcePath = us2fs(src.Ptr());
  item.Size = isDir ? 0 : size;
  item.IsDir = isDir;
  item.Kind = isDir ? az::kItemDir : az::kItemFile;
  applyMeta(item);

  if (isDir && recurse)
    return az::AddTreeRecursive(ctx, src, baseName);

  return az::Ok();
}

AZ_API az_result AZ_CALL az_writer_add_buffer(az_writer writer, const void *data, size_t size,
                                              const char *entry_name_utf8,
                                              const az_item_meta *meta)
{
  if (!writer)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_buffer: writer is NULL");
  if (!entry_name_utf8 || !*entry_name_utf8)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_buffer: entry name is required");
  if (!data && size != 0)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_buffer: data is NULL");

  az::WriterCtx *ctx = (az::WriterCtx *)writer;
  if (ctx->Finished || ctx->Failed)
    return az::Fail(AZ_ERROR_ALREADY_DONE, "writer is already finished");

  UString name = az::Utf8ToU(entry_name_utf8);
  name.Replace(WCHAR_PATH_SEPARATOR, L'/');
  while (!name.IsEmpty() && name[name.Len() - 1] == L'/')
    name.DeleteBack();
  if (name.IsEmpty())
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_buffer: entry name is empty");

  CReferenceBuf *bufRef = new CReferenceBuf();
  CMyComPtr<IUnknown> bufOwner(bufRef);
  const Byte *payload = NULL;
  if (size)
  {
    try
    {
      bufRef->Buf.CopyFrom((const Byte *)data, size);
    }
    catch (const std::bad_alloc &)
    {
      return az::Fail(AZ_ERROR_OUT_OF_MEMORY, "cannot buffer %llu bytes", (unsigned long long)size);
    }
    payload = bufRef->Buf;
  }

  az::Item &item = ctx->Items.AddNew();
  item.Kind = az::kItemBuffer;
  item.Name = name;
  item.Size = size;
  item.IsDir = false;
  item.BufferOwner = bufOwner;
  item.BufferData = payload;

  if (meta)
  {
    if (meta->mtime_unix) item.MTime = meta->mtime_unix;
    if (meta->ctime_unix) item.CTime = meta->ctime_unix;
    if (meta->atime_unix) item.ATime = meta->atime_unix;
    if (meta->attributes) { item.Attrib = meta->attributes;    item.HasAttrib = true; }
    if (meta->posix_mode) { item.PosixMode = meta->posix_mode; item.HasPosixMode = true; }
  }
  if (item.MTime == 0)
    item.MTime = (Int64)time(NULL);

  return az::Ok();
}

AZ_API az_result AZ_CALL az_writer_add_directory(az_writer writer, const char *entry_name_utf8,
                                                 const az_item_meta *meta)
{
  if (!writer)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_directory: writer is NULL");
  if (!entry_name_utf8 || !*entry_name_utf8)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_directory: entry name is required");

  az::WriterCtx *ctx = (az::WriterCtx *)writer;
  if (ctx->Finished || ctx->Failed)
    return az::Fail(AZ_ERROR_ALREADY_DONE, "writer is already finished");

  UString name = az::Utf8ToU(entry_name_utf8);
  name.Replace(WCHAR_PATH_SEPARATOR, L'/');
  while (!name.IsEmpty() && name[name.Len() - 1] == L'/')
    name.DeleteBack();
  if (name.IsEmpty())
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_add_directory: entry name is empty");

  az::Item &item = ctx->Items.AddNew();
  item.Kind = az::kItemDir;
  item.Name = name;
  item.IsDir = true;
  item.Size = 0;

  if (meta)
  {
    if (meta->mtime_unix) item.MTime = meta->mtime_unix;
    if (meta->attributes) { item.Attrib = meta->attributes;    item.HasAttrib = true; }
    if (meta->posix_mode) { item.PosixMode = meta->posix_mode; item.HasPosixMode = true; }
  }
  if (item.MTime == 0)
    item.MTime = (Int64)time(NULL);

  return az::Ok();
}

AZ_API az_result AZ_CALL az_writer_finish(az_writer writer, void **out_data, size_t *out_size)
{
  if (out_data)
    *out_data = NULL;
  if (out_size)
    *out_size = 0;
  if (!writer)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_writer_finish: writer is NULL");

  az::WriterCtx *ctx = (az::WriterCtx *)writer;
  if (ctx->Finished)
    return az::Fail(AZ_ERROR_ALREADY_DONE, "writer is already finished");
  ctx->Finished = true;

  // Properties are resolved here rather than at open time so that "this format
  // cannot do that" is decided against the real handler.
  CObjectVector<az::PropEntry> props;
  AString problems;
  az::BuildProperties(ctx, props, problems);

  const HRESULT propRes = az::ApplyProperties(ctx->SetProps, props);
  az::FreeProps(props);

  if (propRes != S_OK)
  {
    ctx->Failed = true;
    return az::FailHr(propRes, AZ_ERROR_INVALID_ARG, "applying compression settings");
  }

  ISequentialOutStream *outStream = NULL;

  if (ctx->ToMemory)
  {
    az::CMemOutStream *mem = new az::CMemOutStream();
    ctx->MemOutRef = mem;
    ctx->MemOut = mem;
    outStream = mem;
  }
  else
  {
    const az_result mk = az::MakeDirTree(ctx->DestPath);
    if (mk != AZ_OK)
    {
      ctx->Failed = true;
      return mk;
    }

    // An update must not write into the file it is reading -- the source's stored
    // bytes are still being copied out of it. Write beside it and swap afterwards.
    UString writeTarget = ctx->DestPath;
    if (ctx->IsUpdate)
    {
      // Beside the original, so the swap at the end is a rename within one volume
      // rather than a copy across two. The process id separates concurrent writers;
      // the counter separates two updates of the same file in a row.
      #ifdef _WIN32
      const unsigned owner = (unsigned)::GetCurrentProcessId();
      #else
      const unsigned owner = (unsigned)::getpid();
      #endif
      static std::atomic<unsigned> seq(0);

      std::error_code ignored;
      for (unsigned attempt = 0;; attempt++)
      {
        ctx->TempPath = ctx->DestPath;
        ctx->TempPath += L".aztmp";
        ctx->TempPath.Add_UInt32(owner);
        ctx->TempPath += L'-';
        ctx->TempPath.Add_UInt32(seq.fetch_add(1));
        if (attempt >= 64 ||
            !std::filesystem::exists(az::UToPath(ctx->TempPath), ignored))
          break;
      }
      writeTarget = ctx->TempPath;
    }

    COutFileStream *fileStream = new COutFileStream();
    ctx->FileOutRef = fileStream;
    ctx->FileOut = fileStream;
    if (!fileStream->Create_ALWAYS(us2fs(writeTarget.Ptr())))
    {
      ctx->Failed = true;
      return az::Fail(AZ_ERROR_IO, "cannot create '%s'", az::UToStd(writeTarget).c_str());
    }
    if (ctx->Opts.preallocate)
    {
      // Best effort. Not being able to reserve is no reason to abandon a write that
      // may still succeed -- the disk could free up, or the worst-case estimate may
      // simply exceed the volume while the real archive fits inside it.
      if (!az::PreallocateOutFile(fileStream->File, az::SourceBytesTotal(ctx->Items)))
        problems += "could not preallocate the output file; continuing without it. ";
    }
    outStream = fileStream;
  }

  const UInt32 numItems = (UInt32)ctx->Items.Size();
  const HRESULT res = ctx->OutArchive->UpdateItems(outStream, numItems, ctx->Callback);

  if (ctx->Callback->Status != AZ_OK)
  {
    ctx->Failed = true;
    return az::Fail(ctx->Callback->Status, "%s", ctx->Callback->StatusDetail.Ptr());
  }
  if (res != S_OK)
  {
    ctx->Failed = true;
    return az::FailHr(res, AZ_ERROR_GENERIC, "writing the archive");
  }

  if (ctx->Callback->SkippedCount)
  {
    AString note;
    note += "skipped ";
    note.Add_UInt32(ctx->Callback->SkippedCount);
    note += " unreadable member(s), the archive itself is fine: ";
    note += ctx->Callback->SkippedNames;
    note += ". A file another program holds without sharing cannot be read by anyone "
            "(ERROR_SHARING_VIOLATION, backup semantics included) -- that is Windows.";
    problems += note.Ptr();
  }
  if (ctx->FileOut)
  {
    const HRESULT closeRes = ctx->FileOut->Close();
    if (closeRes != S_OK)
    {
      ctx->Failed = true;
      return az::FailHr(closeRes, AZ_ERROR_IO, "closing the archive file");
    }
  }

  if (ctx->IsUpdate)
  {
    // The handler still has the original open, and Windows refuses to rename over
    // a file that is open without FILE_SHARE_DELETE -- 7-Zip's input stream is
    // exactly that. Let it go first; the copy is finished by now, so nothing needs
    // it. Until this swap lands, the archive on disk is still the old one, which is
    // why every failure below says so instead of leaving "did it work?" open.
    if (ctx->SourceArchive)
    {
      az_archive_close(ctx->SourceArchive);
      ctx->SourceArchive = NULL;
    }

    const std::filesystem::path temp = az::UToPath(ctx->TempPath);
    const std::filesystem::path target = az::UToPath(ctx->DestPath);
    std::error_code ec;
    std::filesystem::rename(temp, target, ec);
    if (ec)
    {
      std::error_code ignored;
      std::filesystem::remove(temp, ignored);
      ctx->TempPath.Empty();
      ctx->Failed = true;
      return az::Fail(AZ_ERROR_IO,
                      "the rebuilt archive could not replace '%s' (%s) -- the original "
                      "is untouched", az::UToStd(ctx->DestPath).c_str(), ec.message().c_str());
    }
    ctx->TempPath.Empty();
  }

  if (ctx->ToMemory && out_data)
  {
    const size_t n = (size_t)ctx->MemOut->Size();
    void *block = BigAlloc(n ? n : 1);
    if (!block)
    {
      ctx->Failed = true;
      return az::Fail(AZ_ERROR_OUT_OF_MEMORY, "cannot allocate %llu bytes for the archive",
                      (unsigned long long)n);
    }
    if (n)
      memcpy(block, ctx->MemOut->Data(), n);
    *out_data = block;
    if (out_size)
      *out_size = n;
  }

  // Success, but tell the caller which settings the format could not honour.
  if (!problems.IsEmpty())
    return az::Warn("%s", problems.Ptr());
  return az::Ok();
}

AZ_API void AZ_CALL az_writer_free(az_writer writer)
{
  if (!writer)
    return;
  delete (az::WriterCtx *)writer;
}

AZ_API void AZ_CALL az_free(void *ptr)
{
  BigFree(ptr);
}
