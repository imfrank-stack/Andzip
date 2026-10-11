using System.Runtime.InteropServices;

namespace Andzip.Native.Interop;

/// <summary>
/// Bridges the C <c>az_write_fn</c> sink used by <c>az_entry_read_stream</c> to a
/// managed <see cref="Stream"/>. The delegate is held as a field so it stays alive
/// for the duration of the synchronous native call.
/// </summary>
internal sealed class StreamSink : IDisposable
{
    private readonly Stream _destination;
    private readonly AzWriteFn _fn; // rooted field — never a local
    private byte[] _scratch = Array.Empty<byte>();
    private long _written;

    public IntPtr FunctionPointer { get; }

    public long BytesWritten => _written;

    public StreamSink(Stream destination)
    {
        _destination = destination;
        _fn = Invoke;
        FunctionPointer = Marshal.GetFunctionPointerForDelegate(_fn);
    }

    private NativeResult Invoke(IntPtr ctx, IntPtr data, UIntPtr size)
    {
        var count = (long)size.ToUInt64();
        if (count == 0)
            return NativeResult.Ok;
        if (count > int.MaxValue)
            return NativeResult.ErrorBufferSmall; // chunk larger than a Stream write allows

        var len = (int)count;
        if (_scratch.Length < len)
            Array.Resize(ref _scratch, len);
        Marshal.Copy(data, _scratch, 0, len);
        _destination.Write(_scratch, 0, len);
        _written += len;
        return NativeResult.Ok;
    }

    public void Dispose() { /* delegate is collected with this instance */ }
}
