using System.Runtime.InteropServices;
using System.Text;

namespace Andzip.Native.Interop;

/// <summary>
/// Owns a native <c>az_callbacks</c> block plus the managed delegates it points
/// at. The delegates are stored as instance fields so the GC cannot move or collect
/// them while the engine still holds the function pointers — the classic callback
/// lifetime trap. Dispose frees the block; a bridge must outlive every call that
/// references it.
/// </summary>
internal sealed class CallbackBridge : IDisposable
{
    private readonly IntPtr _block;
    private AzProgressFn? _progress;
    private AzPasswordFn? _password;

    public IntPtr Handle => _block;

    public CallbackBridge(Action<ProgressInfo>? onProgress, Func<string?>? passwordPrompt)
    {
        _block = Marshal.AllocHGlobal(Marshal.SizeOf<AzCallbacks>());

        var cb = new AzCallbacks
        {
            StructSize = (uint)Marshal.SizeOf<AzCallbacks>(),
            Progress = IntPtr.Zero,
            Password = IntPtr.Zero,
            Ctx = IntPtr.Zero,
        };

        // Keep the managed delegate referenced for the bridge's whole lifetime.
        if (onProgress is not null)
        {
            _progress = (ctx, ref p) => InvokeProgress(onProgress, ref p);
            cb.Progress = Marshal.GetFunctionPointerForDelegate(_progress);
        }
        if (passwordPrompt is not null)
        {
            _password = (ctx, buf, cap) => InvokePassword(passwordPrompt, buf, cap);
            cb.Password = Marshal.GetFunctionPointerForDelegate(_password);
        }

        Marshal.StructureToPtr(cb, _block, fDeleteOld: false);
    }

    private static NativeResult InvokeProgress(Action<ProgressInfo> onProgress, ref AzProgressNative p)
    {
        // Native contract: callbacks must never unwind across the ABI boundary.
        // Map an abort to AZ_ERROR_CANCELLED and swallow anything else into a stop.
        try
        {
            onProgress(new ProgressInfo(
                (AzOp)p.Op,
                Utf8.Decode(p.CurrentEntry),
                p.EntryIndex,
                p.EntryCount,
                p.BytesIn,
                p.TotalIn,
                p.BytesOut,
                p.TotalOut));
            return NativeResult.Ok;
        }
        catch (OperationCanceledException)
        {
            return NativeResult.ErrorCancelled;
        }
        catch
        {
            return NativeResult.ErrorCancelled;
        }
    }

    private static int InvokePassword(Func<string?> prompt, IntPtr buffer, UIntPtr capacity)
    {
        var value = prompt();
        if (value is null)
            return 0; // give up → surfaces as AZ_ERROR_PASSWORD

        var cap = capacity.ToUInt64();
        if (cap == 0)
            return 0;

        var bytes = Encoding.UTF8.GetBytes(value);
        var max = (int)Math.Min((ulong)bytes.Length, cap - 1);
        if (max > 0)
            Marshal.Copy(bytes, 0, buffer, max);
        Marshal.WriteByte(buffer, max, 0);
        return 1;
    }

    public void Dispose()
    {
        Marshal.FreeHGlobal(_block);
        _progress = null;
        _password = null;
    }
}
