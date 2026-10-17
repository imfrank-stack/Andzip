// az_hash.cpp -- access to the engine's hashers (CRC32, CRC64, SHA-1/256/512,
// BLAKE2sp, xxHash, ...) through the IHashers registry.
//
// Two of the names this ABI advertises are not in that registry, and both are
// served from the fallback below rather than reported as unknown:
//   SHA-384   Common/Sha3Reg.cpp keeps that registration commented out, so the
//             menu item exists upstream while the hasher behind it does not.
//   BLAKE2sp  the only IHasher for it lives inside the Rar5 handler
//             (Archive/Rar/Rar5Handler.cpp:3410) and is reachable only through
//             that handler's GetHashers(), which a statically linked build
//             never asks.
// Both C implementations are vendored and already compiled into the library
// (C/Sha3.c, and C/Blake2s.c for RAR5 header checks), so this drives the same
// code the registry hashers do. Digests are compared byte for byte against
// 7-Zip 26.04 in Andzip.Native.Tests.

#include "az_internal.h"

#include "C/Sha3.h"
#include "C/Blake2.h"

namespace {

enum Fallback
{
  kFallback_None = 0,
  kFallback_Sha3_384,
  kFallback_Blake2sp
};

// The spellings 7-Zip itself accepts: its command line takes -scrcSHA384, its
// menus say "SHA-384", and the header here has advertised "BLAKE2sp" all along.
int FallbackKind(const char *name)
{
  static const char *const kSha384[]    = { "SHA-384", "SHA384", "SHA3-384" };
  static const char *const kBlake2sp[]  = { "BLAKE2sp", "BLAKE2SP" };

  for (size_t i = 0; i < sizeof(kSha384) / sizeof(kSha384[0]); i++)
    if (StringsAreEqualNoCase_Ascii(name, kSha384[i]))
      return kFallback_Sha3_384;
  for (size_t i = 0; i < sizeof(kBlake2sp) / sizeof(kBlake2sp[0]); i++)
    if (StringsAreEqualNoCase_Ascii(name, kBlake2sp[i]))
      return kFallback_Blake2sp;
  return kFallback_None;
}

} // namespace

struct az_hasher_s
{
  // Both contexts are read as vectors by the SIMD paths, so the allocation has
  // to be 64-byte aligned. CBlake2sp says so itself (MY_ALIGN(64)) but CSha3
  // only carries _pad fields that align its state *relative to its own start*,
  // so alignof(CSha3) is 8 and the guarantee has to come from here. Holding the
  // two in alignas(64) slots makes the whole az_hasher_s over-aligned, which
  // the C++17 new operator honours.
  struct alignas(64) Sha3Slot { CSha3 ctx; } Sha3;
  struct alignas(64) BlakeSlot { CBlake2sp ctx; } Blake;

  CMyComPtr<IHasher> Hasher;   // set unless Fallback says otherwise
  UInt32 DigestSize;
  int Fallback;

  az_hasher_s() : DigestSize(0), Fallback(kFallback_None) {}
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

  std::unique_ptr<az_hasher_s> h(new az_hasher_s());

  // The registry comes first: if a future vendored version registers one of
  // these itself, that implementation is the one used.
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

  if (found >= 0)
  {
    const HRESULT created = hashers->CreateHasher((UInt32)found, &h->Hasher);
    if (created != S_OK || !h->Hasher)
      return az::FailHr(created, AZ_ERROR_INTERNAL, "creating the hasher");
    h->DigestSize = h->Hasher->GetDigestSize();
    h->Hasher->Init();
    *out = h.release();
    return az::Ok();
  }

  h->Fallback = FallbackKind(name);
  switch (h->Fallback)
  {
    case kFallback_Sha3_384:
      // The same two steps CSha3Hasher's constructor does, rate first then the
      // permutation. Sha3_SET_blockSize() is deliberately not used: its
      // replacement text spells the member as the parameter name
      // ("{ (p)->blockSize = (blockSize); }"), so any argument other than
      // something literally called blockSize replaces the member name too and
      // the expansion stops compiling. Upstream only gets away with it because
      // its own caller passes a variable named blockSize.
      h->Sha3.ctx.blockSize = SHA3_BLOCK_SIZE_FROM_DIGEST_SIZE(384 / 8);
      Sha3_Init(&h->Sha3.ctx);
      h->DigestSize = 384 / 8;
      break;

    case kFallback_Blake2sp:
      // Picks the SSE2/AVX2 path once per process. Rar5Handler runs it from a
      // static constructor; calling it again is harmless and keeps this file
      // usable even if that handler ever leaves the build.
      z7_Black2sp_Prepare();
      Blake2sp_Init(&h->Blake.ctx);
      h->DigestSize = Z7_BLAKE2S_DIGEST_SIZE;
      break;

    default:
      return az::Fail(AZ_ERROR_UNSUPPORTED, "unknown hash algorithm '%s'", name);
  }

  *out = h.release();
  return az::Ok();
}

AZ_API void AZ_CALL az_hasher_update(az_hasher h, const void *data, size_t size)
{
  if (!h || !data || size == 0)
    return;

  const Byte *p = (const Byte *)data;

  if (h->Fallback == kFallback_Sha3_384)
  {
    Sha3_Update(&h->Sha3.ctx, p, size);
    return;
  }
  if (h->Fallback == kFallback_Blake2sp)
  {
    Blake2sp_Update(&h->Blake.ctx, p, size);
    return;
  }

  // IHasher::Update takes a UInt32 length, so chunk anything larger.
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

  if (h->Fallback == kFallback_Sha3_384)
    Sha3_Final(&h->Sha3.ctx, digest, 384, 0 /* not SHAKE */);
  else if (h->Fallback == kFallback_Blake2sp)
    Blake2sp_Final(&h->Blake.ctx, digest);
  else
    h->Hasher->Final(digest);

  if (written)
    *written = h->DigestSize;
  return az::Ok();
}

AZ_API void AZ_CALL az_hasher_close(az_hasher h)
{
  delete h;
}
