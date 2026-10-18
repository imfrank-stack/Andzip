// az_deflate_encoder.cpp -- ICompressCoder over zlib-ng's streaming deflate().
//
// The contract this class has to satisfy is the vendor's own, not ours:
//   - interfaces: ICompressCoder + ICompressSetCoderProperties, and nothing else.
//     CCOMCoder is exactly that pair (DeflateEncoder.h:179-188); upstream does not
//     answer ICompressSetCoderPropertiesOpt either, so neither do we.
//   - properties: they arrive already typed and renamed. Common/MethodProps.cpp
//     maps the command-line names, and Archive/Common/HandlerOut.cpp:252-259 turns
//     "x" into NCoderPropID::kLevel. The vendor's rule (DeflateEncoder.cpp:226-244)
//     is: skip kReduceSize and above, require VT_UI4, accept its own list, and
//     return E_INVALIDARG for the rest. We keep the shape but accept only what
//     zlib-ng actually has -- kLevel, plus kNumThreads ignored, because one deflate
//     stream is one thread.
//     That difference is deliberate and it is loud on purpose: 5.6.0 sends "a=0"
//     to this coder. If that name reaches us, the write must fail rather than
//     silently compress with a knob the user believes took effect.
//   - progress: SetRatioInfo(inProcessed, outProcessed), once per input chunk
//     (upstream does it per block, DeflateEncoder.cpp:1010-1013).
//   - cancellation: not a coder concept in this engine. It travels through the
//     streams -- the facade's in/out streams fail their HRESULT once the caller has
//     cancelled -- so every call below has to propagate what it got back, which is
//     what RINOK does. Swallowing one here would show up as a cancelled job that
//     finishes.
//
// What the vendor's coder could not do and this can: deflate(Z_NO_FLUSH) is a real
// streaming entry point that carries history across calls. That is the property
// P1-a named as the selection criterion (docs/perf-p1a-deflate-mt.md section 4),
// and the reason libdeflate was turned down (docs/perf-p2b-libdeflate.md section 3).
//
// Keep this file pure ASCII, like everything else under native/.

#include "az_deflate_zlibng.h"

#include "CPP/Common/MyCom.h"
#include "CPP/7zip/ICoder.h"

#include "C/Alloc.h"            // MidAlloc / MidFree, the same pair CopyCoder.cpp uses

// Generated from zlib.h.in by the library's own CMake, on our include path through
// the linked target. It wraps itself in extern "C" when read from C++ (zlib.h.in:48).
#include "zlib.h"

#include <cstring>

namespace az {

namespace {

// Level 5 when nobody said: what the vendor's coder does after
// CEncProps::Normalize() (DeflateEncoder.cpp:104-105), not zlib's own default of 6.
// The tier table in Core is what a user sees, and "standard" has meant 5 here.
const UInt32 kDefaultLevel = 5;

// -15 is not a tuning choice: it means "raw deflate, 32 KiB window", which is what
// both a ZIP entry and a 7z Deflate member hold. memLevel 9 is the pairing the
// ladder in docs/perf-zlib-ng.tsv was measured with.
const int kWindowBits = -15;
const int kMemLevel   = 9;

const size_t kInBufferSize  = (size_t)1 << 16;
const size_t kOutBufferSize = (size_t)1 << 16;

// Two allocations, one exit path each. Code() below has more ways out than one,
// so neither buffer may depend on reaching the end of the function.
class CBuf
{
  Byte *_p;
public:
  CBuf(): _p(NULL) {}
  ~CBuf() { if (_p) ::MidFree(_p); }
  bool Alloc(size_t size)
  {
    _p = (Byte *)::MidAlloc(size);
    return _p != NULL;
  }
  Byte *Ptr() const { return _p; }
};

// deflateEnd on every exit path, including the RINOK ones.
class CZStream
{
  z_stream _s;
  bool _inited;
public:
  CZStream(): _inited(false) { memset(&_s, 0, sizeof(_s)); }
  ~CZStream() { if (_inited) deflateEnd(&_s); }
  bool Init(UInt32 level)
  {
    _inited = (deflateInit2(&_s, (int)level, Z_DEFLATED, kWindowBits,
        kMemLevel, Z_DEFAULT_STRATEGY) == Z_OK);
    return _inited;
  }
  z_stream &operator()() { return _s; }
};

}

// The type stays in this file; the registry only ever needs CreateAzDeflateEncoder.
Z7_CLASS_IMP_COM_2(
  CEncoder
  , ICompressCoder
  , ICompressSetCoderProperties
)
  UInt32 _level;
public:
  CEncoder(): _level(kDefaultLevel) {}
};

Z7_COM7F_IMF(CEncoder::SetCoderProperties(const PROPID *propIDs, const PROPVARIANT *coderProps, UInt32 numProps))
{
  for (UInt32 i = 0; i < numProps; i++)
  {
    const PROPID propID = propIDs[i];
    // Stream-size hints: the vendor skips everything from kReduceSize up without
    // inspecting it, and a coder must not fail on those (DeflateEncoder.cpp:226-228).
    if (propID >= NCoderPropID::kReduceSize)
      continue;
    const PROPVARIANT &prop = coderProps[i];
    if (prop.vt != VT_UI4)
      return E_INVALIDARG;
    switch (propID)
    {
      case NCoderPropID::kLevel:
        _level = (prop.ulVal > 9) ? 9 : prop.ulVal;
        break;
      case NCoderPropID::kNumThreads:
        break;                    // zlib-ng has no threaded deflate; "mt" is the vendor's own knob
      default:
        return E_INVALIDARG;      // fb / mc / pass / a / mf belong to the coder this one replaced
    }
  }
  return S_OK;
}

Z7_COM7F_IMF(CEncoder::Code(ISequentialInStream *inStream, ISequentialOutStream *outStream,
    const UInt64 * /* inSize */, const UInt64 * /* outSize */, ICompressProgressInfo *progress))
{
  // inSize is ignored, exactly as the vendor ignores it (DeflateEncoder.cpp:969-970):
  // the stream is finished when a Read returns no bytes, and for an in-memory
  // member the handler may not know a size at all.
  CBuf inBuf, outBuf;
  if (!inBuf.Alloc(kInBufferSize) || !outBuf.Alloc(kOutBufferSize))
    return E_OUTOFMEMORY;

  CZStream zc;
  if (!zc.Init(_level))
    return E_OUTOFMEMORY;
  z_stream &s = zc();

  UInt64 inTotal = 0;
  bool eof = false;

  for (;;)
  {
    if (s.avail_in == 0 && !eof)
    {
      UInt32 processed = 0;
      RINOK(inStream->Read(inBuf.Ptr(), (UInt32)kInBufferSize, &processed))
      if (processed == 0)
        eof = true;                   // a short read is not allowed to end a deflate
                                      // stream early; the next pass feeds what we have
      else
      {
        s.next_in = (Bytef *)inBuf.Ptr();
        s.avail_in = processed;
        inTotal += processed;
        if (progress)
        {
          const UInt64 outTotal = s.total_out;
          RINOK(progress->SetRatioInfo(&inTotal, &outTotal))
        }
      }
    }

    const int flush = eof ? Z_FINISH : Z_NO_FLUSH;

    int res = Z_OK;
    do
    {
      s.next_out = (Bytef *)outBuf.Ptr();
      s.avail_out = (uInt)kOutBufferSize;
      res = deflate(&s, flush);
      const size_t wrote = kOutBufferSize - s.avail_out;
      if (wrote)
        RINOK(outStream->Write(outBuf.Ptr(), (UInt32)wrote, NULL))
      if (res == Z_MEM_ERROR)
        return E_OUTOFMEMORY;
      // Z_STREAM_ERROR means the stream state is wrong (a bug in this loop, not in
      // the data) and Z_BUF_ERROR means no progress was possible with a full output
      // buffer just flushed -- neither may be reported as success.
      if (res == Z_STREAM_ERROR || res == Z_BUF_ERROR)
        return E_FAIL;
    }
    while (s.avail_out == 0);

    if (flush == Z_FINISH)
    {
      // A finished stream is Z_STREAM_END with every input byte consumed. Anything
      // else is a short stream, which would read back as a corrupt member later --
      // and a corrupt member is the one failure mode this engine must not write.
      if (res != Z_STREAM_END || s.avail_in != 0)
        return E_FAIL;
      break;
    }
  }

  return S_OK;
}

void *CreateAzDeflateEncoder()
{
  return (void *)(ICompressCoder *)new CEncoder;
}

}
