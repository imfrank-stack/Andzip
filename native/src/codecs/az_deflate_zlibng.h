// az_deflate_zlibng.h -- the Deflate encoder that comes from zlib-ng, and the one
// place that puts it into 7-Zip's codec table.
//
// Why this exists (stage P2-c, docs/perf-p2b-libdeflate.md section 4): the engine's
// own Deflate coder is slow -- 25.3 MB/s at level 5 on 128 MiB of the "mixed"
// corpus while zlib-ng does 346 MB/s for a *smaller* stream (docs/perf-zlib-ng.tsv).
// The vendor's coder cannot simply be given a flush entry point: docs/perf-p1a-deflate-mt.md
// read it and found there is none (one-shot Code(), no SYNC_FLUSH, no preset
// dictionary), which is also why block-parallel deflate was left undone. zlib-ng
// has all three, so it is the library that can carry both the speed and, later,
// the parallelism.
//
// How it gets used (docs/perf-p2a-registry.md section 3, third experiment): the
// registry is an append-only array (CreateCoder.cpp:14-19 -- g_NumCodecs and
// g_Codecs are non-static, which is what makes this legal from outside the vendor
// tree), and the encoder lookup filters by direction (CreateCoder.cpp:211-213).
// Two shapes win the encoder direction for sure: the compile switch
// Z7_DEFLATE_EXTRACT_ONLY, or rewriting the table slot in place. The switch was
// rejected because the same define also strips Deflate64's encoder, and Deflate64
// writes real files today (docs/perf-zip-methods.tsv). So: replace the slot, hand
// the decoder direction straight back to the vendor's own create function, keep
// the entry's Name/NumStreams/IsFilter identical.
//
// One id covers two containers. ZIP asks for kMethodId_ZipBase + 8 (ZipAddCommon.cpp:382,
// ZipCompressionMode.h:17) and 7z spells the same number k_Deflate (7zHeader.h:113),
// so swapping it changes Deflate in both -- which is why the verification has to
// cover both, not just the one that was the reason for the change.

#pragma once

#include "CPP/7zip/Common/RegisterCodec.h"

namespace az {

// 0x40108: ZIP's Deflate method id, and 7z's k_Deflate. Same number, one slot.
const CMethodId kAzMethod_Deflate = 0x40108;

// CCodecInfo::CreateEncoder's exact signature: CreateCodecP is "void * (*)()"
// (RegisterCodec.h:11), and REGISTER_CODEC_CREATE expands to a cast of a new
// object to the interface pointer (RegisterCodec.h:27-28).
void *CreateAzDeflateEncoder();

// Point the Deflate slot's encoder at CreateAzDeflateEncoder and keep everything
// else about the entry. Idempotent and safe to call from any thread; the return
// value is false only when the vendor's entry was not in the table at all, which
// would mean the vendor tree changed under us -- callers must not treat that as
// "fine, we just use the old coder", because then the bytes a user gets are not
// the bytes we measured.
bool AzDeflateSwapInstall();

}
