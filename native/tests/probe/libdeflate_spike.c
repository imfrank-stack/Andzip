/*
 * Spike for stage P2-b (section 6.2 of the compression-speedup plan doc): does libdeflate
 * beat the engine's own Deflate coder by the 2x the veto gate demands, and does it survive
 * the ratio limit?
 *
 * Nothing here is shipped. This compiles against a libdeflate checkout that lives outside
 * the repository (native/tests/probe/build-and-run-libdeflate.ps1 clones it into build/,
 * which is gitignored) and calls only the public API. Two shapes are measured because they
 * are not the same question:
 *
 *   whole  - one libdeflate_deflate_compress() call over the whole corpus. This is what
 *            the API actually offers: there is no streaming entry point, so a coder built
 *            on it holds the entire entry in memory.
 *   chunk  - the corpus cut into N-MiB blocks, each compressed as its own complete deflate
 *            stream. This is the only way a bigger-than-RAM entry could ever be written,
 *            and it is also what has to be byte-surgery-spliced into one stream (see
 *            docs/perf-p1a-deflate-mt.md, option B). The ratio column is what that shape
 *            costs, before any splicing code exists.
 *
 * Output is one TSV row per cell on stdout, with the same first columns as docs/perf-*.tsv
 * so a reader can put them next to the engine rows.
 *
 * Keep this file pure ASCII, like everything else under native/.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "libdeflate.h"

static double now_sec(void)
{
    LARGE_INTEGER c, f;
    QueryPerformanceCounter(&c);
    QueryPerformanceFrequency(&f);
    return (double) c.QuadPart / (double) f.QuadPart;
}

static unsigned char *read_all(const char *path, size_t *out_len)
{
    FILE *f = fopen(path, "rb");
    unsigned char *buf;
    size_t len, got;
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    len = (size_t) ftell(f);
    fseek(f, 0, SEEK_SET);
    buf = (unsigned char *) malloc(len ? len : 1);
    if (!buf) { fclose(f); return NULL; }
    got = fread(buf, 1, len, f);
    fclose(f);
    if (got != len) { free(buf); return NULL; }
    *out_len = len;
    return buf;
}

/* one row: sweep shape corpus_mib level chunk_mib seconds out_bytes */
static void row(const char *shape, const char *corpus, size_t in_bytes,
                int level, double chunk_mib, double seconds, size_t out_bytes, int rt_ok)
{
    double mib = (double) in_bytes / (1024.0 * 1024.0);
    printf("libdeflate\t%s\t%s\t%d\t%.0f\t%.2f\t%.1f\t%llu\t%.5f\t%d\n",
           shape, corpus, level, chunk_mib,
           seconds, mib / seconds,
           (unsigned long long) out_bytes,
           (double) out_bytes / (double) in_bytes, rt_ok);
}

int main(int argc, char **argv)
{
    const char *path = (argc > 1) ? argv[1] : NULL;
    const char *corpus = (argc > 2) ? argv[2] : "corpus";
    size_t in_len = 0, bound = 0, cap = 0;
    unsigned char *in, *out = NULL;
    int level;
    static const size_t chunks[] = { 1, 8, 64 };

    if (!path) {
        fprintf(stderr, "usage: libdeflate_spike <file> [corpus-name]\n");
        return 2;
    }
    in = read_all(path, &in_len);
    if (!in) { fprintf(stderr, "cannot read %s\n", path); return 2; }

    printf("shape\tcorpus\tlevel\tchunk_mib\tinput_mib\tseconds\tmb_per_s\tout_bytes\tratio\troundtrip\n");

    /* whole-buffer shape: the only thing the public API can do in one call */
    for (level = 1; level <= 12; level++) {
        struct libdeflate_compressor *c = libdeflate_alloc_compressor(level);
        double best = 1e30;
        size_t out_len = 0;
        int run, rt = 0;
        if (!c) { fprintf(stderr, "alloc failed at level %d\n", level); return 1; }
        bound = libdeflate_deflate_compress_bound(c, in_len);
        if (bound > cap) { free(out); out = (unsigned char *) malloc(bound); cap = bound;
                           if (!out) { fprintf(stderr, "oom\n"); return 1; } }
        for (run = 0; run < 2; run++) {
            double t0 = now_sec();
            out_len = libdeflate_deflate_compress(c, in, in_len, out, bound);
            if (now_sec() - t0 < best) best = now_sec() - t0;
            if (!out_len) { fprintf(stderr, "level %d did not fit the bound\n", level); return 1; }
        }
        if (level == 6) {   /* one round-trip per sweep is enough to prove the bytes are real */
            struct libdeflate_decompressor *d = libdeflate_alloc_decompressor();
            unsigned char *back = (unsigned char *) malloc(in_len);
            size_t used = 0;
            enum libdeflate_result r = d ? libdeflate_deflate_decompress(d, out, out_len,
                                                        back, in_len, &used)
                                         : (enum libdeflate_result) -1;
            rt = (r == LIBDEFLATE_SUCCESS && used == in_len && memcmp(back, in, in_len) == 0);
            free(back);
            if (d) libdeflate_free_decompressor(d);
        }
        row("whole", corpus, in_len, level, 0, best, out_len, rt);
        libdeflate_free_compressor(c);
    }

    /* chunked shape: what a streaming coder would really have to do */
    {
        static const int pick[] = { 1, 5, 6, 9, 12 };
        size_t ci, pi;
        for (ci = 0; ci < sizeof(chunks) / sizeof(chunks[0]); ci++) {
            size_t chunk = chunks[ci] * 1024 * 1024;
            for (pi = 0; pi < sizeof(pick) / sizeof(pick[0]); pi++) {
                struct libdeflate_compressor *c;
                size_t off, total = 0;
                double t0, best = 1e30;
                int run;
                level = pick[pi];
                c = libdeflate_alloc_compressor(level);
                if (!c) { fprintf(stderr, "alloc failed\n"); return 1; }
                bound = libdeflate_deflate_compress_bound(c, chunk);
                if (bound > cap) { free(out); out = (unsigned char *) malloc(bound); cap = bound;
                                   if (!out) { fprintf(stderr, "oom\n"); return 1; } }
                for (run = 0; run < 2; run++) {
                    off = 0; total = 0;
                    t0 = now_sec();
                    while (off < in_len) {
                        size_t n = in_len - off < chunk ? in_len - off : chunk;
                        size_t r = libdeflate_deflate_compress(c, in + off, n, out, bound);
                        if (!r) { fprintf(stderr, "chunk did not fit\n"); return 1; }
                        total += r;
                        off += n;
                    }
                    if (now_sec() - t0 < best) best = now_sec() - t0;
                }
                row("chunk", corpus, in_len, level, (double) chunks[ci], best, total, 0);
                libdeflate_free_compressor(c);
            }
        }
    }

    free(in);
    free(out);
    return 0;
}
