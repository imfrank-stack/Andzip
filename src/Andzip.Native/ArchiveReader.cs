using System.Runtime.InteropServices;
using Andzip.Native.Interop;

namespace Andzip.Native;

/// <summary>
/// A read-side handle over an opened archive. Wraps an <c>az_archive</c>.
/// <para>
/// Thread affinity: a single reader is NOT thread safe — the ABI only guarantees
/// safety when one handle is touched by one thread at a time. Open one reader per
/// concurrent worker if you need parallel extraction.
/// </para>
/// <para>
/// All returned strings (<see cref="FormatName"/>, <see cref="Comment"/>) point into
/// native memory that stays valid only until <see cref="Dispose"/>; the shell copies
/// them to managed strings on read, so callers never hold dangling pointers.
/// </para>
/// </summary>
public sealed class ArchiveReader : IDisposable
{
    private readonly IntPtr _archive;
    // The engine keeps the az_open_options callback bridge alive inside the archive
    // context and calls it during ExtractTo/Test, i.e. after Open returned. Releasing
    // it at the end of Open was a use-after-free; it now lives until Dispose.
    private readonly IDisposable? _ownedArgs;
    private bool _disposed;

    private ArchiveReader(IntPtr archive, IDisposable? ownedArgs)
    {
        _archive = archive;
        _ownedArgs = ownedArgs;
    }

    /// <summary>Opens a file on disk via <c>az_archive_open</c>.</summary>
    public static ArchiveReader Open(string path, OpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        NativeMethods.EnsureLoaded();
        using var strings = new Utf8.Scope();
        var args = options is null ? null : NativeArgs.Open(options);
        var pathPtr = strings.Add(path);
        try
        {
            NativeMethods.Check(NativeMethods.az_archive_open(pathPtr, args?.Ptr ?? IntPtr.Zero, out var handle), "az_archive_open");
            return new ArchiveReader(handle, args);
        }
        catch
        {
            args?.Dispose();
            throw;
        }
    }

    /// <summary>Opens from an in-memory image via <c>az_archive_open_memory</c>. The
    /// buffer is copied by the engine, so it need only stay valid for this call.
    /// <paramref name="nameHint"/> assists format auto-detection when the bytes have no signature.</summary>
    public static ArchiveReader Open(ReadOnlyMemory<byte> data, string? nameHint = null, OpenOptions? options = null)
    {
        NativeMethods.EnsureLoaded();
        using var strings = new Utf8.Scope();
        var args = options is null ? null : NativeArgs.Open(options);
        var namePtr = strings.Add(nameHint);
        using var pinned = PinnedMemory.Create(data);
        try
        {
            NativeMethods.Check(
                NativeMethods.az_archive_open_memory(pinned.Pointer, (UIntPtr)pinned.Length, namePtr, args?.Ptr ?? IntPtr.Zero, out var handle),
                "az_archive_open_memory");
            return new ArchiveReader(handle, args);
        }
        catch
        {
            args?.Dispose();
            throw;
        }
    }

    /// <summary>Convenience: open an in-memory image with options but no format hint.</summary>
    public static ArchiveReader Open(ReadOnlyMemory<byte> data, OpenOptions options)
        => Open(data, null, options);

    /// <summary>Detected canonical format name from <c>az_archive_format_name</c>.</summary>
    public string FormatName
    {
        get
        {
            ThrowIfDisposed();
            return Utf8.Decode(NativeMethods.az_archive_format_name(_archive)) ?? string.Empty;
        }
    }

    /// <summary>Total entry count from <c>az_archive_entry_count</c>.</summary>
    public ulong EntryCount
    {
        get
        {
            ThrowIfDisposed();
            NativeMethods.Check(NativeMethods.az_archive_entry_count(_archive, out var count), "az_archive_entry_count");
            return count;
        }
    }

    /// <summary>Whole-archive comment from <c>az_archive_comment</c>; null when absent.</summary>
    public string? Comment
    {
        get
        {
            ThrowIfDisposed();
            NativeMethods.Check(NativeMethods.az_archive_comment(_archive, out var ptr), "az_archive_comment");
            return Utf8.Decode(ptr);
        }
    }

    /// <summary>Physical on-disk size via <c>az_archive_physical_size</c> (may include volume headers).</summary>
    public ulong PhysicalSize
    {
        get
        {
            ThrowIfDisposed();
            NativeMethods.Check(NativeMethods.az_archive_physical_size(_archive, out var size), "az_archive_physical_size");
            return size;
        }
    }

    public bool IsEncrypted => GetFlag("az_archive_is_encrypted", NativeMethods.az_archive_is_encrypted);
    public bool IsSolid => GetFlag("az_archive_is_solid", NativeMethods.az_archive_is_solid);
    public bool IsVolume => GetFlag("az_archive_is_volume", NativeMethods.az_archive_is_volume);

    private bool GetFlag(string op, AzFlagGetter getter)
    {
        ThrowIfDisposed();
        NativeMethods.Check(getter(_archive, out var v), op);
        return v != 0;
    }

    /// <summary>Every entry, materialised through <c>az_entry_get</c>/<c>az_entry_release</c>.
    /// Snapshots are detached so they are safe to keep after <see cref="Dispose"/>.</summary>
    public IReadOnlyList<ArchiveEntry> Entries
    {
        get
        {
            ThrowIfDisposed();
            ulong n = EntryCount;
            var list = new List<ArchiveEntry>((int)Math.Min(n, 1_000_000));
            for (ulong i = 0; i < n; i++)
                list.Add(GetEntry(i));
            return list;
        }
    }

    /// <summary>Reads one entry by index via <c>az_entry_get</c>, releasing the native
    /// <c>_bag</c> immediately. The entry's own <c>struct_size</c> is set before the call.</summary>
    public ArchiveEntry this[ulong index] => GetEntry(index);

    public ArchiveEntry GetEntry(ulong index)
    {
        ThrowIfDisposed();
        var info = default(AzEntryInfo);
        info.StructSize = (uint)Marshal.SizeOf<AzEntryInfo>();
        NativeMethods.Check(NativeMethods.az_entry_get(_archive, index, ref info), "az_entry_get");
        try
        {
            return new ArchiveEntry(
                info.Index,
                Utf8.Decode(info.Name) ?? string.Empty,
                Utf8.Decode(info.Method),
                UnknownToNull(info.Size),
                UnknownToNull(info.PackedSize),
                info.Crc,
                info.IsDir != 0,
                info.IsEncrypted != 0,
                info.IsAnti != 0,
                info.HasAttributes != 0 ? info.Attributes : 0,
                info.MTimeUnix != 0 ? DateTimeOffset.FromUnixTimeSeconds(info.MTimeUnix) : null,
                info.PosixMode,
                Utf8.Decode(info.Comment),
                info.NumSubFiles);
        }
        finally
        {
            NativeMethods.az_entry_release(ref info);
        }
    }

    /// <summary>
    /// Finds an entry by exact stored name (forward-slash separators), or null.
    /// Scans <see cref="Entries"/>, which is the shell's own search: the engine
    /// used to export <c>az_entry_find</c> but it faulted when called through
    /// P/Invoke, so the lookup lives here instead (see native/README.md).
    /// </summary>
    public ArchiveEntry? FindEntry(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var entry in Entries)
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                return entry;
        return null;
    }

    /// <summary>Extracts every entry into <paramref name="destDir"/> via
    /// <c>az_archive_extract</c> (the engine sanitises traversal — see its docs).</summary>
    public void ExtractTo(string destDir, ExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(destDir);
        ExtractCore(null, destDir, options);
    }

    /// <summary>Extracts only the listed entries; an empty sequence extracts nothing extra.
    /// Backed by <c>az_archive_extract</c> with an explicit index array.</summary>
    public void Extract(IEnumerable<ulong> indices, string destDir, ExtractOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(destDir);
        ExtractCore(indices.ToArray(), destDir, options);
    }

    /// <summary>Decompresses and CRC-verifies without writing files (<c>test_only</c>).</summary>
    public void Test(ExtractOptions? options = null)
    {
        ThrowIfDisposed();
        using var args = NativeArgs.Extract(options ?? new ExtractOptions(), testOnly: true);
        NativeMethods.Check(NativeMethods.az_archive_extract(_archive, IntPtr.Zero, IntPtr.Zero, UIntPtr.Zero, args.Ptr), "az_archive_extract (test)");
    }

    private void ExtractCore(ulong[]? indices, string destDir, ExtractOptions? options)
    {
        ThrowIfDisposed();
        using var strings = new Utf8.Scope();
        using var args = NativeArgs.Extract(options ?? new ExtractOptions(), testOnly: false);
        var destPtr = strings.Add(destDir);

        GCHandle idxHandle = default;
        bool pinned = false;
        try
        {
            UIntPtr count = UIntPtr.Zero;
            IntPtr idxPtr = IntPtr.Zero;
            if (indices is { Length: > 0 })
            {
                idxHandle = GCHandle.Alloc(indices, GCHandleType.Pinned);
                pinned = true;
                idxPtr = idxHandle.AddrOfPinnedObject();
                count = (UIntPtr)(ulong)indices.Length;
            }
            NativeMethods.Check(NativeMethods.az_archive_extract(_archive, destPtr, idxPtr, count, args.Ptr), "az_archive_extract");
        }
        finally
        {
            if (pinned) idxHandle.Free();
        }
    }

    /// <summary>Reads a whole entry into a fresh array. Uses the stored size when known,
    /// otherwise streams through <c>az_entry_read_stream</c>.</summary>
    public byte[] ReadEntry(ulong index)
    {
        ThrowIfDisposed();
        ulong? size = TryGetSize(index);
        if (size is > 0 and <= int.MaxValue)
        {
            var buffer = new byte[(int)size.Value];
            int written = ReadRaw(index, buffer, buffer.Length, allowExact: false);
            if (written >= 0)
                return written == buffer.Length ? buffer : Shrink(buffer, written);
            // written < 0 signalled BufferSmall; fall through to the stream path.
        }
        using var ms = new MemoryStream();
        ReadEntry(index, ms);
        return ms.ToArray();
    }

    /// <summary>Decompresses one entry straight into <paramref name="destination"/>
    /// through a native write-callback sink — safe for entries larger than RAM.</summary>
    public void ReadEntry(ulong index, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfDisposed();
        using var sink = new StreamSink(destination);
        NativeMethods.Check(NativeMethods.az_entry_read_stream(_archive, index, sink.FunctionPointer, IntPtr.Zero), "az_entry_read_stream");
    }

    /// <summary>Decompresses one entry into <paramref name="destination"/>; returns the
    /// byte count written. Throws <see cref="AndzipException"/> with
    /// <see cref="AzResult.ErrorBufferSmall"/> when the entry does not fit.</summary>
    public int ReadEntry(ulong index, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfDisposed();
        int written = ReadRaw(index, destination, destination.Length, allowExact: true);
        return written;
    }

    // Shared az_entry_read path. Returns bytes written; -1 only when allowExact is false
    // and the buffer proved too small (caller then retries via the stream).
    private int ReadRaw(ulong index, byte[] buffer, int capacity, bool allowExact)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            int code = NativeMethods.az_entry_read(_archive, index, handle.AddrOfPinnedObject(), (UIntPtr)(ulong)capacity, out var writtenU);
            if (code == (int)NativeResult.Ok)
                return (int)writtenU.ToUInt64();
            if (code == (int)NativeResult.ErrorBufferSmall && !allowExact)
                return -1;
            NativeMethods.Check(code, "az_entry_read");
            throw new InvalidOperationException(); // Check always throws
        }
        finally
        {
            handle.Free();
        }
    }

    private ulong? TryGetSize(ulong index)
    {
        try
        {
            return GetEntry(index).Size;
        }
        catch (AndzipException)
        {
            return null;
        }
    }

    private static ulong? UnknownToNull(ulong v) => v == ulong.MaxValue ? null : v;

    private static byte[] Shrink(byte[] src, int length)
    {
        var dst = new byte[length];
        Array.Copy(src, dst, length);
        return dst;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ArchiveReader), "the archive handle has been closed (az_archive_close)");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        NativeMethods.az_archive_close(_archive);
        _ownedArgs?.Dispose();
    }
}

/// <summary>Signature of the native <c>int32_t*</c> property getters.</summary>
internal delegate int AzFlagGetter(IntPtr archive, out int value);
