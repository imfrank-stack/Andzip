using System.Runtime.InteropServices;
using System.Text;

namespace Andzip.Native.Interop;

/// <summary>
/// UTF-8 marshalling for the C ABI, whose every string is a NUL-terminated
/// <c>const char*</c>. Inputs go through <see cref="Marshal.StringToCoTaskMemUTF8"/>
/// (never the ANSI path, which would mangle non-ASCII on some platforms); outputs
/// are read back with <see cref="Marshal.PtrToStringUTF8(nint)"/>.
/// </summary>
internal static class Utf8
{
    /// <summary>Encodes <paramref name="s"/> to a fresh CoTaskMem UTF-8 buffer, or
    /// <see cref="IntPtr.Zero"/> for null. Caller must free with <see cref="Marshal.FreeCoTaskMem"/>.</summary>
    public static IntPtr Encode(string? s)
        => s is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(s);

    /// <summary>Decodes a NUL-terminated UTF-8 buffer; returns null for <see cref="IntPtr.Zero"/>.</summary>
    public static string? Decode(IntPtr ptr)
        => ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);

    /// <summary>Collects native UTF-8 allocations so they are freed exactly once
    /// when the owning call returns.</summary>
    internal sealed class Scope : IDisposable
    {
        private readonly List<IntPtr> _ptrs = new();

        /// <summary>Encodes and registers the buffer for release on dispose.</summary>
        public IntPtr Add(string? s)
        {
            var p = Encode(s);
            if (p != IntPtr.Zero) _ptrs.Add(p);
            return p;
        }

        public void Dispose()
        {
            foreach (var p in _ptrs) Marshal.FreeCoTaskMem(p);
            _ptrs.Clear();
        }
    }
}
