using System.Runtime.InteropServices;
using Andzip.Native.Interop;

namespace Andzip.Native;

/// <summary>
/// A write-side handle over an archive under construction. Wraps an <c>az_writer</c>.
/// <para>
/// Ownership: the caller supplies either a filesystem path (<see cref="Create"/>) or
/// an in-memory sink (<see cref="CreateInMemory"/>). In memory mode the finished bytes
/// are handed back by <see cref="FinishToBytes"/>; the engine's allocation is copied and
/// released with <c>az_free</c> inside that call, so no native memory escapes.
/// </para>
/// <para>
/// Not thread safe: drive one writer from one thread. After <see cref="Finish"/> or
/// <see cref="FinishToBytes"/> the handle is finished; further adds throw
/// <see cref="AzResult.ErrorAlreadyDone"/>.
/// </para>
/// </summary>
public sealed class ArchiveWriter : IDisposable
{
    private readonly IntPtr _writer;
    private readonly bool _toMemory;
    // The engine copies az_create_options into its own context, but the copy still
    // points at the callback bridge we allocated. Compression runs in Finish(), long
    // after Create() returned, so the block (and the delegates it roots) must live
    // until the writer is freed -- freeing it at the end of Create was a use-after-free.
    private readonly IDisposable? _ownedArgs;
    private bool _finished;
    private bool _disposed;

    private readonly List<string> _warnings = new();

    /// <summary>Non-fatal advisories from the last <see cref="Finish"/>/<see cref="FinishToBytes"/>
    /// — settings the chosen format could not honour. The native call still returned
    /// <see cref="AzResult.Ok"/>; surfaced here rather than swallowed.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    private ArchiveWriter(IntPtr writer, bool toMemory, IDisposable? ownedArgs)
    {
        _writer = writer;
        _toMemory = toMemory;
        _ownedArgs = ownedArgs;
    }

    /// <summary>Opens a file writer via <c>az_writer_open</c>. The file is created lazily;
    /// finishing with nothing added still yields a valid empty archive.</summary>
    public static ArchiveWriter Create(string path, CreateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        NativeMethods.EnsureLoaded();
        var args = options is null ? null : NativeArgs.Create(options);
        using var strings = new Utf8.Scope();
        try
        {
            NativeMethods.Check(NativeMethods.az_writer_open(strings.Add(path), args?.Ptr ?? IntPtr.Zero, out var writer), "az_writer_open");
            return new ArchiveWriter(writer, toMemory: false, args);
        }
        catch
        {
            args?.Dispose();
            throw;
        }
    }

    /// <summary>Opens an in-memory writer via <c>az_writer_open_memory</c>; retrieve the
    /// result with <see cref="FinishToBytes"/>.</summary>
    public static ArchiveWriter CreateInMemory(CreateOptions? options = null)
    {
        NativeMethods.EnsureLoaded();
        var args = options is null ? null : NativeArgs.Create(options);
        try
        {
            NativeMethods.Check(NativeMethods.az_writer_open_memory(args?.Ptr ?? IntPtr.Zero, out var writer), "az_writer_open_memory");
            return new ArchiveWriter(writer, toMemory: true, args);
        }
        catch
        {
            args?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rewrites an archive that already exists via <c>az_writer_open_existing</c>.
    /// <para>
    /// <paramref name="options"/>.<c>Password</c> here is the password for <em>reading</em>
    /// the existing file, not just for writing the result -- a header-encrypted 7z cannot
    /// even be enumerated without it, and the native side refuses the open rather than
    /// offering a member list it cannot see.
    /// </para>
    /// <para>
    /// Members survive by being listed: call <see cref="KeepMember"/> for each one to
    /// keep, in listing order, and omit the ones to delete. Finish writes a temporary
    /// beside the original and renames it over the top, so <strong>the caller must not
    /// hold this file open</strong> -- Windows refuses the swap while any handle without
    /// FILE_SHARE_DELETE exists, which includes one this process opened itself.
    /// </para>
    /// </summary>
    public static ArchiveWriter OpenExisting(string path, CreateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        NativeMethods.EnsureLoaded();
        var args = options is null ? null : NativeArgs.Create(options);
        using var strings = new Utf8.Scope();
        try
        {
            NativeMethods.Check(NativeMethods.az_writer_open_existing(strings.Add(path), args?.Ptr ?? IntPtr.Zero, out var writer), "az_writer_open_existing");
            return new ArchiveWriter(writer, toMemory: false, args);
        }
        catch
        {
            args?.Dispose();
            throw;
        }
    }

    /// <summary>Carries one existing member over untouched via <c>az_writer_keep_member</c>.
    /// The stored bytes are copied, not recompressed, and the name, times and attributes
    /// come from the archive -- which is why <paramref name="indexInArchive"/> addresses the
    /// listing the writer opened, not the one being written.</summary>
    public void KeepMember(uint indexInArchive)
    {
        ThrowIfUnavailable();
        NativeMethods.Check(NativeMethods.az_writer_keep_member(_writer, indexInArchive), "az_writer_keep_member");
    }

    /// <summary>Adds a file, or a whole directory tree when <paramref name="recurse"/> is set,
    /// via <c>az_writer_add_file</c>. <paramref name="entryName"/> null derives the base name.</summary>
    public void AddFile(string sourcePath, string? entryName = null, ItemMeta? meta = null, bool recurse = true)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ThrowIfUnavailable();
        using var strings = new Utf8.Scope();
        using var metaArgs = meta is null ? null : NativeArgs.Meta(meta);
        NativeMethods.Check(
            NativeMethods.az_writer_add_file(_writer, strings.Add(sourcePath), strings.Add(entryName), metaArgs?.Ptr ?? IntPtr.Zero, recurse ? 1 : 0),
            "az_writer_add_file");
    }

    /// <summary>Adds in-memory bytes under <paramref name="entryName"/> via <c>az_writer_add_buffer</c>.
    /// The engine copies the data, so it need only stay valid for the call.</summary>
    public void AddData(ReadOnlyMemory<byte> data, string entryName, ItemMeta? meta = null)
    {
        ArgumentNullException.ThrowIfNull(entryName);
        ThrowIfUnavailable();
        using var strings = new Utf8.Scope();
        using var metaArgs = meta is null ? null : NativeArgs.Meta(meta);
        using var pinned = PinnedMemory.Create(data);
        NativeMethods.Check(
            NativeMethods.az_writer_add_buffer(_writer, pinned.Pointer, (UIntPtr)pinned.Length, strings.Add(entryName), metaArgs?.Ptr ?? IntPtr.Zero),
            "az_writer_add_buffer");
    }

    public void AddData(byte[] data, string entryName, ItemMeta? meta = null)
        => AddData((ReadOnlyMemory<byte>)data, entryName, meta);

    /// <summary>Adds an empty directory entry via <c>az_writer_add_directory</c>.</summary>
    public void AddDirectory(string entryName, ItemMeta? meta = null)
    {
        ArgumentNullException.ThrowIfNull(entryName);
        ThrowIfUnavailable();
        using var strings = new Utf8.Scope();
        using var metaArgs = meta is null ? null : NativeArgs.Meta(meta);
        NativeMethods.Check(
            NativeMethods.az_writer_add_directory(_writer, strings.Add(entryName), metaArgs?.Ptr ?? IntPtr.Zero),
            "az_writer_add_directory");
    }

    /// <summary>Runs compression and writes the file-backed archive (<c>az_writer_finish</c>
    /// with NULL output). Any format advisories land in <see cref="Warnings"/>.</summary>
    public void Finish()
    {
        ThrowIfUnavailable();
        NativeMethods.Check(NativeMethods.az_writer_finish(_writer, IntPtr.Zero, IntPtr.Zero), "az_writer_finish");
        _finished = true;
        CaptureWarnings();
    }

    /// <summary>Finishes an in-memory writer and returns the archive bytes. The engine's
    /// block is copied to managed memory and released with <c>az_free</c> here.</summary>
    public byte[] FinishToBytes()
    {
        ThrowIfUnavailable();
        if (!_toMemory)
            throw new InvalidOperationException("FinishToBytes is only valid for writers from CreateInMemory; file writers use Finish().");

        int sizeOfPtr = IntPtr.Size;
        int sizeOfWord = UIntPtr.Size;
        IntPtr dataSlot = Marshal.AllocHGlobal(sizeOfPtr);
        IntPtr sizeSlot = Marshal.AllocHGlobal(sizeOfWord);
        Marshal.WriteIntPtr(dataSlot, IntPtr.Zero);
        try
        {
            NativeMethods.Check(NativeMethods.az_writer_finish(_writer, dataSlot, sizeSlot), "az_writer_finish");
            _finished = true;
            CaptureWarnings();

            var block = Marshal.ReadIntPtr(dataSlot);
            var size = (long)Marshal.ReadIntPtr(sizeSlot);
            if (block == IntPtr.Zero || size == 0)
                return Array.Empty<byte>();
            var bytes = new byte[size];
            Marshal.Copy(block, bytes, 0, (int)size);
            NativeMethods.az_free(block); // release the engine-allocated block
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(dataSlot);
            Marshal.FreeHGlobal(sizeSlot);
        }
    }

    private void CaptureWarnings()
    {
        // az_writer_finish returns AZ_OK but leaves the "settings not honoured" text
        // in the thread-local slot; read it before any other az_* call can clear it.
        var message = NativeMethods.LastErrorMessage();
        if (string.IsNullOrWhiteSpace(message))
            return;
        foreach (var part in message.Split(new[] { ". " }, StringSplitOptions.RemoveEmptyEntries))
            _warnings.Add(part.TrimEnd('.') + ".");
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ArchiveWriter), "the writer was already freed (az_writer_free)");
        if (_finished)
            throw new AndzipException(AzResult.ErrorAlreadyDone, "writer", "this writer was already finished; open a new one");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        NativeMethods.az_writer_free(_writer); // idempotent finish/cancel of the native handle
        _ownedArgs?.Dispose();                // only safe once the engine is done with it
    }
}
