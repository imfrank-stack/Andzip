using System.Runtime.InteropServices;

namespace Andzip.Native.Interop;

/// <summary>Builders that turn a managed option record into a native struct block plus
/// its UTF-8 string buffers and callback bridge, all freed on dispose. Pass
/// <see cref="Ptr"/> to the P/Invoke, or <see cref="IntPtr.Zero"/> for native defaults.
/// Lifetime: a block handed to a one-shot call (extract, test, add) must outlive that
/// call. A block handed to a call that mints a <b>handle</b> (az_archive_open,
/// az_writer_open) must outlive the handle -- the engine copies the callback pointers
/// out of it and calls back during finish/extract, so ownership passes to the handle
/// object and it is freed only in that object's Dispose.</summary>
internal sealed class NativeArgs : IDisposable
{
    private readonly IntPtr _block;
    private readonly Utf8.Scope _strings;
    private readonly CallbackBridge? _bridge;

    public IntPtr Ptr => _block;

    private static IntPtr Alloc<T>(in T value) where T : struct
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, p, fDeleteOld: false);
        return p;
    }

    public static NativeArgs Open(OpenOptions o)
    {
        var strings = new Utf8.Scope();
        var bridge = MakeBridge(o.Progress, o.PasswordPrompt);
        var s = new AzOpenOptions
        {
            StructSize = (uint)Marshal.SizeOf<AzOpenOptions>(),
            Password = strings.Add(o.Password),
            ForcedFormat = strings.Add(o.ForcedFormat),
            Callbacks = bridge?.Handle ?? IntPtr.Zero,
            HeadersOnly = o.HeadersOnly ? 1 : 0,
        };
        return new NativeArgs(Alloc(s), strings, bridge);
    }

    public static NativeArgs Extract(ExtractOptions o, bool testOnly)
    {
        var strings = new Utf8.Scope();
        var bridge = MakeBridge(o.Progress, o.PasswordPrompt);
        var s = new AzExtractOptions
        {
            StructSize = (uint)Marshal.SizeOf<AzExtractOptions>(),
            Password = strings.Add(o.Password),
            Callbacks = bridge?.Handle ?? IntPtr.Zero,
            Overwrite = (int)o.Overwrite,
            PreserveMTime = o.PreserveMTime ? 1 : 0,
            TestOnly = testOnly ? 1 : 0,
            Flatten = o.Flatten ? 1 : 0,
        };
        return new NativeArgs(Alloc(s), strings, bridge);
    }

    public static NativeArgs Create(CreateOptions o)
    {
        var strings = new Utf8.Scope();
        var bridge = MakeBridge(o.Progress, o.PasswordPrompt);
        var s = new AzCreateOptions
        {
            StructSize = (uint)Marshal.SizeOf<AzCreateOptions>(),
            Format = strings.Add(o.Format),
            Method = strings.Add(o.Method),
            Level = o.Level,
            DictSizeMib = o.DictSizeMib,
            WordSize = o.WordSize,
            NumThreads = o.NumThreads,
            MemoryLimitMib = o.MemoryLimitMib,
            Password = strings.Add(o.Password),
            Encryption = (int)o.Encryption,
            EncryptHeaders = o.EncryptHeaders ? 1 : 0,
            Solid = o.Solid ? 1 : 0,
            MultiThread = o.MultiThread ? 1 : 0,
            VolumeSize = strings.Add(o.VolumeSize),
            Comment = strings.Add(o.Comment),
            Callbacks = bridge?.Handle ?? IntPtr.Zero,
            ExtraProperties = strings.Add(o.ExtraProperties),
            Preallocate = o.Preallocate ? 1 : 0,
            ShareLocked = o.ShareLocked ? 1 : 0,
        };
        return new NativeArgs(Alloc(s), strings, bridge);
    }

    public static NativeArgs Meta(ItemMeta m)
    {
        var s = new AzItemMeta
        {
            StructSize = (uint)Marshal.SizeOf<AzItemMeta>(),
            MTimeUnix = m.MTimeUnix,
            CTimeUnix = m.CTimeUnix,
            ATimeUnix = m.ATimeUnix,
            Attributes = m.Attributes,
            PosixMode = m.PosixMode,
            IsDir = m.IsDir,
        };
        return new NativeArgs(Alloc(s), new Utf8.Scope(), null);
    }

    private static CallbackBridge? MakeBridge(Action<ProgressInfo>? progress, Func<string?>? prompt)
        => (progress is not null || prompt is not null) ? new CallbackBridge(progress, prompt) : null;

    // Overload-friendly ctor that adopts a caller-built string scope.
    private NativeArgs(IntPtr block, Utf8.Scope strings, CallbackBridge? bridge)
    {
        _block = block;
        _strings = strings;
        _bridge = bridge;
    }

    public void Dispose()
    {
        if (_block != IntPtr.Zero) Marshal.FreeHGlobal(_block);
        _strings.Dispose();
        _bridge?.Dispose();
    }
}
