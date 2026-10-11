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

} // namespace az
