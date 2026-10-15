// az_internal.h -- shared declarations for the src/api translation units.
//
// Everything in here is private to libandzip_core; the public surface is only
// include/andzip/andzip.h.

#pragma once

#include "az_pch.h"

// ---------------------------------------------------------------------------
// Engine entry points. These are ordinary (non-exported) functions provided by
// the 7-Zip bundle sources we compile in: ArchiveExports.cpp, DllExports2.cpp
// and Compress/CodecExports.cpp. STDAPI already carries extern "C" plus the
// platform calling convention, so declarations here must match exactly.
// ---------------------------------------------------------------------------

STDAPI CreateObject(const GUID *clsid, const GUID *iid, void **outObject);
STDAPI CreateArchiver(const GUID *clsid, const GUID *iid, void **outObject);
STDAPI GetNumberOfFormats(UINT32 *numFormats);
STDAPI GetHandlerProperty2(UInt32 formatIndex, PROPID propID, PROPVARIANT *value);
STDAPI GetIsArc(UInt32 formatIndex, Func_IsArc *isArc);
STDAPI GetNumberOfMethods(UInt32 *numCodecs);
STDAPI GetMethodProperty(UInt32 codecIndex, PROPID propID, PROPVARIANT *value);
STDAPI CreateDecoder(UInt32 index, const GUID *iid, void **outObject);
STDAPI CreateEncoder(UInt32 index, const GUID *iid, void **outObject);
STDAPI GetHashers(IHashers **hashers);
STDAPI SetCaseSensitive(Int32 caseSensitive);

namespace az {

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

void SetError(az_result code, const char *fmt, ...);
void SetErrorV(az_result code, const char *fmt, va_list args);

// Maps a COM/Win32 HRESULT onto the az_result space.
az_result HrToAz(HRESULT hr);

// Records `hr` (or `fallback` when hr == S_OK) and returns the mapped code.
// `context` names the operation so the message is actionable.
az_result FailHr(HRESULT hr, az_result fallback, const char *context);

// Records a formatted message and returns `code`.
az_result Fail(az_result code, const char *fmt, ...);

// Records a message but reports success -- used for settings a particular
// format cannot honour, which should not fail the whole operation.
az_result Warn(const char *fmt, ...);

// Clears any pending message; use on the success path so a stale error from an
// earlier call on this thread never leaks into a later failure report.
az_result Ok();

// ---------------------------------------------------------------------------
// Strings
// ---------------------------------------------------------------------------

UString Utf8ToU(const char *s);                 // NULL-safe -> empty string
AString UToUtf8A(const UString &s);
std::string UToStd(const UString &s);

// PROPVARIANT accessors. VT_BSTR is how 7-Zip carries both text and raw bytes.
UString PropToUString(const PROPVARIANT &prop);
bool    PropToBytes(const PROPVARIANT &prop, CByteBuffer &out);

inline UString FsToU(const FString &s) { return fs2us(s); }
std::filesystem::path UToPath(const UString &s);
UString PathToU(const std::filesystem::path &p);

// FILETIME carried in a PROPVARIANT -> seconds since the Unix epoch (0 = unset).
Int64 FileTimePropToUnix(const PROPVARIANT &prop);
// Seconds since the Unix epoch -> FILETIME. Returns false when out of range.
bool UnixToFileTimeProp(Int64 unixTime, FILETIME &out);

// CFiTime is FILETIME on Windows and struct timespec elsewhere. TimeUtils.h
// provides FiTime_To_FILETIME on both but only declares the reverse for POSIX,
// so wrap them rather than sprinkle #ifdefs through the call sites.
inline void AzFiTimeToFileTime(const CFiTime &t, FILETIME &out)
{
  FiTime_To_FILETIME(t, out);
}
inline void AzFileTimeToFiTime(const FILETIME &ft, CFiTime &out)
{
  #ifdef _WIN32
  out = ft;
  #else
  // FILETIME_To_timespec is declared at global scope by TimeUtils.h, not in
  // NWindows::NTime like the rest of that header's helpers.
  ::FILETIME_To_timespec(ft, out);
  #endif
}

// ---------------------------------------------------------------------------
// Format table
// ---------------------------------------------------------------------------

struct Format
{
  GUID ClassId;
  UString Name;
  UString Ext;                       // "zip", "tar.gz" -- may be empty
  UString AddExt;
  bool CanRead;
  bool CanWrite;
  UInt32 Flags;
  UInt32 SignatureOffset;
  CByteBuffer Signatures;            // concatenated signature blobs
  CRecordVector<UInt32> SigSizes;    // length of each blob
  Func_IsArc IsArc;                  // may be NULL

  Format() : CanRead(false), CanWrite(false), Flags(0),
             SignatureOffset(0), IsArc(NULL)
  { memset(&ClassId, 0, sizeof(ClassId)); }
};

// Built once from the engine registry on first use.
const CObjectVector<Format> &Formats();

// Case-insensitive match on canonical name ("7z") or extension ("zip").
// Returns -1 when nothing matches.
int FindFormatIndex(const char *nameOrExtension);

HRESULT CreateInArchive(int formatIndex, CMyComPtr<IInArchive> &out);
HRESULT CreateOutArchive(int formatIndex, CMyComPtr<IOutArchive> &out);

// Open a file so that it can be rewritten in place. Lives in az_reader.cpp, where
// the context behind the opaque handle is defined: for the updatable formats one
// COM instance answers both IInArchive and IOutArchive, and the handler keeps
// reading member bytes off this stream while UpdateItems runs.
// *outHandle must be closed with az_archive_close once the copy is done and
// before the temporary is swapped over the original.
az_result OpenForUpdate(const UString &path, const char *passwordUtf8,
                        az_archive *outHandle, CMyComPtr<IOutArchive> &outArchive,
                        UInt32 *outItemCount, AString *outFormatName);

// ---------------------------------------------------------------------------
// Global settings
// ---------------------------------------------------------------------------

UInt32 NumThreads();        // 0 means "one per hardware thread"
void   SetNumThreads(UInt32 n);
UInt64 MemoryLimitMiB();    // 0 means unlimited
void   SetMemoryLimitMiB(UInt64 mib);

// ---------------------------------------------------------------------------
// Streams
//
// Reading from memory reuses the engine's own CBufInStream /
// Create_BufInStream_WithNewBuffer (CPP/7zip/Common/StreamObjects.h). Only the
// two shapes it does not provide are implemented here.
// ---------------------------------------------------------------------------

// Growable seekable out-stream, used by az_writer_open_memory. Container
// writers patch headers in place, so ISequentialOutStream is not enough.
Z7_CLASS_IMP_COM_1(
  CMemOutStream
  , IOutStream)
  Z7_IFACE_COM7_IMP(ISequentialOutStream)
  std::vector<Byte> _buf;
  UInt64 _pos;
public:
  CMemOutStream() : _pos(0) {}
  UInt64 Size() const { return (UInt64)_buf.size(); }
  const Byte *Data() const { return _buf.empty() ? (const Byte *)NULL : &_buf[0]; }
};

// Out-stream forwarding every chunk to an az_write_fn.
Z7_CLASS_IMP_COM_1(
  CSinkOutStream
  , ISequentialOutStream)
  az_write_fn _sink;
  void *_ctx;
  UInt64 _total;
  az_result _status;
public:
  CSinkOutStream() : _sink(NULL), _ctx(NULL), _total(0), _status(AZ_OK) {}
  void Init(az_write_fn sink, void *ctx)
  { _sink = sink; _ctx = ctx; _total = 0; _status = AZ_OK; }
  UInt64 Total() const { return _total; }
  az_result Status() const { return _status; }
};

// ---------------------------------------------------------------------------
// Progress + password plumbing
// ---------------------------------------------------------------------------

class ProgressSink
{
  const az_callbacks *_cb;
  UInt64 _total;
  UInt64 _done;
  UString _entry;
public:
  ProgressSink() : _cb(NULL), _total(0), _done(0) {}
  void Init(const az_callbacks *cb) { _cb = cb; }
  void SetTotal(UInt64 total) { _total = total; }
  void SetCompleted(UInt64 done) { _done = done; }
  void SetEntry(const UString &name) { _entry = name; }
  bool HasCallback() const { return _cb && _cb->progress; }

  // Returns AZ_OK to continue, or the code the caller asked to abort with.
  az_result Report(az_op op, UInt64 index, UInt64 count);
};

// Produces the password BSTR the engine asks for. Prefers `explicitUtf8`, then
// the az_callbacks prompt. *isDefined is 1 only when a password was obtained.
HRESULT AcquirePassword(const az_callbacks *cb, const char *explicitUtf8,
                        Int32 *isDefined, BSTR *out);

// True when `cb` is NULL or carries the expected struct_size. A mismatch means
// the caller was compiled against a different andzip.h, so the struct must not
// be dereferenced; report it rather than reading garbage function pointers.
bool CheckCallbacks(const az_callbacks *cb);

// ---------------------------------------------------------------------------
// Path safety
// ---------------------------------------------------------------------------

// Turns an entry name from an archive into a path that cannot escape `destDir`:
// drive letters and rooted prefixes are dropped, ".." never climbs above
// `destDir`, and backslashes fold to '/'. Returns false when nothing usable is
// left after sanitising.
bool SafeJoin(const UString &destDir, const UString &entryName, UString &outFullPath);

// Creates every missing directory component of `fullPath` (not the leaf).
az_result MakeDirTree(const UString &fullPath);

// True when the entry name denotes a directory in this container.
bool EntryNameIsDir(const UString &name);

// 7-Zip's handlers hand kpidPath back in OS form -- Archive/Common/ItemNameUtils.cpp
// converts '/' to WCHAR_PATH_SEPARATOR on Windows. The ABI promises '/', so fold
// it back. On POSIX a backslash is an ordinary filename character and is left
// untouched.
void NormalizeEntryName(UString &name);

// ---------------------------------------------------------------------------
// Out-parameters that hand back strings
// ---------------------------------------------------------------------------

// az_format_info / az_codec_info / az_entry_info return UTF-8 strings that
// must outlive the call. Bag owns them all behind the opaque `_bag` field; the
// matching az_*_release frees it. std::deque rather than std::vector because
// vector growth would invalidate the c_str() pointers already handed out.
class StringBag
{
  std::deque<std::string> _items;
public:
  const char *Add(const std::string &s)
  {
    _items.push_back(s);
    return _items.back().c_str();
  }
  const char *Add(const UString &s) { return Add(UToStd(s)); }
  // Empty strings come back as NULL so callers can test with `if (x->name)`.
  const char *AddOptional(const std::string &s) { return s.empty() ? NULL : Add(s); }
  const char *AddOptional(const UString &s) { return AddOptional(UToStd(s)); }
};

// Formats tried when signature detection and the file extension both come up
// empty, in the order a user is most likely to have meant.
extern const char *const kFallbackFormats[];
extern const size_t kFallbackFormatsCount;

} // namespace az
