// az_formats.cpp -- format and codec tables read out of the engine registry,
// plus handler construction.
//
// 7-Zip registers every container and coder through static initialisers in the
// *Register.cpp files; ArchiveExports.cpp and CodecExports.cpp expose that
// registry. We snapshot it once so callers get a stable, indexable view.

#include "az_internal.h"

namespace az {

const char *const kFallbackFormats[] = {
  "7z", "zip", "tar", "gz", "xz", "bzip2", "rar", "cab", "iso", "wim",
  "zst", "lz4", "lzma", "cpio", "arj", "lzh"
};
const size_t kFallbackFormatsCount = sizeof(kFallbackFormats) / sizeof(kFallbackFormats[0]);

namespace {

CObjectVector<Format> *BuildFormats()
{
  CObjectVector<Format> *table = new CObjectVector<Format>();

  UInt32 count = 0;
  if (GetNumberOfFormats(&count) != S_OK)
    return table;

  for (UInt32 i = 0; i < count; i++)
  {
    Format &f = table->AddNew();
    PROPVARIANT prop;
    ::PropVariantInit(&prop);

    if (GetHandlerProperty2(i, NArchive::NHandlerPropID::kName, &prop) == S_OK)
      f.Name = PropToUString(prop);
    if (GetHandlerProperty2(i, NArchive::NHandlerPropID::kClassID, &prop) == S_OK)
    {
      // The class id comes back as 16 raw bytes wrapped in a BSTR.
      if (prop.vt == VT_BSTR && prop.bstrVal && ::SysStringByteLen(prop.bstrVal) == sizeof(GUID))
        memcpy(&f.ClassId, prop.bstrVal, sizeof(GUID));
    }
    if (GetHandlerProperty2(i, NArchive::NHandlerPropID::kExtension, &prop) == S_OK)
      f.Ext = PropToUString(prop);
    if (GetHandlerProperty2(i, NArchive::NHandlerPropID::kAddExtension, &prop) == S_OK)
      f.AddExt = PropToUString(prop);
    if (GetHandlerProperty2(i, NArchive::NHandlerPropID::kUpdate, &prop) == S_OK)
      f.CanWrite = (prop.vt == VT_BOOL && prop.boolVal != VARIANT_FALSE);
    if (GetHandlerProperty2(i, NArchive::NHandlerPropID::kFlags, &prop) == S_OK && prop.vt == VT_UI4)
      f.Flags = prop.ulVal;
    if (GetHandlerProperty2(i, NArchive::NHandlerPropID::kSignatureOffset, &prop) == S_OK
        && prop.vt == VT_UI4)
      f.SignatureOffset = prop.ulVal;

    // GetHandlerProperty2 answers S_OK even when it leaves the variant empty
    // (a multi-signature format asked for kSignature, say), so the return code
    // alone cannot select the branch -- check whether bytes actually arrived.
    bool gotSignature = false;
    if (GetHandlerProperty2(i, NArchive::NHandlerPropID::kSignature, &prop) == S_OK)
      gotSignature = PropToBytes(prop, f.Signatures);

    if (gotSignature)
      f.SigSizes.Add((UInt32)f.Signatures.Size());
    else if (GetHandlerProperty2(i, NArchive::NHandlerPropID::kMultiSignature, &prop) == S_OK
             && PropToBytes(prop, f.Signatures))
    {
      // Several blobs of unknown individual length concatenated together. The
      // per-blob list cannot be rebuilt from that, so byte matching is off and
      // detection leans on Format::IsArc instead.
      f.SignatureOffset = 0;
    }

    ::PropVariantClear(&prop);

    // Every registered handler exposes IInArchive; only some expose IOutArchive.
    f.CanRead = true;
    GetIsArc(i, &f.IsArc);
  }

  return table;
}

} // namespace

const CObjectVector<Format> &Formats()
{
  // Deliberately leaked: it avoids static-destruction-order problems when the
  // host process tears down while a handle is still open.
  static CObjectVector<Format> *const table = BuildFormats();
  return *table;
}

int FindFormatIndex(const char *nameOrExtension)
{
  if (!nameOrExtension || !*nameOrExtension)
    return -1;

  UString wanted = Utf8ToU(nameOrExtension);

  // Accept ".zip", "zip" and "backup/archive.tar.gz" alike.
  const int sep = wanted.ReverseFind_PathSepar();
  if (sep >= 0)
    wanted.Delete((unsigned)sep + 1, wanted.Len() - (unsigned)sep - 1);
  if (!wanted.IsEmpty() && wanted[0] == L'.')
    wanted.Delete(0);
  if (wanted.IsEmpty())
    return -1;

  // A multi-token extension list ("tar.gz tar tgz") still has to match whole
  // tokens, so try the longest suffixes first.
  const CObjectVector<Format> &formats = Formats();

  FOR_VECTOR(i, formats)
    if (formats[i].Name.IsEqualTo_NoCase(wanted.Ptr()))
      return (int)i;

  FOR_VECTOR(i, formats)
  {
    UString ext = formats[i].Ext;
    unsigned pos = 0;
    for (;;)
    {
      const int space = ext.Find(L' ', pos);
      UString token;
      if (space < 0)
        token = ext.Ptr(pos);
      else
        token = ext.Mid(pos, (unsigned)space - pos);
      if (token.IsEqualTo_NoCase(wanted.Ptr()))
        return (int)i;
      if (space < 0)
        break;
      pos = (unsigned)space + 1;
      if (pos >= ext.Len())
        break;
    }
  }

  return -1;
}

HRESULT CreateInArchive(int formatIndex, CMyComPtr<IInArchive> &out)
{
  const CObjectVector<Format> &formats = Formats();
  if (formatIndex < 0 || (unsigned)formatIndex >= formats.Size())
    return E_INVALIDARG;
  out.Release();
  return CreateObject(&formats[formatIndex].ClassId, &IID_IInArchive, (void **)&out);
}

HRESULT CreateOutArchive(int formatIndex, CMyComPtr<IOutArchive> &out)
{
  const CObjectVector<Format> &formats = Formats();
  if (formatIndex < 0 || (unsigned)formatIndex >= formats.Size())
    return E_INVALIDARG;
  if (!formats[formatIndex].CanWrite)
    return E_NOTIMPL;
  out.Release();
  return CreateObject(&formats[formatIndex].ClassId, &IID_IOutArchive, (void **)&out);
}

} // namespace az

// ---------------------------------------------------------------------------
// C ABI -- formats
// ---------------------------------------------------------------------------

AZ_API uint32_t AZ_CALL az_format_count(void)
{
  return (uint32_t)az::Formats().Size();
}

AZ_API az_result AZ_CALL az_format_get(uint32_t index, az_format_info *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_format_get: out is NULL");
  if (out->struct_size != sizeof(az_format_info))
    return az::Fail(AZ_ERROR_INVALID_ARG,
                    "az_format_info.struct_size is %u, expected %u (ABI mismatch)",
                    (unsigned)out->struct_size, (unsigned)sizeof(az_format_info));

  const CObjectVector<az::Format> &formats = az::Formats();
  if (index >= (uint32_t)formats.Size())
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_format_get: index %u out of range (%u formats)",
                    index, (unsigned)formats.Size());

  const az::Format &f = formats[index];

  std::unique_ptr<az::StringBag> bag(new az::StringBag());
  memset(out, 0, sizeof(*out));
  out->struct_size      = sizeof(az_format_info);
  out->index            = index;
  out->name             = bag->Add(f.Name);
  out->extension        = bag->AddOptional(f.Ext);
  out->add_extension    = bag->AddOptional(f.AddExt);
  out->can_read         = f.CanRead ? 1 : 0;
  out->can_write        = f.CanWrite ? 1 : 0;
  out->flags            = f.Flags;
  out->signatures       = reinterpret_cast<const char *>((const Byte *)f.Signatures);
  out->signatures_size  = (uint32_t)f.Signatures.Size();
  out->_bag             = bag.release();
  return az::Ok();
}

AZ_API void AZ_CALL az_format_release(az_format_info *info)
{
  if (!info)
    return;
  delete static_cast<az::StringBag *>(info->_bag);
  info->_bag = NULL;
}

AZ_API az_result AZ_CALL az_format_find(const char *name_or_extension, uint32_t *out_index)
{
  if (!out_index)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_format_find: out_index is NULL");

  const int i = az::FindFormatIndex(name_or_extension);
  if (i < 0)
    return az::Fail(AZ_ERROR_NOT_FOUND, "unknown format '%s'",
                    name_or_extension ? name_or_extension : "(null)");
  *out_index = (uint32_t)i;
  return az::Ok();
}

// ---------------------------------------------------------------------------
// C ABI -- codecs
// ---------------------------------------------------------------------------

AZ_API uint32_t AZ_CALL az_codec_count(void)
{
  UInt32 n = 0;
  if (GetNumberOfMethods(&n) != S_OK)
    return 0;
  return (uint32_t)n;
}

AZ_API az_result AZ_CALL az_codec_get(uint32_t index, az_codec_info *out)
{
  if (!out)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_codec_get: out is NULL");
  if (out->struct_size != sizeof(az_codec_info))
    return az::Fail(AZ_ERROR_INVALID_ARG,
                    "az_codec_info.struct_size is %u, expected %u (ABI mismatch)",
                    (unsigned)out->struct_size, (unsigned)sizeof(az_codec_info));

  UInt32 total = 0;
  if (GetNumberOfMethods(&total) != S_OK || index >= total)
    return az::Fail(AZ_ERROR_INVALID_ARG, "az_codec_get: index %u out of range (%u methods)",
                    index, (unsigned)total);

  std::unique_ptr<az::StringBag> bag(new az::StringBag());
  memset(out, 0, sizeof(*out));
  out->struct_size = sizeof(az_codec_info);
  out->index = index;

  PROPVARIANT prop;
  ::PropVariantInit(&prop);

  bool haveName = GetMethodProperty(index, NMethodPropID::kName, &prop) == S_OK;
  out->name = bag->Add(haveName ? az::PropToUString(prop) : UString());

  ::PropVariantClear(&prop);
  std::string idHex;
  if (GetMethodProperty(index, NMethodPropID::kID, &prop) == S_OK && prop.vt == VT_UI8)
  {
    // GetMethodProperty reports the method id as a UInt64, not as the binary
    // blob the 7z header stores. Render it the way 7-Zip documents ids
    // (DOC/Methods.txt): big-endian hex, leading zero nibble dropped.
    static const char digits[] = "0123456789abcdef";
    const UInt64 id = prop.uhVal.QuadPart;
    int top = 7;
    while (top > 0 && ((id >> (top * 8)) & 0xFFu) == 0)
      top--;
    for (int b = top; b >= 0; b--)
    {
      const unsigned byte = (unsigned)((id >> (b * 8)) & 0xFFu);
      if (b == top && top > 0 && (byte >> 4) == 0)
        idHex.push_back(digits[byte & 15]);
      else
      {
        idHex.push_back(digits[byte >> 4]);
        idHex.push_back(digits[byte & 15]);
      }
    }
  }
  out->id_hex = bag->Add(idHex);

  auto flag = [&](PROPID id) {
    ::PropVariantClear(&prop);
    return GetMethodProperty(index, id, &prop) == S_OK
        && prop.vt == VT_BOOL && prop.boolVal != VARIANT_FALSE;
  };
  out->can_decode = flag(NMethodPropID::kDecoderIsAssigned) ? 1 : 0;
  out->can_encode = flag(NMethodPropID::kEncoderIsAssigned) ? 1 : 0;
  out->is_filter  = flag(NMethodPropID::kIsFilter) ? 1 : 0;

  ::PropVariantClear(&prop);
  out->_bag = bag.release();
  return az::Ok();
}

AZ_API void AZ_CALL az_codec_release(az_codec_info *info)
{
  if (!info)
    return;
  delete static_cast<az::StringBag *>(info->_bag);
  info->_bag = NULL;
}
