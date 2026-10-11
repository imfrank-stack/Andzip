// az_exports.cpp -- global library state: build information and the two
// process-wide knobs (worker threads, memory ceiling).

#include "az_internal.h"

#include "C/7zVersion.h"

namespace az {
namespace {

bool HasEncoderNamed(const char *name)
{
  UInt32 count = 0;
  if (GetNumberOfMethods(&count) != S_OK)
    return false;

  for (UInt32 i = 0; i < count; i++)
  {
    PROPVARIANT prop;
    ::PropVariantInit(&prop);
    if (GetMethodProperty(i, NMethodPropID::kName, &prop) != S_OK)
      continue;

    bool named = false;
    if (prop.vt == VT_BSTR && prop.bstrVal)
      named = StringsAreEqualNoCase_Ascii(prop.bstrVal, name);
    ::PropVariantClear(&prop);
    if (!named)
      continue;

    ::PropVariantClear(&prop);
    const bool ok = GetMethodProperty(i, NMethodPropID::kEncoderIsAssigned, &prop) == S_OK
                 && prop.vt == VT_BOOL && prop.boolVal != VARIANT_FALSE;
    ::PropVariantClear(&prop);
    if (ok)
      return true;
  }
  return false;
}

} // namespace
} // namespace az

AZ_API az_result AZ_CALL az_get_build_info(az_build_info *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_get_build_info: out is NULL");
  if (out->struct_size != sizeof(az_build_info))
    return az::Fail(AZ_ERROR_INVALID_ARG,
                    "az_build_info.struct_size is %u, expected %u (ABI mismatch)",
                    (unsigned)out->struct_size, (unsigned)sizeof(az_build_info));

  // Static storage: the strings must outlive every call.
  static const char *const kCoreVersion = ANDZIP_CORE_VERSION;
  static const char *const kEngine = "7-Zip " MY_VERSION_CPU;
  static const char *const kConfig = ANDZIP_BUILD_CONFIG;

  memset(out, 0, sizeof(*out));
  out->struct_size   = sizeof(az_build_info);
  out->abi_version   = AZ_ABI_VERSION;
  out->core_version  = kCoreVersion;
  out->engine        = kEngine;
  out->build_config  = kConfig;
  out->format_count  = az_format_count();
  out->codec_count   = az_codec_count();
  out->has_zstd_encoder   = az::HasEncoderNamed("Zstd") ? 1u : 0u;
  out->has_lz4_encoder    = az::HasEncoderNamed("LZ4") ? 1u : 0u;
  out->has_brotli_encoder = az::HasEncoderNamed("Brotli") ? 1u : 0u;
  return az::Ok();
}

AZ_API void AZ_CALL az_set_num_threads(uint32_t threads)
{
  az::SetNumThreads((UInt32)threads);
}

AZ_API uint32_t AZ_CALL az_get_num_threads(void)
{
  return (uint32_t)az::NumThreads();
}

AZ_API void AZ_CALL az_set_memory_limit_mib(uint64_t mib)
{
  az::SetMemoryLimitMiB((UInt64)mib);
}

AZ_API uint64_t AZ_CALL az_get_memory_limit_mib(void)
{
  return (uint64_t)az::MemoryLimitMiB();
}
