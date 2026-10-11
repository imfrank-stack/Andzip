// Blittable mirrors of the structs in native/include/andzip/andzip.h.
//
// Field order and packing must match the C header exactly; every struct carries
// a struct_size that the native side validates, so a layout drift is reported
// rather than silently corrupting memory.

using System.Runtime.InteropServices;

namespace Andzip.Native.Interop;

// Internal mirror of az_result. Named NativeResult so the public, consumer-facing
// Andzip.Native.AzResult enum (same values) can live alongside it without clashing.
internal enum NativeResult : int
{
    Ok = 0,
    ErrorGeneric = 1,
    ErrorInvalidArg = 2,
    ErrorNotFound = 3,
    ErrorIo = 4,
    ErrorOutOfMemory = 5,
    ErrorUnsupported = 6,
    ErrorBadFormat = 7,
    ErrorPassword = 8,
    ErrorCrc = 9,
    ErrorCancelled = 10,
    ErrorAlreadyDone = 11,
    ErrorBufferSmall = 12,
    ErrorInternal = 13,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzProgressNative
{
    public int Op;
    public IntPtr CurrentEntry;
    public ulong EntryIndex;
    public ulong EntryCount;
    public ulong BytesIn;
    public ulong TotalIn;
    public ulong BytesOut;
    public ulong TotalOut;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzCallbacks
{
    public uint StructSize;
    public IntPtr Progress;
    public IntPtr Password;
    public IntPtr Ctx;

    public static AzCallbacks Empty() => new() { StructSize = (uint)Marshal.SizeOf<AzCallbacks>() };
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzBuildInfo
{
    public uint StructSize;
    public uint AbiVersion;
    public IntPtr CoreVersion;
    public IntPtr Engine;
    public IntPtr BuildConfig;
    public uint FormatCount;
    public uint CodecCount;
    public uint HasZstdEncoder;
    public uint HasLz4Encoder;
    public uint HasBrotliEncoder;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzFormatInfo
{
    public uint StructSize;
    public uint Index;
    public IntPtr Name;
    public IntPtr Extension;
    public IntPtr AddExtension;
    public int CanRead;
    public int CanWrite;
    public uint Flags;
    public IntPtr Signatures;
    public uint SignaturesSize;
    public IntPtr Bag;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzCodecInfo
{
    public uint StructSize;
    public uint Index;
    public IntPtr Name;
    public IntPtr IdHex;
    public int CanEncode;
    public int CanDecode;
    public int IsFilter;
    public IntPtr Bag;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzOpenOptions
{
    public uint StructSize;
    public IntPtr Password;
    public IntPtr ForcedFormat;
    public IntPtr Callbacks;
    public int HeadersOnly;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzExtractOptions
{
    public uint StructSize;
    public IntPtr Password;
    public IntPtr Callbacks;
    public int Overwrite;
    public int PreserveMTime;
    public int TestOnly;
    public int Flatten;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzCreateOptions
{
    public uint StructSize;
    public IntPtr Format;
    public IntPtr Method;
    public int Level;
    public uint DictSizeMib;
    public uint WordSize;
    public uint NumThreads;
    public ulong MemoryLimitMib;
    public IntPtr Password;
    public int Encryption;
    public int EncryptHeaders;
    public int Solid;
    public int MultiThread;
    public IntPtr VolumeSize;
    public IntPtr Comment;
    public IntPtr Callbacks;
    public IntPtr ExtraProperties;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzItemMeta
{
    public uint StructSize;
    public long MTimeUnix;
    public long CTimeUnix;
    public long ATimeUnix;
    public uint Attributes;
    public uint PosixMode;
    public int IsDir;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AzEntryInfo
{
    public uint StructSize;
    public ulong Index;
    public IntPtr Name;
    public IntPtr Method;
    public ulong Size;
    public ulong PackedSize;
    public uint Crc;
    public int IsDir;
    public int IsEncrypted;
    public int IsAnti;
    public int HasAttributes;
    public uint Attributes;
    public long MTimeUnix;
    public long CTimeUnix;
    public long ATimeUnix;
    public uint PosixMode;
    public IntPtr Comment;
    public ulong NumSubFiles;
    public IntPtr Bag;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate NativeResult AzProgressFn(IntPtr ctx, ref AzProgressNative progress);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int AzPasswordFn(IntPtr ctx, IntPtr buffer, UIntPtr capacity);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate NativeResult AzWriteFn(IntPtr ctx, IntPtr data, UIntPtr size);
