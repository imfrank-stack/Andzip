// az_streams.cpp -- the two stream shapes 7-Zip does not already provide.
//
// Reading from memory reuses the engine's CBufInStream / CDynBufSeqOutStream /
// CBufPtrSeqOutStream from CPP/7zip/Common/StreamObjects.h.

#include "az_internal.h"

namespace az {

STDMETHODIMP CMemOutStream::Write(const void *data, UInt32 size, UInt32 *processedSize) throw()
{
  if (processedSize)
    *processedSize = 0;
  if (size == 0)
    return S_OK;

  try
  {
    const UInt64 end = _pos + size;
    if (end > (UInt64)_buf.size())
      _buf.resize((size_t)end);
    memcpy(&_buf[(size_t)_pos], data, size);
    _pos = end;
  }
  catch (const std::bad_alloc &)
  {
    return E_OUTOFMEMORY;
  }

  if (processedSize)
    *processedSize = size;
  return S_OK;
}

STDMETHODIMP CMemOutStream::Seek(Int64 offset, UInt32 seekOrigin, UInt64 *newPosition) throw()
{
  if (newPosition)
    *newPosition = 0;

  Int64 target;
  switch (seekOrigin)
  {
    case STREAM_SEEK_SET: target = offset; break;
    case STREAM_SEEK_CUR: target = (Int64)_pos + offset; break;
    case STREAM_SEEK_END: target = (Int64)_buf.size() + offset; break;
    default: return STG_E_INVALIDFUNCTION;
  }

  if (target < 0)
    return HRESULT_WIN32_ERROR_NEGATIVE_SEEK;

  _pos = (UInt64)target;
  if (newPosition)
    *newPosition = _pos;
  return S_OK;
}

STDMETHODIMP CMemOutStream::SetSize(UInt64 newSize) throw()
{
  try
  {
    _buf.resize((size_t)newSize);
  }
  catch (const std::bad_alloc &)
  {
    return E_OUTOFMEMORY;
  }
  if (_pos > newSize)
    _pos = newSize;
  return S_OK;
}

STDMETHODIMP CSinkOutStream::Write(const void *data, UInt32 size, UInt32 *processedSize) throw()
{
  if (processedSize)
    *processedSize = 0;
  if (_status != AZ_OK)
    return E_ABORT;
  if (size == 0)
    return S_OK;

  if (_sink)
  {
    const az_result r = _sink(_ctx, data, size);
    if (r != AZ_OK)
    {
      _status = r;
      return E_ABORT;
    }
  }

  _total += size;
  if (processedSize)
    *processedSize = size;
  return S_OK;
}

// ---------------------------------------------------------------------------
// CVolInStream
// ---------------------------------------------------------------------------

CVolInStream::~CVolInStream()
{
  // _fileRef is a smart pointer and releases the handle on its own; clearing the
  // raw back-reference here only records who owned what.
  _fileSpec = NULL;
}

HRESULT CVolInStream::Init(const CObjectVector<UString> &paths)
{
  _paths.Clear();
  _starts.Clear();
  _sizes.Clear();
  _fileRef.Release();
  _fileSpec = NULL;
  _openIndex = (unsigned)-1;
  _handleRel = 0;
  _pos = 0;

  UInt64 sum = 0;
  FOR_VECTOR(i, paths)
  {
    NWindows::NFile::NFind::CFileInfo fi;
    if (!fi.Find(us2fs(paths[i].Ptr())))
      return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    _paths.Add(paths[i]);
    _starts.Add(sum);
    _sizes.Add(fi.Size);
    sum += fi.Size;
  }

  return _paths.IsEmpty() ? E_FAIL : S_OK;
}

unsigned CVolInStream::VolumeOf(UInt64 pos, UInt64 &relOffset) const
{
  relOffset = 0;
  const unsigned num = (unsigned)_starts.Size();
  if (num == 0 || pos >= Size())
    return (unsigned)-1;

  // Binary search, then a short walk: Read() asks this question for every chunk,
  // and a 1 MiB split of a big archive is a lot of volumes.
  unsigned low = 0, high = num;
  while (low + 1 < high)
  {
    const unsigned mid = low + ((high - low) >> 1);
    if (_starts[mid] <= pos) low = mid; else high = mid;
  }

  UInt64 rel = pos - _starts[low];
  unsigned i = low;
  while (i + 1 < num && rel >= _sizes[i])
  {
    rel -= _sizes[i];
    i++;
  }
  if (rel >= _sizes[i])
    return (unsigned)-1;

  relOffset = rel;
  return i;
}

bool CVolInStream::PositionAt(unsigned index, UInt64 rel)
{
  if (index >= (unsigned)_paths.Size())
    return false;

  if (_openIndex != index)
  {
    // Drop the previous volume's handle before opening the next: keeping it
    // means the file stays locked while the reader works on other volumes.
    _fileRef.Release();
    _fileSpec = NULL;
    _openIndex = (unsigned)-1;

    CInFileStream *spec = new CInFileStream();
    CMyComPtr<IInStream> wrap(spec);
    if (!spec->Open(us2fs(_paths[index].Ptr())))
      return false;
    if (wrap->Seek((Int64)rel, STREAM_SEEK_SET, NULL) != S_OK)
      return false;
    _fileRef = wrap;      // assignment takes its own reference; wrap lets go of
                         // the one it created, leaving exactly one owner
    _fileSpec = spec;
    _openIndex = index;
  }
  else if (_handleRel != rel)
  {
    if (_fileRef->Seek((Int64)rel, STREAM_SEEK_SET, NULL) != S_OK)
      return false;
  }

  _handleRel = rel;
  return true;
}

STDMETHODIMP CVolInStream::Read(void *data, UInt32 size, UInt32 *processedSize) throw()
{
  if (processedSize)
    *processedSize = 0;

  UInt32 done = 0;
  while (done < size)
  {
    UInt64 rel;
    const unsigned index = VolumeOf(_pos, rel);
    if (index == (unsigned)-1)
      break;
    if (!PositionAt(index, rel))
      return E_FAIL;

    const UInt64 room = _sizes[index] - rel;
    const UInt64 asked = (UInt64)(size - done);
    const UInt32 want = (UInt32)(asked < room ? asked : room);
    if (!want)
      break;

    UInt32 got = 0;
    const HRESULT res = _fileRef->Read((Byte *)data + done, want, &got);
    if (res != S_OK)
      return res;
    // A volume shorter than the size we measured is a set being rewritten under
    // us. Stop there rather than inventing bytes to fill the gap.
    if (got == 0)
      break;

    done += got;
    _pos += got;
    _handleRel += got;
  }

  if (processedSize)
    *processedSize = done;
  return S_OK;
}

STDMETHODIMP CVolInStream::Seek(Int64 offset, UInt32 seekOrigin, UInt64 *newPosition) throw()
{
  if (newPosition)
    *newPosition = 0;

  Int64 target;
  switch (seekOrigin)
  {
    case STREAM_SEEK_SET: target = offset; break;
    case STREAM_SEEK_CUR: target = (Int64)_pos + offset; break;
    case STREAM_SEEK_END: target = (Int64)Size() + offset; break;
    default: return STG_E_INVALIDFUNCTION;
  }

  if (target < 0)
    return HRESULT_WIN32_ERROR_NEGATIVE_SEEK;

  // Seeking past the end is legal here the way it is for a plain file: the next
  // read simply returns nothing. Handlers probe sizes with Seek(END, 0).
  _pos = (UInt64)target;
  if (newPosition)
    *newPosition = _pos;
  return S_OK;
}

} // namespace az
