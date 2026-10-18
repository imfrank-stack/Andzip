// az_deflate_swap.cpp -- point the Deflate slot's encoder at zlib-ng, in place.
//
// The shape comes straight out of P2-a's third experiment (docs/perf-p2a-registry.md
// section 3): the codec table is an append-only array whose entries carry one
// function pointer per direction, and CreateCoder.cpp keeps g_NumCodecs / g_Codecs
// as ordinary globals (CreateCoder.cpp:14-19) rather than file-static ones, which
// is the only reason a translation unit outside third_party/ can touch them at all.
//
// Why not the compile switch (-DZ7_DEFLATE_EXTRACT_ONLY), which P2-a recommended
// before it was run: the same define strips Deflate64's encoder too
// (Deflate64Register.cpp:8,17), and Deflate64 writes real ZIP members today
// (docs/perf-zip-methods.tsv: that row is 1329 bytes, not a rejection). Trading a
// feature that works for one that is merely faster is not the deal.
//
// What "found the slot" means: the entry's Id and a non-NULL CreateDecoder, never
// its index. P2-a measured the vendor's Deflate at position 14 in the shared build
// and 15 in the static one -- same sources, different order -- so an index check
// would be a claim about the linker.
//
// Keep this file pure ASCII, like everything else under native/.

#include "az_deflate_zlibng.h"

#include <atomic>
#include <mutex>

// Defined in the vendor's CreateCoder.cpp; declared here exactly as that file
// declares them to itself (extern on the definition line, non-static storage).
extern unsigned g_NumCodecs;
extern const CCodecInfo *g_Codecs[];

namespace az {

namespace {

// Static storage, on purpose: the table keeps this address for the rest of the
// process, so nothing that lives on a stack or in a per-call object will do.
CCodecInfo g_swappedEntry;

bool DoSwap()
{
  for (unsigned i = 0; i < g_NumCodecs; i++)
  {
    const CCodecInfo *entry = g_Codecs[i];
    if (!entry || entry->Id != kAzMethod_Deflate || !entry->CreateDecoder)
      continue;

    g_swappedEntry.CreateDecoder = entry->CreateDecoder;   // decoding stays upstream's
    g_swappedEntry.CreateEncoder = CreateAzDeflateEncoder;
    g_swappedEntry.Id            = entry->Id;
    g_swappedEntry.Name          = entry->Name;            // still "Deflate" to the ABI:
                                                           // az_codec_get and Core's method
                                                           // table read this string
    g_swappedEntry.NumStreams    = entry->NumStreams;
    g_swappedEntry.IsFilter      = entry->IsFilter;

    // The replacement has to be completely written before another thread can reach
    // it through the table. A plain store would let the pointer publish first.
    std::atomic_thread_fence(std::memory_order_release);
    g_Codecs[i] = &g_swappedEntry;
    return true;
  }
  return false;
}

std::atomic<bool> g_swapped(false);

}

bool AzDeflateSwapInstall()
{
  // One time, any thread. Compression can run on several threads at once in this
  // engine, so "the first writer does it" has to mean something.
  static std::once_flag once;
  std::call_once(once, []
  {
    g_swapped.store(DoSwap());
  });
  return g_swapped.load();
}

}
