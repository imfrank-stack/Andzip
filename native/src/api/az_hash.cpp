// az_hash.cpp -- access to the engine's hashers (CRC32, CRC64, SHA-1/256/512,
// BLAKE2sp, xxHash, ...) through the IHashers registry.

#include "az_internal.h"

struct az_hasher_s
{
  CMyComPtr<IHasher> Hasher;
  UInt32 DigestSize;
  az_hasher_s() : DigestSize(0) {}
};

namespace az {
namespace {

bool HasherNameEquals(IHashers *registry, UInt32 index, const char *wanted)
{
  PROPVARIANT prop;
  ::PropVariantInit(&prop);
  if (registry->GetHasherProp(index, NMethodPropID::kName, &prop) != S_OK)
    return false;

  bool equal = false;
  if (prop.vt == VT_BSTR && prop.bstrVal)
    equal = StringsAreEqualNoCase_Ascii(prop.bstrVal, wanted);
  ::PropVariantClear(&prop);
  return equal;
}

} // namespace
} // namespace az

AZ_API az_result AZ_CALL az_hasher_open(const char *name, az_hasher *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_hasher_open: out is NULL");
  *out = NULL;
  if (!name || !*name)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_hasher_open: name is empty");

  CMyComPtr<IHashers> hashers;
  const HRESULT res = GetHashers(&hashers);
  if (res != S_OK || !hashers)
    return az::FailHr(res, AZ_ERROR_INTERNAL, "loading the hasher registry");

  const UInt32 count = hashers->GetNumHashers();
  int found = -1;
  for (UInt32 i = 0; i < count; i++)
  {
    if (az::HasherNameEquals(hashers, i, name))
    {
      found = (int)i;
      break;
    }
  }
  if (found < 0)
    return az::Fail(AZ_ERROR_UNSUPPORTED, "unknown hash algorithm '%s'", name);

  std::unique_ptr<az_hasher_s> h(new az_hasher_s());
  CMyComPtr<IHasher> hasher;
  const HRESULT created = hashers->CreateHasher((UInt32)found, &hasher);
  if (created != S_OK || !hasher)
    return az::FailHr(created, AZ_ERROR_INTERNAL, "creating the hasher");

  h->Hasher = hasher;
  h->DigestSize = hasher->GetDigestSize();
  hasher->Init();

  *out = h.release();
  return az::Ok();
}

AZ_API void AZ_CALL az_hasher_update(az_hasher h, const void *data, size_t size)
{
  if (!h || !data || size == 0)
    return;

  // IHasher::Update takes a UInt32 length, so chunk anything larger.
  const Byte *p = (const Byte *)data;
  size_t left = size;
  while (left)
  {
    const UInt32 n = (left > 0x40000000u) ? 0x40000000u : (UInt32)left;
    h->Hasher->Update(p, n);
    p += n;
    left -= n;
  }
}

AZ_API az_result AZ_CALL az_hasher_finish(az_hasher h, uint8_t *digest, size_t capacity,
                                          size_t *written)
{
  if (written)
    *written = 0;
  if (!h)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_hasher_finish: hasher is NULL");
  if (!digest)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_hasher_finish: digest is NULL");
  if (capacity < h->DigestSize)
    return az::Fail(AZ_ERROR_BUFFER_SMALL,
                    "digest buffer is %llu bytes, %u needed",
                    (unsigned long long)capacity, (unsigned)h->DigestSize);

  h->Hasher->Final(digest);
  if (written)
    *written = h->DigestSize;
  return az::Ok();
}

AZ_API void AZ_CALL az_hasher_close(az_hasher h)
{
  delete h;
}
