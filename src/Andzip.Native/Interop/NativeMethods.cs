using System.Reflection;
using System.Runtime.InteropServices;

namespace Andzip.Native.Interop;

/// <summary>
/// Flat P/Invoke surface for every <c>az_*</c> export. All use
/// <see cref="CallingConvention.Cdecl"/> to match the ABI's <c>AZ_CALL</c> — the
/// default (StdCall on Windows x86) must never leak in. Strings are passed as
/// pre-encoded UTF-8 <see cref="IntPtr"/>s and buffers as pinned/unmanaged pointers,
/// so none of these declarations marshal anything implicitly.
/// </summary>
internal static class NativeMethods
{
    private const string LibName = NativeLibraryResolver.LibraryName;

    // Registering the resolver here guarantees it runs before the first P/Invoke:
    // the runtime invokes this static constructor before any member below executes.
    static NativeMethods()
    {
        NativeLibraryResolver.Register(typeof(NativeMethods).Assembly);
    }

    /// <summary>Resolves the engine up front so failures raise the descriptive
    /// <see cref="AndzipLibraryNotFoundException"/> instead of a bare
    /// <see cref="DllNotFoundException"/>. Touching <see cref="NativeMethods"/> at
    /// all runs the static constructor first, registering the resolver.</summary>
    public static void EnsureLoaded() => NativeLibraryResolver.EnsureLoaded();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr az_last_error_message();

    /// <summary>The thread-local description of the most recent failure.</summary>
    public static string LastErrorMessage() => Utf8.Decode(az_last_error_message()) ?? string.Empty;

    // ---- Global state -----------------------------------------------------
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint az_abi_version();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_get_build_info(ref AzBuildInfo info);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_set_num_threads(uint threads);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint az_get_num_threads();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_set_memory_limit_mib(ulong mib);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong az_get_memory_limit_mib();

    // ---- Formats ----------------------------------------------------------
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint az_format_count();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_format_get(uint index, ref AzFormatInfo info);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_format_release(ref AzFormatInfo info);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_format_find(IntPtr nameOrExtension, out uint outIndex);

    // ---- Codecs -----------------------------------------------------------
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint az_codec_count();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_codec_get(uint index, ref AzCodecInfo info);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_codec_release(ref AzCodecInfo info);

    // ---- Opening / inspecting --------------------------------------------
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_archive_open(IntPtr pathUtf8, IntPtr options, out IntPtr archive);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_archive_open_memory(IntPtr data, UIntPtr size, IntPtr nameHintUtf8, IntPtr options, out IntPtr archive);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_archive_close(IntPtr archive);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr az_archive_format_name(IntPtr archive);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_archive_entry_count(IntPtr archive, out ulong outCount);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_archive_comment(IntPtr archive, out IntPtr outUtf8);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_archive_physical_size(IntPtr archive, out ulong outSize);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_archive_is_encrypted(IntPtr archive, out int outValue);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_archive_is_solid(IntPtr archive, out int outValue);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_archive_is_volume(IntPtr archive, out int outValue);

    // ---- Entries ----------------------------------------------------------
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_entry_get(IntPtr archive, ulong index, ref AzEntryInfo info);


    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_entry_release(ref AzEntryInfo info);

    // ---- Extraction -------------------------------------------------------
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_archive_extract(IntPtr archive, IntPtr destDirUtf8, IntPtr indices, UIntPtr count, IntPtr options);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_entry_read(IntPtr archive, ulong index, IntPtr buffer, UIntPtr capacity, out UIntPtr written);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_entry_read_stream(IntPtr archive, ulong index, IntPtr sink, IntPtr sinkContext);

    // ---- Creating ---------------------------------------------------------
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_writer_open(IntPtr pathUtf8, IntPtr options, out IntPtr writer);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_writer_open_memory(IntPtr options, out IntPtr writer);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_writer_add_file(IntPtr writer, IntPtr sourcePathUtf8, IntPtr entryNameUtf8, IntPtr meta, int recurse);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_writer_add_buffer(IntPtr writer, IntPtr data, UIntPtr size, IntPtr entryNameUtf8, IntPtr meta);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_writer_add_directory(IntPtr writer, IntPtr entryNameUtf8, IntPtr meta);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_writer_finish(IntPtr writer, IntPtr outData, IntPtr outSize);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_writer_free(IntPtr writer);

    // ---- Hashing ----------------------------------------------------------
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_hasher_open(IntPtr name, out IntPtr hasher);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_hasher_update(IntPtr hasher, IntPtr data, UIntPtr size);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int az_hasher_finish(IntPtr hasher, IntPtr digest, UIntPtr capacity, out UIntPtr written);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_hasher_close(IntPtr hasher);

    // ---- Memory -----------------------------------------------------------
    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void az_free(IntPtr ptr);

    /// <summary>Central choke point: turns any non-<see cref="NativeResult.Ok"/> return
    /// into an <see cref="AndzipException"/>, capturing the thread-local native message
    /// before anything can overwrite it.</summary>
    public static void Check(int code, string operation)
    {
        if (code == (int)NativeResult.Ok)
            return;
        var message = LastErrorMessage();
        throw new AndzipException((AzResult)code, operation, message);
    }
}
