using System.Buffers;
using System.Runtime.InteropServices;

namespace Andzip.Native.Interop;

/// <summary>
/// Pins a <see cref="ReadOnlyMemory{Byte}"/> and exposes its address as an
/// <see cref="IntPtr"/> without needing <c>unsafe</c> (MemoryHandle.Pointer is an
/// <c>void*</c>). Prefers pinning the backing array in place; only copies when the
/// memory has no usable single-array segment. Dispose releases the pin.
/// </summary>
internal readonly struct PinnedMemory : IDisposable
{
    private readonly GCHandle _handle;
    public IntPtr Pointer { get; }
    public int Length { get; }

    private PinnedMemory(GCHandle handle, IntPtr ptr, int length)
    {
        _handle = handle;
        Pointer = ptr;
        Length = length;
    }

    public static PinnedMemory Create(ReadOnlyMemory<byte> memory)
    {
        if (memory.Length == 0)
            return new PinnedMemory(default, IntPtr.Zero, 0);

        if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment)
            && segment.Array is not null && segment.Offset == 0 && segment.Count == memory.Length)
        {
            var h = GCHandle.Alloc(segment.Array, GCHandleType.Pinned);
            return new PinnedMemory(h, h.AddrOfPinnedObject(), memory.Length);
        }

        var copy = memory.ToArray();
        var ch = GCHandle.Alloc(copy, GCHandleType.Pinned);
        return new PinnedMemory(ch, ch.AddrOfPinnedObject(), copy.Length);
    }

    public void Dispose()
    {
        if (_handle.IsAllocated)
            _handle.Free();
    }
}
