/*
 * andzip.h -- public C ABI of libandzip_core.
 *
 * This is the only header a consumer needs. It is deliberately flat C99: no
 * C++ types, no exceptions, no callbacks that can throw across the boundary,
 * and every struct carries a `struct_size` field so new members can be added
 * without breaking already-compiled callers.
 *
 * Conventions
 *   - All strings are NUL-terminated UTF-8. Never free a `const char *` that
 *     came out of this library; it stays valid until the owning handle is
 *     closed (or, for az_entry_info / az_format_info, until az_*_release).
 *   - Handles are opaque pointers. Pass NULL where an optional handle or
 *     options pointer is accepted.
 *   - Every function returns az_result. AZ_OK means success. On failure call
 *     az_last_error_message() for a human readable reason.
 *   - The library is thread safe as long as a single handle is only used from
 *     one thread at a time.
 *
 * Linkage
 *   Windows : andzip_core.dll   (import lib andzip_core.lib)
 *   Linux   : libandzip_core.so
 *   macOS   : libandzip_core.dylib
 */

#ifndef ANDZIP_ANDZIP_H
#define ANDZIP_ANDZIP_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(ANDZIP_CORE_BUILDING) && !defined(ANDZIP_CORE_STATIC)
#    define AZ_API __declspec(dllexport)
#  elif defined(ANDZIP_CORE_STATIC)
    /*
     * A static andzip_core has no export table to fill: both the library's own
     * definitions and its consumers want plain external linkage. Getting this
     * wrong is not cosmetic -- __declspec(dllimport) on a definition is
     * error C2491 ("cannot define an imported function").
     */
#    define AZ_API
#  else
#    define AZ_API __declspec(dllimport)
#  endif
#  define AZ_CALL __cdecl
#elif defined(__GNUC__) && __GNUC__ >= 4
#  define AZ_API __attribute__((visibility("default")))
#  define AZ_CALL
#else
#  define AZ_API
#  define AZ_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * ABI 2: az_create_options grew two trailing fields (preallocate, share_locked).
 * Every options struct carries its own sizeof, so an old caller is rejected by
 * AZ_ERROR_INVALID_ARG rather than mis-parsed -- but the number still has to move,
 * because the shells assert it.
 */
#define AZ_ABI_VERSION 2

/* ------------------------------------------------------------------------- */
/* Result codes                                                              */
/* ------------------------------------------------------------------------- */

typedef enum az_result {
  AZ_OK                 = 0,
  AZ_ERROR_GENERIC      = 1,   /* engine reported a failure; see az_last_error_message */
  AZ_ERROR_INVALID_ARG  = 2,
  AZ_ERROR_NOT_FOUND    = 3,   /* file or archive entry does not exist */
  AZ_ERROR_IO           = 4,   /* read/write failure */
  AZ_ERROR_OUT_OF_MEMORY= 5,
  AZ_ERROR_UNSUPPORTED  = 6,   /* format or method not supported by this build */
  AZ_ERROR_BAD_FORMAT   = 7,   /* not an archive, or corrupt */
  AZ_ERROR_PASSWORD     = 8,   /* missing or wrong password */
  AZ_ERROR_CRC          = 9,   /* data integrity check failed */
  AZ_ERROR_CANCELLED    = 10,  /* a callback asked us to stop */
  AZ_ERROR_ALREADY_DONE = 11,  /* handle already finished/closed */
  AZ_ERROR_BUFFER_SMALL = 12,
  AZ_ERROR_INTERNAL     = 13
} az_result;

/* ------------------------------------------------------------------------- */
/* Opaque handles                                                            */
/* ------------------------------------------------------------------------- */

typedef struct az_archive_s *az_archive;  /* an opened archive (read side)   */
typedef struct az_writer_s  *az_writer;   /* an archive under construction   */

/* ------------------------------------------------------------------------- */
/* Callbacks                                                                 */
/* ------------------------------------------------------------------------- */

typedef enum az_op {
  AZ_OP_SCAN    = 0,  /* enumerating input */
  AZ_OP_COMPRESS= 1,
  AZ_OP_EXTRACT = 2,
  AZ_OP_TEST    = 3,
  AZ_OP_HASH    = 4
} az_op;

typedef struct az_progress {
  az_op       op;
  const char *current_entry;  /* UTF-8, may be NULL; valid only during the call */
  uint64_t    entry_index;    /* index within the operation, 0-based */
  uint64_t    entry_count;    /* 0 when unknown */
  /*
   * The engine reports one byte counter per operation through IProgress. For
   * AZ_OP_EXTRACT / AZ_OP_TEST that counter is packed bytes read; for
   * AZ_OP_COMPRESS it is source bytes consumed. It surfaces as bytes_in /
   * total_in. The _out pair is only filled when both directions are known.
   */
  uint64_t    bytes_in;
  uint64_t    total_in;
  uint64_t    bytes_out;
  uint64_t    total_out;
} az_progress;

/*
 * Return AZ_OK to continue. Return any other az_result to abort the operation;
 * that code is then what the top-level call reports (typically AZ_ERROR_CANCELLED).
 */
typedef az_result (*az_progress_fn)(void *ctx, const az_progress *progress);

/*
 * Called when an encrypted archive needs a password and none was supplied in
 * az_open_options. Copy a NUL-terminated UTF-8 password into `buf` (at most
 * `capacity - 1` bytes) and return 1. Return 0 to give up, which surfaces as
 * AZ_ERROR_PASSWORD.
 */
typedef int (*az_password_fn)(void *ctx, char *buf, size_t capacity);

typedef struct az_callbacks {
  uint32_t        struct_size;  /* = sizeof(az_callbacks) */
  az_progress_fn  progress;     /* may be NULL */
  az_password_fn  password;     /* may be NULL */
  void           *ctx;          /* passed through unchanged */
} az_callbacks;

#define AZ_CALLBACKS_INIT { sizeof(az_callbacks), NULL, NULL, NULL }

/* ------------------------------------------------------------------------- */
/* Global state                                                              */
/* ------------------------------------------------------------------------- */

typedef struct az_build_info {
  uint32_t    struct_size;   /* = sizeof(az_build_info) */
  uint32_t    abi_version;   /* AZ_ABI_VERSION */
  const char *core_version;  /* e.g. "1.0.0" */
  const char *engine;        /* e.g. "7-Zip 25.01 (x64)" */
  const char *build_config;  /* "Debug" / "Release" / "RelWithDebInfo" */
  uint32_t    format_count;
  uint32_t    codec_count;
  uint32_t    has_zstd_encoder;
  uint32_t    has_lz4_encoder;
  uint32_t    has_brotli_encoder;
} az_build_info;

AZ_API uint32_t    AZ_CALL az_abi_version(void);
AZ_API az_result   AZ_CALL az_get_build_info(az_build_info *out);

/*
 * Per-thread description of the most recent failure. The returned pointer is
 * valid until the next az_* call on the same thread. Never NULL.
 */
AZ_API const char *AZ_CALL az_last_error_message(void);

/*
 * Worker threads used by multi-threaded coders (LZMA2, BZip2, XZ, Zstd).
 * 0 means "one per hardware thread". Applies to operations started afterwards.
 */
AZ_API void     AZ_CALL az_set_num_threads(uint32_t threads);
AZ_API uint32_t AZ_CALL az_get_num_threads(void);

/* Upper bound, in MiB, that a decoder may allocate. 0 disables the limit. */
AZ_API void     AZ_CALL az_set_memory_limit_mib(uint64_t mib);
AZ_API uint64_t AZ_CALL az_get_memory_limit_mib(void);

/* ------------------------------------------------------------------------- */
/* Formats and codecs                                                        */
/* ------------------------------------------------------------------------- */

typedef struct az_format_info {
  uint32_t    struct_size;   /* = sizeof(az_format_info) */
  uint32_t    index;         /* engine format index, pass back to az_*_by_index */
  const char *name;          /* canonical name used by az_create_options.format */
  const char *extension;     /* primary extension, no dot; may be NULL */
  const char *add_extension; /* extra extension for split volumes; may be NULL */
  int32_t     can_read;
  int32_t     can_write;
  uint32_t    flags;
  const char *signatures;    /* raw signature bytes, see signatures_size */
  uint32_t    signatures_size;
  void       *_bag;          /* internal; pass back to az_format_release */
} az_format_info;

typedef struct az_codec_info {
  uint32_t    struct_size;   /* = sizeof(az_codec_info) */
  uint32_t    index;
  const char *name;          /* e.g. "LZMA2", "Deflate", "Zstd" */
  const char *id_hex;        /* method id as lower case hex, e.g. "21" */
  int32_t     can_encode;
  int32_t     can_decode;
  int32_t     is_filter;     /* BCJ/ARM/Delta style preprocessor */
  void       *_bag;          /* internal; pass back to az_codec_release */
} az_codec_info;

AZ_API uint32_t  AZ_CALL az_format_count(void);
AZ_API az_result AZ_CALL az_format_get(uint32_t index, az_format_info *out);
AZ_API void      AZ_CALL az_format_release(az_format_info *info);

/* Case-insensitive lookup by canonical name ("7z") or extension ("zip"). */
AZ_API az_result AZ_CALL az_format_find(const char *name_or_extension, uint32_t *out_index);

AZ_API uint32_t  AZ_CALL az_codec_count(void);
AZ_API az_result AZ_CALL az_codec_get(uint32_t index, az_codec_info *out);
AZ_API void      AZ_CALL az_codec_release(az_codec_info *info);

/* ------------------------------------------------------------------------- */
/* Opening and inspecting archives                                           */
/* ------------------------------------------------------------------------- */

typedef struct az_open_options {
  uint32_t             struct_size;  /* = sizeof(az_open_options) */
  const char          *password;     /* UTF-8 or NULL */
  const char          *forced_format;/* NULL = auto-detect from content, else canonical name */
  const az_callbacks  *callbacks;    /* may be NULL */
  int32_t              headers_only; /* 1 = do not parse the whole file for sizes (faster) */
} az_open_options;

#define AZ_OPEN_OPTIONS_INIT { sizeof(az_open_options), NULL, NULL, NULL, 0 }

AZ_API az_result AZ_CALL az_archive_open(const char *path_utf8,
                                         const az_open_options *opts, /* NULL = defaults */
                                         az_archive *out);

AZ_API az_result AZ_CALL az_archive_open_memory(const void *data,
                                                size_t size,
                                                const char *name_hint_utf8, /* NULL ok; used for detection */
                                                const az_open_options *opts,
                                                az_archive *out);

AZ_API void AZ_CALL az_archive_close(az_archive archive);

/* Detected format name (canonical, e.g. "zip"). Valid until the handle closes. */
AZ_API const char *AZ_CALL az_archive_format_name(az_archive archive);
AZ_API az_result    AZ_CALL az_archive_entry_count(az_archive archive, uint64_t *out);

/* Whole-archive properties. Any out pointer may be NULL. */
AZ_API az_result AZ_CALL az_archive_comment(az_archive archive, const char **out_utf8);
AZ_API az_result AZ_CALL az_archive_physical_size(az_archive archive, uint64_t *out);
AZ_API az_result AZ_CALL az_archive_is_encrypted(az_archive archive, int32_t *out);
AZ_API az_result AZ_CALL az_archive_is_solid(az_archive archive, int32_t *out);
AZ_API az_result AZ_CALL az_archive_is_volume(az_archive archive, int32_t *out);

typedef struct az_entry_info {
  uint32_t    struct_size;   /* = sizeof(az_entry_info) */
  uint64_t    index;
  const char *name;          /* UTF-8, '/' separators */
  const char *method;        /* e.g. "LZMA2:24", "Deflate", "Copy"; may be NULL */
  uint64_t    size;          /* uncompressed; UINT64_MAX when unknown */
  uint64_t    packed_size;   /* UINT64_MAX when unknown */
  uint32_t    crc;
  int32_t     is_dir;
  int32_t     is_encrypted;
  int32_t     is_anti;       /* 7z anti-item used to mask a file in a solid base */
  int32_t     has_attributes;
  uint32_t    attributes;    /* Win32 attributes when the format stores them */
  int64_t     mtime_unix;    /* seconds since epoch, 0 when not stored */
  int64_t     ctime_unix;
  int64_t     atime_unix;
  uint32_t    posix_mode;    /* 0 when not stored */
  const char *comment;       /* per-entry comment; may be NULL */
  uint64_t    num_sub_files; /* for dirs: items nested underneath, 0 if unknown */
  void       *_bag;          /* internal; pass back to az_entry_release */
} az_entry_info;

AZ_API az_result AZ_CALL az_entry_get(az_archive archive, uint64_t index, az_entry_info *out);
AZ_API void      AZ_CALL az_entry_release(az_entry_info *info);

/* ------------------------------------------------------------------------- */
/* Extraction                                                                */
/* ------------------------------------------------------------------------- */

typedef enum az_overwrite {
  AZ_OVERWRITE_ASK        = 0, /* not used by the native layer; treat as ERROR */
  AZ_OVERWRITE_OVERWRITE  = 1,
  AZ_OVERWRITE_SKIP       = 2,
  AZ_OVERWRITE_RENAME     = 3, /* appends ~1, ~2, ... to avoid a clash */
  AZ_OVERWRITE_FAIL       = 4
} az_overwrite;

typedef struct az_extract_options {
  uint32_t             struct_size;  /* = sizeof(az_extract_options) */
  const char          *password;     /* overrides the one given at open, or NULL */
  const az_callbacks  *callbacks;    /* may be NULL */
  az_overwrite         overwrite;    /* default AZ_OVERWRITE_OVERWRITE */
  int32_t              preserve_mtime;
  int32_t              test_only;    /* 1 = decompress and verify CRCs without writing files */
  int32_t              flatten;      /* 1 = write every entry into the destination directory directly */
} az_extract_options;

#define AZ_EXTRACT_OPTIONS_INIT \
  { sizeof(az_extract_options), NULL, NULL, AZ_OVERWRITE_OVERWRITE, 1, 0, 0 }

/* Extract everything, or only the entries listed in `indices` (count == 0 means all). */
AZ_API az_result AZ_CALL az_archive_extract(az_archive archive,
                                            const char *dest_dir_utf8,
                                            const uint64_t *indices,
                                            size_t count,
                                            const az_extract_options *opts /* NULL = defaults */);

/* Decompress a single entry into a caller buffer. */
AZ_API az_result AZ_CALL az_entry_read(az_archive archive,
                                       uint64_t index,
                                       void *buffer,
                                       size_t capacity,
                                       size_t *written_out);

/*
 * Decompress a single entry through a sink callback instead of memory. The
 * sink receives successive chunks; return AZ_OK to continue, anything else to
 * abort. Useful for entries far larger than RAM.
 */
typedef az_result (*az_write_fn)(void *ctx, const void *data, size_t size);

AZ_API az_result AZ_CALL az_entry_read_stream(az_archive archive,
                                              uint64_t index,
                                              az_write_fn sink,
                                              void *sink_ctx);

/* ------------------------------------------------------------------------- */
/* Creating archives                                                         */
/* ------------------------------------------------------------------------- */

typedef enum az_encryption {
  AZ_ENC_NONE      = 0,
  AZ_ENC_DEFAULT   = 1,  /* AES-256 for 7z, AES-256 (WinZip AE) for zip */
  AZ_ENC_ZIPCRYPTO = 2,  /* zip only; weak, kept for compatibility */
  AZ_ENC_AES256    = 3,  /* zip: WinZip AE-2, 7z: AES-256 + SHA-256 KDF */
  AZ_ENC_7ZAES     = 4   /* 7z only */
} az_encryption;

typedef struct az_create_options {
  uint32_t             struct_size;  /* = sizeof(az_create_options) */
  const char          *format;       /* canonical format name: "7z","zip","tar","gz","xz","bzip2","zstd" */
  const char          *method;       /* "LZMA2","LZMA","Deflate","Zstd","PPMd","BZip2","Copy","LZ4",... NULL = format default */
  int32_t              level;        /* 0..9, or -1 for the format default */
  uint32_t             dict_size_mib;/* 0 = default for the chosen level */
  uint32_t             word_size;    /* PPMd order (2..64) or LZMA lc (0..4); 0 = default */
  uint32_t             num_threads;  /* 0 = az_get_num_threads() */
  uint64_t             memory_limit_mib; /* 0 = az_get_memory_limit_mib() */
  const char          *password;     /* UTF-8 or NULL */
  az_encryption        encryption;
  int32_t              encrypt_headers;  /* 7z: also encrypt the file list */
  int32_t              solid;            /* 7z: one solid block (default 1) */
  int32_t              multi_thread;     /* 1 = allow MT coders (default 1) */
  const char          *volume_size;   /* "100M", "700m", "4G"... NULL = single file.
                                         Creating split volumes is not supported yet:
                                         a non-NULL value makes az_writer_open fail
                                         with AZ_ERROR_UNSUPPORTED. Reading split
                                         volumes does work. */
  const char          *comment;       /* zip/7z archive comment; may be NULL */
  const az_callbacks  *callbacks;     /* may be NULL */
  const char         *extra_properties; /* newline separated "name=value" passthrough, e.g. "mf=bt4\nfb=273" */
  int32_t             preallocate;   /* 1 = reserve the output file's clusters before writing.
                                        Windows: SetFileInformationByHandle(FileAllocationInfo);
                                        POSIX: posix_fallocate(). The reservation is an upper bound
                                        (the uncompressed total), so it can be far larger than the
                                        finished archive -- it is released when the file closes.
                                        Best effort: failure only warns, it never aborts the write.
                                        Ignored when writing to memory. */
  int32_t             share_locked;  /* 1 = open members with FILE_SHARE_READ|FILE_SHARE_WRITE, so a
                                        file another process is still writing can be packed. A member
                                        that still cannot be opened is skipped and reported, exactly
                                        as before -- the archive is not abandoned. */
} az_create_options;

#define AZ_CREATE_OPTIONS_INIT \
  { sizeof(az_create_options), "7z", NULL, -1, 0, 0, 0, 0, NULL, AZ_ENC_NONE, \
    0, 1, 1, NULL, NULL, NULL, NULL, 0, 0 }

/* Metadata attached to a single input item. Zero fields mean "derive". */
typedef struct az_item_meta {
  uint32_t struct_size;   /* = sizeof(az_item_meta) */
  int64_t  mtime_unix;    /* 0 = now */
  int64_t  ctime_unix;
  int64_t  atime_unix;
  uint32_t attributes;    /* Win32 attribute bits; 0 = derive from the source */
  uint32_t posix_mode;    /* 0 = derive */
  int32_t  is_dir;        /* -1 = derive from source, 0 = file, 1 = directory */
} az_item_meta;

#define AZ_ITEM_META_INIT { sizeof(az_item_meta), 0, 0, 0, 0, 0, -1 }

/*
 * Opens `path_utf8` for writing. The file is created (or truncated) lazily on
 * az_writer_add_*; if nothing is added, az_writer_finish still produces a
 * valid empty archive of the requested format.
 */
AZ_API az_result AZ_CALL az_writer_open(const char *path_utf8,
                                        const az_create_options *opts, /* NULL = defaults */
                                        az_writer *out);

/* Same, but the archive is assembled in memory and handed back on finish. */
AZ_API az_result AZ_CALL az_writer_open_memory(const az_create_options *opts,
                                               az_writer *out);

/* Add a file, or a whole directory tree when recurse is non-zero. */
AZ_API az_result AZ_CALL az_writer_add_file(az_writer writer,
                                            const char *source_path_utf8,
                                            const char *entry_name_utf8, /* NULL = base name of source */
                                            const az_item_meta *meta,    /* NULL = defaults */
                                            int32_t recurse);

/* Add bytes the caller already has in memory. */
AZ_API az_result AZ_CALL az_writer_add_buffer(az_writer writer,
                                              const void *data,
                                              size_t size,
                                              const char *entry_name_utf8,
                                              const az_item_meta *meta);

/* Add an empty directory entry. */
AZ_API az_result AZ_CALL az_writer_add_directory(az_writer writer,
                                                 const char *entry_name_utf8,
                                                 const az_item_meta *meta);

/*
 * Runs compression and writes the result. After this the writer is finished;
 * az_writer_free must still be called. For memory writers, *out_data receives
 * an az_alloc'd block released by az_free.
 */
AZ_API az_result AZ_CALL az_writer_finish(az_writer writer, void **out_data, size_t *out_size);

AZ_API void AZ_CALL az_writer_free(az_writer writer);

/* ------------------------------------------------------------------------- */
/* Hashing                                                                   */
/* ------------------------------------------------------------------------- */

typedef struct az_hasher_s *az_hasher;

AZ_API az_result AZ_CALL az_hasher_open(const char *name /* "CRC32","SHA256","BLAKE2sp","XXH64" */,
                                        az_hasher *out);
AZ_API void      AZ_CALL az_hasher_update(az_hasher h, const void *data, size_t size);
AZ_API az_result AZ_CALL az_hasher_finish(az_hasher h, uint8_t *digest, size_t capacity, size_t *written);
AZ_API void      AZ_CALL az_hasher_close(az_hasher h);

/* ------------------------------------------------------------------------- */
/* Memory                                                                    */
/* ------------------------------------------------------------------------- */

/* Releases a block handed out by az_writer_finish. */
AZ_API void AZ_CALL az_free(void *ptr);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* ANDZIP_ANDZIP_H */
