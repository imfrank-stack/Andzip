/*
 * Spike for the question P2-b left open: the engine's own Deflate coder is slow, and
 * libdeflate proved that a fast coder exists (docs/perf-libdeflate.tsv) -- but libdeflate
 * has no streaming compression, so it cannot be the coder a ZIP entry needs. This measures
 * a library that DOES have the shape P1-a named as the selection criterion (Z_SYNC_FLUSH,
 * deflateSetDictionary): zlib-ng.
 *
 * Two shapes, because only one of them is what a coder can actually do:
 *
 *   finish - the whole corpus handed to one deflate(Z_FINISH) call. Comparable with the
 *            libdeflate rows, which can only be taken this way.
 *   stream - 64 KiB fed with Z_NO_FLUSH, one continuous deflate stream to the end. This is
 *            the shape the vendor's coder uses too, so this column is the one a product
 *            decision should be read from.
 *
 * Levels 1..9 line up with az_create_options.level; level 6 is round-tripped through
 * zlib-ng's own inflate and memcmp'd, so the ratio column is not measuring garbage.
 *
 * Output is one TSV row per cell, same first columns as docs/perf-*.tsv.
 *
 * Keep this file pure ASCII, like everything else under native/.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "zlib.h"

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

/* whole-buffer: one deflate() call with Z_FINISH */
static size_t do_finish(int level, const unsigned char *in, size_t in_len,
                        unsigned char *out, size_t out_cap, double *secs)
{
    z_stream s;
    size_t written = 0;
    int runs, best_set = 0;
    for (runs = 0; runs < 2; runs++) {
        double t0;
        int r;
        memset(&s, 0, sizeof(s));
        if (deflateInit2(&s, level, Z_DEFLATED, -15, 9, Z_DEFAULT_STRATEGY) != Z_OK)
            { fprintf(stderr, "deflateInit2 failed\n"); exit(1); }
        t0 = now_sec();
        s.next_in = (Bytef *) in; s.avail_in = (uInt) in_len;
        s.next_out = out; s.avail_out = (uInt) out_cap;
        r = deflate(&s, Z_FINISH);
        written = s.total_out;
        if (r != Z_STREAM_END) { fprintf(stderr, "did not finish (buffer too small?)\n"); exit(1); }
        deflateEnd(&s);
        {
            double el = now_sec() - t0;
            if (!best_set || el < *secs) { *secs = el; best_set = 1; }
        }
    }
    return written;
}

/* streaming: 64 KiB at a time, Z_NO_FLUSH in the middle, Z_FINISH last */
static size_t do_stream(int level, const unsigned char *in, size_t in_len,
                        unsigned char *out, size_t out_cap, double *secs)
{
    const size_t CHUNK = 64 * 1024;
    size_t written = 0;
    int runs, best_set = 0;
    for (runs = 0; runs < 2; runs++) {
        z_stream s;
        double t0;
        size_t off = 0;
        int done = 0;
        memset(&s, 0, sizeof(s));
        if (deflateInit2(&s, level, Z_DEFLATED, -15, 9, Z_DEFAULT_STRATEGY) != Z_OK)
            { fprintf(stderr, "deflateInit2 failed\n"); exit(1); }
        t0 = now_sec();
        s.next_out = out; s.avail_out = (uInt) out_cap;
        while (!done) {
            size_t n = in_len - off < CHUNK ? in_len - off : CHUNK;
            int flush;
            s.next_in = (Bytef *) (in + off); s.avail_in = (uInt) n;
            if (n == 0) flush = Z_FINISH;
            else { flush = (off + n >= in_len) ? Z_FINISH : Z_NO_FLUSH; }
            if (deflate(&s, flush) == Z_BUF_ERROR) { fprintf(stderr, "buf error\n"); exit(1); }
            off += n;
            if (flush == Z_FINISH && s.avail_in == 0) done = 1;
        }
        written = s.total_out;
        deflateEnd(&s);
        {
            double el = now_sec() - t0;
            if (!best_set || el < *secs) { *secs = el; best_set = 1; }
        }
    }
    return written;
}

static void row(const char *shape, const char *corpus, size_t in_bytes,
                int level, double seconds, size_t out_bytes, int rt_ok)
{
    double mib = (double) in_bytes / (1024.0 * 1024.0);
    printf("zlib-ng\t%s\t%s\t%d\t-\t%.2f\t%.1f\t%llu\t%.5f\t%d\n",
           shape, corpus, level, seconds, mib / seconds,
           (unsigned long long) out_bytes,
           (double) out_bytes / (double) in_bytes, rt_ok);
}

int main(int argc, char **argv)
{
    const char *path = (argc > 1) ? argv[1] : NULL;
    const char *corpus = (argc > 2) ? argv[2] : "corpus";
    size_t in_len = 0, bound, cap;
    unsigned char *in, *out;
    int level;

    if (!path) { fprintf(stderr, "usage: zlibng_spike <file> [corpus-name]\n"); return 2; }
    in = read_all(path, &in_len);
    if (!in) { fprintf(stderr, "cannot read %s\n", path); return 2; }

    bound = deflateBound((z_stream *) NULL, (uLong) in_len);
    if (!bound) { fprintf(stderr, "deflateBound needs a stream\n"); return 1; }
    cap = bound + 1024;
    out = (unsigned char *) malloc(cap);
    if (!out) { fprintf(stderr, "oom\n"); return 1; }

    printf("shape\tcorpus\tlevel\tchunk_mib\tseconds\tmb_per_s\tout_bytes\tratio\troundtrip\n");
    printf("zlibng-version\t%s\n", zlibVersion());

    for (level = 1; level <= 9; level++) {
        double secs = 0;
        size_t n;
        int rt = 0;
        n = do_finish(level, in, in_len, out, cap, &secs);
        if (level == 6) {
            /* raw deflate has no container, so uncompress() (which wants zlib format)
             * cannot read it back; inflateInit2 with -15 is the matching window. */
            unsigned char *back = (unsigned char *) malloc(in_len);
            z_stream d;
            memset(&d, 0, sizeof(d));
            if (back && inflateInit2(&d, -15) == Z_OK) {
                int ir;
                d.next_in = (Bytef *) out; d.avail_in = (uInt) n;
                d.next_out = back; d.avail_out = (uInt) in_len;
                ir = inflate(&d, Z_FINISH);
                rt = (ir == Z_STREAM_END && d.total_out == in_len
                      && memcmp(back, in, in_len) == 0);
                inflateEnd(&d);
            }
            free(back);
        }
        row("finish", corpus, in_len, level, secs, n, rt);
        secs = 0;
        n = do_stream(level, in, in_len, out, cap, &secs);
        row("stream", corpus, in_len, level, secs, n, 0);
    }

    free(in);
    free(out);
    return 0;
}
