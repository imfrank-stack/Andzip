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

// What the engine's level (the number Core's tier table sends as "x") means to zlib-ng.
// The tiers are not "one zlib level each": the measured curve is flat in places and
// turns back on itself at the top, so the tiers are pinned to the points that keep the
// promise the UI makes -- "a later tier is never a bigger package" -- on the corpus the
// tier table is measured on. The rows below are the product's own bytes: a 128 MiB
// "mixed" member and a "text" one, ZIP container, min of two runs (docs/perf-tiers.tsv,
// sweep defladder; each 7z twin is a couple of bytes smaller -- 30 on "mixed", 28 on
// "text" -- the same stream under a different header, which is itself worth reading
// in docs/perf-p2c-deflate-swap.md):
//
//   sent  zlib-ng   mixed bytes    text bytes   MB/s   the tier
//   1     2         40 421 017       212 918     356   UltraFast
//   3     2         40 421 017       212 918     366   Fast -- the same package as
//                                                       UltraFast, exactly as before,
//                                                       so section 6 item 27 stays true
//   5     6         39 783 086       201 782     233   Normal
//   9     8         39 768 264       212 908     121   Max: the smallest of all ten
//                                                       levels on "mixed"
//
// All four are smaller *and* faster than what 5.6.0 ships at the same tier: UltraFast
// 40 972 439 / 259 346 at 162 MB/s, Normal 40 710 824 / 263 318 at 25.3, Max with a=0
// 39 983 091 / 259 346 at 18.4.
//
// Two things this table does not do. It does not hide that level 8 is worse than level 6
// on "text" (212 908 against 201 782): that inversion is on the record, and 5.6.0's Max
// tier is inverted on the same corpus too (259 346 against Normal's 263 318). And it does
// not invent numbers for levels no UI sends -- 2/4/6/7 pass through to zlib-ng unchanged,
// and 0 is store.
UInt32 MapTierLevelToZlib(UInt32 level)
{
  switch (level)
  {
    case 1: case 2: case 3: return 2;
    case 5:                 return 6;
    case 9:                 return 8;
    default:                return level;
  }
}

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
  if (!zc.Init(MapTierLevelToZlib(_level)))
    return E_OUTOFMEMORY;
  z_stream &s = zc();

  UInt64 inTotal = 0;
  bool eof = false;

  for (;;)
  {
    if (s.avail_in == 0 && !eof)
    {
      // Fill the buffer before handing anything to deflate(): one Read() call is
      // allowed to return less than asked, and how much it returns depends on what
      // is above us -- the ZIP path hands over a file stream, the 7z path a
      // solid-block stream, and they do not slice the same file the same way.
      // deflate() must not be able to tell the difference: "same level, same bytes"
      // is the property the tier table and tests/perf/tier-check.cs both rely on.
      size_t filled = 0;
      while (filled < kInBufferSize)
      {
        UInt32 processed = 0;
        RINOK(inStream->Read(inBuf.Ptr() + filled, (UInt32)(kInBufferSize - filled), &processed))
        if (processed == 0)
        {
          eof = true;      // a short read is not an end of stream; only a zero one is
          break;
        }
        filled += processed;
      }
      if (filled)
      {
        s.next_in = (Bytef *)inBuf.Ptr();
        s.avail_in = (uInt)filled;
        inTotal += filled;
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
      {
        // Write() reports in *processedSize how much it actually took, and a stream
        // may take less than asked; upstream's COutBuffer::FlushPart reads that
        // number and loops (OutBuffer.cpp:69-72), which is why a coder of 1 MiB
        // buffers never loses a byte through it. Passing NULL here was the bug: a
        // ZIP write that took part of a chunk silently dropped the rest, and the
        // member landed 16 472 bytes shorter than deflate had emitted -- it decoded
        // 3.47 MiB of an 8 MiB file and died with "data error". A stream that takes
        // nothing is a hang waiting to happen, so that is an error, not a retry.
        const Byte *p = outBuf.Ptr();
        size_t left = wrote;
        while (left)
        {
          UInt32 processedOut = 0;
          RINOK(outStream->Write(p, (UInt32)left, &processedOut))
          if (processedOut == 0 || processedOut > left)
            return E_FAIL;
          p += processedOut;
          left -= processedOut;
        }
      }
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
