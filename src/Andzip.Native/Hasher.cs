using System.Runtime.InteropServices;
using Andzip.Native.Interop;

namespace Andzip.Native;

/// <summary>
/// Streaming hash helper over <c>az_hasher_*</c> ("CRC32", "SHA256", "BLAKE2sp", "XXH64").
/// Feed bytes with <see cref="Update(ReadOnlyMemory{Byte})"/> and finalise with <see cref="Finish"/>.
/// Not thread safe, and not resettable: <see cref="Finish"/> ends the digest.
/// </summary>
public sealed class Hasher : IDisposable
{
    private readonly IntPtr _hasher;
    private bool _disposed;
    private bool _finished;

    private Hasher(IntPtr hasher) => _hasher = hasher;

    /// <summary>Opens a hasher for <paramref name="algorithm"/> via <c>az_hasher_open</c>.</summary>
    public static Hasher Open(string algorithm)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        NativeMethods.EnsureLoaded();
        using var strings = new Utf8.Scope();
        NativeMethods.Check(NativeMethods.az_hasher_open(strings.Add(algorithm), out var h), "az_hasher_open");
        return new Hasher(h);
    }

    public void Update(ReadOnlyMemory<byte> data)
    {
        ThrowIfUnavailable();
        if (data.Length == 0)
            return;
        using var pinned = PinnedMemory.Create(data);
        NativeMethods.az_hasher_update(_hasher, pinned.Pointer, (UIntPtr)pinned.Length);
    }

    public void Update(byte[] data) => Update((ReadOnlyMemory<byte>)data);

    /// <summary>Finalises into a freshly sized digest array (<c>az_hasher_finish</c>).
    /// A 128-byte scratch covers every supported algorithm; the returned array is trimmed
    /// to the byte count the engine reports.</summary>
    public byte[] Finish()
    {
        ThrowIfUnavailable();
        if (_finished)
            throw new AndzipException(AzResult.ErrorAlreadyDone, "az_hasher_finish", "this hasher was already finished");

        var scratch = new byte[128];
        var handle = GCHandle.Alloc(scratch, GCHandleType.Pinned);
        try
        {
            NativeMethods.Check(
                NativeMethods.az_hasher_finish(_hasher, handle.AddrOfPinnedObject(), (UIntPtr)(ulong)scratch.Length, out var writtenU),
                "az_hasher_finish");
            _finished = true;
            var written = (int)writtenU.ToUInt64();
            var digest = new byte[written];
            Array.Copy(scratch, digest, written);
            return digest;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Lower-case hex digest of <see cref="Finish"/>.</summary>
    public string FinishHex()
        => Convert.ToHexString(Finish()).ToLowerInvariant();

    private void ThrowIfUnavailable()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Hasher), "the hasher was already closed (az_hasher_close)");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        NativeMethods.az_hasher_close(_hasher);
    }
}
