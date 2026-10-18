// codec_registry_probe.cpp -- a stage P2-a experiment, not a product file.
//
// Question it answers (the compression speed-up plan, section 6.1, under docs/):
// when two codecs declare the same MethodID, which one does the engine actually
// create? Everything printed here is read out of the live registry the static
// library carries, so the answer is measured rather than inferred from reading
// CreateCoder.cpp.
//
// Build/run: native/tests/probe/build-and-run.ps1 (it links this file against
// build/native-static/lib/andzip_core.lib with /WHOLEARCHIVE, the same way the
// native test suite does -- static codec registration needs every TU).
//
// Keep this file pure ASCII (everything under native/ is).

#include "../../third_party/7zip/CPP/7zip/Common/CreateCoder.h"
#include "../../third_party/7zip/CPP/7zip/Common/RegisterCodec.h"

#include <cstdio>

// The registry itself. Both are non-static globals in Compress/.../Common/CreateCoder.cpp,
// so they are linkable from a consumer of the static library:
//   unsigned          g_NumCodecs;                       // CreateCoder.cpp:15-16
//   const CCodecInfo *g_Codecs[kNumCodecsMax];           // CreateCoder.cpp:18-19, cap = 64
extern unsigned g_NumCodecs;
extern const CCodecInfo *g_Codecs[];

static const CMethodId kDeflate = 0x40108;  // ZipAddCommon: kMethodId_ZipBase + kDeflate

// An IID nothing else in the tree answers: the probe coder is the only object that
// returns S_OK for it, so "did our class get created" is answered by behaviour and
// not by comparing pointers we cannot name.
static const IID IID_kAzProbeMarker =
    { 0x0a7a1234, 0x3344, 0x5566, { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 } };

class CProbeCoder : public ICompressCoder,
                    public ICompressSetCoderProperties
{
  LONG m_Ref;

public:
  CProbeCoder(): m_Ref(1) {}

  virtual HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void **object) throw()
  {
    if (!object)
      return E_POINTER;
    if (riid == IID_IUnknown || riid == IID_kAzProbeMarker)
    {
      *object = static_cast<ICompressCoder *>(this);
      AddRef();
      return S_OK;
    }
    *object = NULL;
    return E_NOINTERFACE;
  }

  virtual ULONG STDMETHODCALLTYPE AddRef(void) throw() { return (ULONG)InterlockedIncrement(&m_Ref); }
  virtual ULONG STDMETHODCALLTYPE Release(void) throw()
  {
    const ULONG r = (ULONG)InterlockedDecrement(&m_Ref);
    if (r == 0)
      delete this;
    return r;
  }

  // Never called by the probe; present so the interface is complete.
  virtual HRESULT STDMETHODCALLTYPE Code(ISequentialInStream *, ISequentialOutStream *,
      const UInt64 *, const UInt64 *, ICompressProgressInfo *) throw() { return S_OK; }
  virtual HRESULT STDMETHODCALLTYPE SetCoderProperties(const PROPID *, const PROPVARIANT *, UInt32) throw() { return S_OK; }
};

static void *CreateProbeEncoder() { return (void *)(ICompressCoder *)new CProbeCoder; }

// Encoder-only, which is exactly what P2-b would register: the decoder direction stays
// with upstream, so the only way this entry can be chosen is the encode-side lookup.
static const CCodecInfo g_ProbeCodec =
{
  NULL,                    // CreateDecoder
  CreateProbeEncoder,      // CreateEncoder
  kDeflate,                // Id: collides with upstream's Deflate on purpose
  "DeflateProbe",          // a distinct Name, so lookups tell us which entry answered
  1,                       // NumStreams: same as upstream's Deflate entry
  false                    // IsFilter: same as upstream's Deflate entry
};

static bool CreatedIsProbe(const CMethodId id, bool encode, const char *label)
{
  CCreatedCoder cod;
  CMyComPtr<ICompressFilter> filter;
  const HRESULT res = CreateCoder_Id(id, encode, filter, cod);
  if (res != S_OK)
  {
    std::printf("  %-6s CreateCoder_Id(0x%06x, encode=%d) -> HRESULT 0x%08x\n",
        label, (unsigned)id, encode ? 1 : 0, (unsigned)res);
    return false;
  }

  void *marker = NULL;
  CMyComPtr<ICompressCoder> coder = cod.Coder;
  bool mine = false;
  if (coder)
    mine = (coder->QueryInterface(IID_kAzProbeMarker, &marker) == S_OK) && marker != NULL;
  if (marker)
    ((IUnknown *)marker)->Release();

  std::printf("  %-6s encode=%d -> %s (NumStreams=%u isFilter=%d)\n",
      label, encode ? 1 : 0, mine ? "OUR probe coder" : "someone else's",
      cod.NumStreams, cod.IsFilter ? 1 : 0);
  return mine;
}

static void DumpDeflateEntries(const char *when)
{
  std::printf("  %s: g_NumCodecs = %u (cap 64)\n", when, g_NumCodecs);
  for (unsigned i = 0; i < g_NumCodecs; i++)
  {
    const CCodecInfo &c = *g_Codecs[i];
    if (c.Id == kDeflate)
      std::printf("      index %2u  name=%-14s enc=%s dec=%s\n", i, c.Name,
          c.CreateEncoder ? "yes" : "no", c.CreateDecoder ? "yes" : "no");
  }
}

int main()
{
  std::printf("== codec registry probe (method 0x40108 = ZIP's Deflate) ==\n");
  DumpDeflateEntries("before");

  // FindMethod() matches the Id without the direction filter (CreateCoder.cpp:236),
  // so the name it reports is the first entry with that id -- order evidence from the
  // public API rather than from the array.
  AString name;
  const bool found = FindMethod(kDeflate, name);
  std::printf("  FindMethod(0x40108) -> %s\n", found ? name.Ptr() : "(nothing)");
  CreatedIsProbe(kDeflate, true, "encode");
  CreatedIsProbe(kDeflate, false, "decode");

  RegisterCodec(&g_ProbeCodec);
  DumpDeflateEntries("after");

  std::printf("  -- after registering an encoder-only entry with the same Id --\n");
  const bool encodeIsOurs = CreatedIsProbe(kDeflate, true, "encode");
  const bool decodeIsOurs = CreatedIsProbe(kDeflate, false, "decode");
  std::printf("  FindMethod(0x40108) -> %s\n", FindMethod(kDeflate, name) ? name.Ptr() : "(nothing)");

  // Third way: no compile flag, no link order -- rewrite the array slot. The facade
  // lives inside this same library, so g_Codecs is writable from there, and a
  // replacement entry can hand the decoder direction back to the vendor's own
  // CreateDecoder pointer. That keeps Deflate64 (which the flag would also strip)
  // untouched, which is the whole reason to prefer it over Z7_DEFLATE_EXTRACT_ONLY.
  std::printf("  -- after replacing the vendor entry in place (keeping its decoder) --\n");
  const CCodecInfo *vendor = NULL;
  for (unsigned i = 0; i < g_NumCodecs; i++)
    if (g_Codecs[i]->Id == kDeflate && g_Codecs[i]->CreateDecoder)
      vendor = g_Codecs[i];
  if (!vendor)
    std::printf("  no vendor entry with a decoder: nothing to replace\n");
  else
  {
    static CCodecInfo replaced;
    replaced.CreateDecoder = vendor->CreateDecoder;   // the vendor's own decoder creator
    replaced.CreateEncoder = CreateProbeEncoder;      // our encoder
    replaced.Id = kDeflate;
    replaced.Name = vendor->Name;                    // same name: FindMethod reports it
    replaced.NumStreams = vendor->NumStreams;
    replaced.IsFilter = vendor->IsFilter;
    for (unsigned i = 0; i < g_NumCodecs; i++)
      if (g_Codecs[i] == vendor)
        g_Codecs[i] = &replaced;

    const bool swappedEncode = CreatedIsProbe(kDeflate, true, "encode");
    std::printf("  decode  -> %s\n", CreatedIsProbe(kDeflate, false, "decode")
        ? "OUR probe coder (WRONG: the vendor decoder was lost)"
        : "vendor's decoder (kept, as intended)");
    std::printf("  FindMethod(0x40108) -> %s\n", FindMethod(kDeflate, name) ? name.Ptr() : "(nothing)");
    std::printf("== verdict ==\n");
    std::printf("  in-place replacement gives us the encoder direction: %s\n", swappedEncode ? "YES" : "no");
    std::printf("  (P2-a's first build expects \"no\" above: first registered match wins, and our\n"
                "   TUs are linked after the engine's. With -DZ7_DEFLATE_EXTRACT_ONLY the vendor\n"
                "   entry has enc=no, so the encoder-only registration wins without any ordering.)\n");
    return swappedEncode ? 0 : 1;
  }
  return 1;
}
