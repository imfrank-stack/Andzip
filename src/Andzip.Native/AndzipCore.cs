using System.Runtime.InteropServices;
using Andzip.Native.Interop;

namespace Andzip.Native;

/// <summary>Engine-wide build metadata returned by <c>az_get_build_info</c>.</summary>
public sealed record BuildInformation(
    uint AbiVersion,
    string CoreVersion,
    string Engine,
    string BuildConfig,
    uint FormatCount,
    uint CodecCount,
    bool HasZstdEncoder,
    bool HasLz4Encoder,
    bool HasBrotliEncoder);

/// <summary>
/// Process-wide entry point over the flat C ABI. Every member resolves the native
/// library first (raising <see cref="AndzipLibraryNotFoundException"/> with the tried
/// paths if it is missing). The thread knobs (<see cref="NumThreads"/>,
/// <see cref="MemoryLimitMiB"/>) are global, apply to operations started afterwards,
/// and must be set before opening readers/writers to influence them.
/// </summary>
public static class AndzipCore
{
    /// <summary>ABI contract version (mirrors <c>AZ_ABI_VERSION</c>).</summary>
    public static uint AbiVersion
    {
        get
        {
            NativeMethods.EnsureLoaded();
            return NativeMethods.az_abi_version();
        }
    }

    /// <summary>Version, engine and feature flags from the loaded native library.</summary>
    public static BuildInformation BuildInfo
    {
        get
        {
            NativeMethods.EnsureLoaded();
            var info = default(AzBuildInfo);
            info.StructSize = (uint)Marshal.SizeOf<AzBuildInfo>();
            NativeMethods.Check(NativeMethods.az_get_build_info(ref info), "az_get_build_info");
            return new BuildInformation(
                info.AbiVersion,
                Utf8.Decode(info.CoreVersion) ?? string.Empty,
                Utf8.Decode(info.Engine) ?? string.Empty,
                Utf8.Decode(info.BuildConfig) ?? string.Empty,
                info.FormatCount,
                info.CodecCount,
                info.HasZstdEncoder != 0,
                info.HasLz4Encoder != 0,
                info.HasBrotliEncoder != 0);
        }
    }

    /// <summary>All container formats the build knows, via <c>az_format_count</c>/<c>az_format_get</c>.
    /// Each native <c>_bag</c> is released as it is read.</summary>
    public static IReadOnlyList<FormatInfo> Formats
    {
        get
        {
            NativeMethods.EnsureLoaded();
            uint count = NativeMethods.az_format_count();
            var list = new List<FormatInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var info = default(AzFormatInfo);
                info.StructSize = (uint)Marshal.SizeOf<AzFormatInfo>();
                NativeMethods.Check(NativeMethods.az_format_get(i, ref info), "az_format_get");
                try
                {
                    list.Add(new FormatInfo(
                        info.Index,
                        Utf8.Decode(info.Name) ?? string.Empty,
                        Utf8.Decode(info.Extension),
                        Utf8.Decode(info.AddExtension),
                        info.CanRead != 0,
                        info.CanWrite != 0,
                        info.Flags));
                }
                finally
                {
                    NativeMethods.az_format_release(ref info);
                }
            }
            return list;
        }
    }

    /// <summary>All codecs the build exposes, via <c>az_codec_count</c>/<c>az_codec_get</c>.
    /// <see cref="CodecInfo.IdHex"/> is the lower-case method id.</summary>
    public static IReadOnlyList<CodecInfo> Codecs
    {
        get
        {
            NativeMethods.EnsureLoaded();
            uint count = NativeMethods.az_codec_count();
            var list = new List<CodecInfo>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var info = default(AzCodecInfo);
                info.StructSize = (uint)Marshal.SizeOf<AzCodecInfo>();
                NativeMethods.Check(NativeMethods.az_codec_get(i, ref info), "az_codec_get");
                try
                {
                    list.Add(new CodecInfo(
                        info.Index,
                        Utf8.Decode(info.Name) ?? string.Empty,
                        Utf8.Decode(info.IdHex) ?? string.Empty,
                        info.CanEncode != 0,
                        info.CanDecode != 0,
                        info.IsFilter != 0));
                }
                finally
                {
                    NativeMethods.az_codec_release(ref info);
                }
            }
            return list;
        }
    }

    /// <summary>Worker threads for multi-threaded coders. 0 means one per hardware
    /// thread. Maps to <c>az_set_num_threads</c>/<c>az_get_num_threads</c>.</summary>
    public static uint NumThreads
    {
        get
        {
            NativeMethods.EnsureLoaded();
            return NativeMethods.az_get_num_threads();
        }
        set
        {
            NativeMethods.EnsureLoaded();
            NativeMethods.az_set_num_threads(value);
        }
    }

    /// <summary>Upper bound, in MiB, a decoder may allocate; 0 disables the limit.
    /// Maps to <c>az_set_memory_limit_mib</c>/<c>az_get_memory_limit_mib</c>.</summary>
    public static ulong MemoryLimitMiB
    {
        get
        {
            NativeMethods.EnsureLoaded();
            return NativeMethods.az_get_memory_limit_mib();
        }
        set
        {
            NativeMethods.EnsureLoaded();
            NativeMethods.az_set_memory_limit_mib(value);
        }
    }
}
