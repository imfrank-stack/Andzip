// az_util.cpp -- string conversion, global settings, path safety, progress and
// password plumbing shared by the reader and the writer.

#include "az_internal.h"

#include <atomic>

namespace az {

// ---------------------------------------------------------------------------
// Strings
// ---------------------------------------------------------------------------

UString Utf8ToU(const char *s)
{
  UString result;
  if (!s || !*s)
    return result;
  // 7-Zip's converter keeps unpaired UTF-8 bytes as escape-coded surrogates
  // instead of failing, which is what we want for names that are not really
  // UTF-8 (common in archives written by legacy tools).
  ConvertUTF8ToUnicode(AString(s), result);
  return result;
}

AString UToUtf8A(const UString &s)
{
  AString result;
  ConvertUnicodeToUTF8(s, result);
  return result;
}

std::string UToStd(const UString &s)
{
  const AString a = UToUtf8A(s);
  return std::string(a.Ptr(), a.Len());
}

std::filesystem::path UToPath(const UString &s)
{
  #ifdef _WIN32
  UString t = s;
  t.Replace(L'/', WCHAR_PATH_SEPARATOR);
  return std::filesystem::path(t.Ptr());
  #else
  return std::filesystem::path(UToStd(s));
  #endif
}

UString PropToUString(const PROPVARIANT &prop)
{
  if (prop.vt == VT_BSTR && prop.bstrVal)
    return UString(prop.bstrVal);
  #ifdef _WIN32
  // VT_LPSTR/pszVal only exist in the real Windows PROPVARIANT; 7-Zip's
  // emulation carries text as BSTR on every platform.
  if (prop.vt == VT_LPSTR && prop.pszVal)
    return GetUnicodeString(prop.pszVal);
  #endif
  if (prop.vt == VT_UI4)
  {
    UString s;
    s.Add_UInt32(prop.ulVal);
    return s;
  }
  if (prop.vt == VT_UI8)
  {
    UString s;
    s.Add_UInt64(prop.uhVal.QuadPart);
    return s;
  }
  return UString();
}

bool PropToBytes(const PROPVARIANT &prop, CByteBuffer &out)
{
  if (prop.vt != VT_BSTR || !prop.bstrVal)
    return false;
  const UInt32 len = ::SysStringByteLen(prop.bstrVal);
  if (len == 0)
    return false;
  out.Alloc(len);
  memcpy(out, prop.bstrVal, len);
  return true;
}

UString PathToU(const std::filesystem::path &p)
{
  #ifdef _WIN32
  return UString(p.wstring().c_str());
  #else
  return Utf8ToU(p.string().c_str());
  #endif
}

Int64 FileTimePropToUnix(const PROPVARIANT &prop)
{
  if (prop.vt != VT_FILETIME)
    return 0;
  if (prop.filetime.dwLowDateTime == 0 && prop.filetime.dwHighDateTime == 0)
    return 0;
  return NWindows::NTime::FileTime_To_UnixTime64(prop.filetime);
}

bool UnixToFileTimeProp(Int64 unixTime, FILETIME &out)
{
  return NWindows::NTime::UnixTime64_To_FileTime(unixTime, out);
}

// ---------------------------------------------------------------------------
// Global settings
// ---------------------------------------------------------------------------

namespace {
std::atomic<UInt32> g_numThreads(0);
std::atomic<unsigned long long> g_memLimitMiB(0);
}

UInt32 NumThreads() { return g_numThreads.load(std::memory_order_relaxed); }
void SetNumThreads(UInt32 n) { g_numThreads.store(n, std::memory_order_relaxed); }
UInt64 MemoryLimitMiB() { return (UInt64)g_memLimitMiB.load(std::memory_order_relaxed); }
void SetMemoryLimitMiB(UInt64 mib) { g_memLimitMiB.store((unsigned long long)mib, std::memory_order_relaxed); }

// ---------------------------------------------------------------------------
// Path safety
// ---------------------------------------------------------------------------

bool EntryNameIsDir(const UString &name)
{
  if (name.IsEmpty())
    return false;
  const wchar_t last = name[name.Len() - 1];
  return last == L'/' || last == WCHAR_PATH_SEPARATOR;
}

void NormalizeEntryName(UString &name)
{
  #ifdef _WIN32
  name.Replace(L'\\', L'/');
  #else
  UNUSED_VAR(name)
  #endif
}

bool SafeJoin(const UString &destDir, const UString &entryName, UString &outFullPath)
{
  UString name = entryName;
  name.Replace(WCHAR_PATH_SEPARATOR, L'/');

  // A leading drive letter ("C:foo", "C:") would otherwise escape destDir on
  // Windows; UNC prefixes collapse to empty components below.
  if (name.Len() >= 2 && name[1] == L':' && ((name[0] >= L'A' && name[0] <= L'Z')
      || (name[0] >= L'a' && name[0] <= L'z')))
    name.Delete(0, 2);

  CObjectVector<UString> parts;
  {
    unsigned pos = 0;
    for (;;)
    {
      const int slash = name.Find(L'/', pos);
      if (slash < 0)
      {
        parts.Add(name.Ptr(pos));
        break;
      }
      parts.Add(name.Mid(pos, (unsigned)slash - pos));
      pos = (unsigned)slash + 1;
    }
  }

  const bool trailingSlash = EntryNameIsDir(entryName);

  CObjectVector<UString> clean;
  FOR_VECTOR(i, parts)
  {
    UString part = parts[i];
    if (part.IsEmpty() || part == L".")
      continue;
    if (part == L"..")
    {
      // Never climb above the destination; drop the component instead of
      // failing so a single odd entry cannot abort a whole extraction.
      if (!clean.IsEmpty())
        clean.DeleteBack();
      continue;
    }
    #ifdef _WIN32
    // ':' introduces an NTFS alternate data stream and is illegal in a plain
    // file name; fold it so the entry lands as an ordinary file.
    part.Replace(L':', L'_');
    // Trailing dots and spaces are stripped by Win32 and would silently change
    // the name, so remove them up front.
    while (!part.IsEmpty())
    {
      const wchar_t c = part[part.Len() - 1];
      if (c == L' ' || c == L'.')
        part.DeleteBack();
      else
        break;
    }
    if (part.IsEmpty())
      continue;
    #endif
    clean.Add(part);
  }

  if (clean.IsEmpty())
    return false;

  UString dest = destDir;
  while (!dest.IsEmpty())
  {
    const wchar_t c = dest[dest.Len() - 1];
    if (c == L'/' || c == WCHAR_PATH_SEPARATOR)
      dest.DeleteBack();
    else
      break;
  }

  UString joined;
  FOR_VECTOR(k, clean)
  {
    if (k != 0)
      joined += L'/';
    joined += clean[k];
  }
  if (trailingSlash)
    joined += L'/';

  outFullPath = dest.IsEmpty() ? joined : dest + WCHAR_PATH_SEPARATOR + joined;
  #ifdef _WIN32
  outFullPath.Replace(L'/', WCHAR_PATH_SEPARATOR);
  #endif
  return true;
}

az_result MakeDirTree(const UString &fullPath)
{
  std::error_code ec;
  const std::filesystem::path p = UToPath(fullPath);
  const std::filesystem::path parent = p.parent_path();
  if (parent.empty())
    return AZ_OK;

  if (std::filesystem::exists(parent, ec))
    return AZ_OK;

  ec.clear();
  std::filesystem::create_directories(parent, ec);
  if (ec)
  {
    // Report the parent as UTF-8 so the message is readable from managed code.
    std::string parentText;
    #ifdef _WIN32
    parentText = UToStd(UString(parent.wstring().c_str()));
    #else
    parentText = parent.string();
    #endif
    return Fail(AZ_ERROR_IO, "cannot create directory '%s': %s",
                parentText.c_str(), ec.message().c_str());
  }
  return AZ_OK;
}

// ---------------------------------------------------------------------------
// Progress
// ---------------------------------------------------------------------------

az_result ProgressSink::Report(az_op op, UInt64 index, UInt64 count)
{
  if (!HasCallback())
    return AZ_OK;

  az_progress p;
  memset(&p, 0, sizeof(p));
  p.op = op;
  const AString entry = UToUtf8A(_entry);
  p.current_entry = entry.IsEmpty() ? NULL : entry.Ptr();
  p.entry_index = index;
  p.entry_count = count;
  // SetTotal/SetCompleted on IProgress carry one direction only; which one
  // depends on the operation (packed bytes when extracting, source bytes when
  // compressing). We report it as the "in" pair and leave "out" unset.
  p.bytes_in = _done;
  p.total_in = _total;
  return _cb->progress(_cb->ctx, &p);
}

// ---------------------------------------------------------------------------
// Passwords
// ---------------------------------------------------------------------------

bool CheckCallbacks(const az_callbacks *cb)
{
  return !cb || cb->struct_size == sizeof(az_callbacks);
}

HRESULT AcquirePassword(const az_callbacks *cb, const char *explicitUtf8,
                        Int32 *isDefined, BSTR *out)
{
  *isDefined = 0;
  *out = NULL;

  UString password;
  bool have = false;

  if (explicitUtf8 && *explicitUtf8)
  {
    password = Utf8ToU(explicitUtf8);
    have = true;
  }
  else if (cb && cb->password)
  {
    char buf[1024];
    buf[0] = 0;
    if (cb->password(cb->ctx, buf, sizeof(buf)))
    {
      buf[sizeof(buf) - 1] = 0;
      password = Utf8ToU(buf);
      have = true;
    }
    // Wipe the plaintext copy regardless of whether the caller used it.
    memset(buf, 0, sizeof(buf));
  }

  if (!have)
    return S_OK;

  *out = ::SysAllocString(password.Ptr());
  if (!*out)
    return E_OUTOFMEMORY;
  *isDefined = 1;
  return S_OK;
}

} // namespace az
