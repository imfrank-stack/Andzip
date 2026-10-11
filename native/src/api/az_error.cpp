// az_error.cpp -- thread-local error reporting and HRESULT mapping.

#include "az_internal.h"

#ifndef HRESULT_FACILITY
#define HRESULT_FACILITY(hr) (((hr) >> 16) & 0x1fff)
#endif
#ifndef HRESULT_CODE
#define HRESULT_CODE(hr) ((hr) & 0xFFFF)
#endif
#ifndef FACILITY_WIN32
#define FACILITY_WIN32 7
#endif

namespace {

// One message buffer per thread. az_last_error_message() hands out c_str()
// from here, so it stays valid until this thread makes another az_* call.
thread_local std::string g_message;
thread_local az_result g_code = AZ_OK;

unsigned Win32FromHresult(HRESULT hr)
{
  return HRESULT_FACILITY(hr) == FACILITY_WIN32 ? (unsigned)HRESULT_CODE(hr) : 0u;
}

} // namespace

namespace az {

void SetErrorV(az_result code, const char *fmt, va_list args)
{
  char buf[1024];
  if (fmt)
  {
    vsnprintf(buf, sizeof(buf), fmt, args);
    buf[sizeof(buf) - 1] = 0;
  }
  else
    buf[0] = 0;

  g_message.assign(buf);
  g_code = code;
}

void SetError(az_result code, const char *fmt, ...)
{
  va_list args;
  va_start(args, fmt);
  SetErrorV(code, fmt, args);
  va_end(args);
}

az_result Fail(az_result code, const char *fmt, ...)
{
  va_list args;
  va_start(args, fmt);
  SetErrorV(code, fmt, args);
  va_end(args);
  return code;
}

az_result Ok()
{
  g_message.clear();
  g_code = AZ_OK;
  return AZ_OK;
}

az_result Warn(const char *fmt, ...)
{
  va_list args;
  va_start(args, fmt);
  SetErrorV(AZ_OK, fmt, args);
  va_end(args);
  return AZ_OK;
}

az_result HrToAz(HRESULT hr)
{
  if (SUCCEEDED(hr))
    return AZ_OK;

  switch (hr)
  {
    case E_INVALIDARG:          return AZ_ERROR_INVALID_ARG;
    case E_OUTOFMEMORY:         return AZ_ERROR_OUT_OF_MEMORY;
    case E_NOTIMPL:             return AZ_ERROR_UNSUPPORTED;
    case E_NOINTERFACE:         return AZ_ERROR_UNSUPPORTED;
    case E_ABORT:               return AZ_ERROR_CANCELLED;
    case E_FAIL:                return AZ_ERROR_GENERIC;
    case E_UNEXPECTED:          return AZ_ERROR_INTERNAL;
    case STG_E_INVALIDFUNCTION: return AZ_ERROR_UNSUPPORTED;
    default: break;
  }

#ifdef _WIN32
  switch (Win32FromHresult(hr))
  {
    case 0:
      break;
    case ERROR_FILE_NOT_FOUND:
    case ERROR_PATH_NOT_FOUND:
    case ERROR_NO_MORE_FILES:
      return AZ_ERROR_NOT_FOUND;
    case ERROR_NOT_ENOUGH_MEMORY:
      return AZ_ERROR_OUT_OF_MEMORY;
    case ERROR_ACCESS_DENIED:
    case ERROR_SHARING_VIOLATION:
    case ERROR_LOCK_VIOLATION:
    case ERROR_DISK_FULL:
    case ERROR_INVALID_NAME:
    case ERROR_FILENAME_EXCED_RANGE:
    case ERROR_DIR_NOT_EMPTY:
    case ERROR_ALREADY_EXISTS:
      return AZ_ERROR_IO;
    default:
      break;
  }
#endif

  return AZ_ERROR_IO;
}

az_result FailHr(HRESULT hr, az_result fallback, const char *context)
{
  if (SUCCEEDED(hr))
    return Ok();

  const az_result code = HrToAz(hr);
  const unsigned win32 = Win32FromHresult(hr);

  char buf[512];
  if (win32)
    snprintf(buf, sizeof(buf), "%s failed (HRESULT 0x%08lX, Win32 %u)",
             context ? context : "operation", (unsigned long)hr, win32);
  else
    snprintf(buf, sizeof(buf), "%s failed (HRESULT 0x%08lX)",
             context ? context : "operation", (unsigned long)hr);
  buf[sizeof(buf) - 1] = 0;

  g_message.assign(buf);
  g_code = code;
  UNUSED_VAR(fallback)
  return code;
}

} // namespace az

// ---------------------------------------------------------------------------
// C ABI
// ---------------------------------------------------------------------------

AZ_API const char *AZ_CALL az_last_error_message(void)
{
  return g_message.empty() ? "" : g_message.c_str();
}

AZ_API uint32_t AZ_CALL az_abi_version(void)
{
  return AZ_ABI_VERSION;
}
