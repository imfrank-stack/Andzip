namespace Andzip.Native;

/// <summary>Options for opening an archive. Defaults mirror <c>AZ_OPEN_OPTIONS_INIT</c>.
/// When <see cref="PasswordPrompt"/> is set it is wired to the native
/// <c>az_password_fn</c> callback and may be invoked on a decoder worker thread.</summary>
public sealed record OpenOptions
{
    public string? Password { get; init; }
    /// <summary>Force a canonical format name instead of auto-detecting from content.</summary>
    public string? ForcedFormat { get; init; }
    /// <summary>Parse only headers (faster, sizes may be unknown).</summary>
    public bool HeadersOnly { get; init; }
    public Action<ProgressInfo>? Progress { get; init; }
    public Func<string?>? PasswordPrompt { get; init; }
}

/// <summary>Options for extracting. Defaults mirror <c>AZ_EXTRACT_OPTIONS_INIT</c>
/// (overwrite on, preserve mtime on).</summary>
public sealed record ExtractOptions
{
    public string? Password { get; init; }
    public AzOverwrite Overwrite { get; init; } = AzOverwrite.Overwrite;
    public bool PreserveMTime { get; init; } = true;
    /// <summary>Write every entry flat into the destination directory.</summary>
    public bool Flatten { get; init; }
    public Action<ProgressInfo>? Progress { get; init; }
    public Func<string?>? PasswordPrompt { get; init; }
}

/// <summary>Options for creating an archive. Defaults mirror <c>AZ_CREATE_OPTIONS_INIT</c>
/// (7z, default level, solid, multi-thread).</summary>
public sealed record CreateOptions
{
    public string Format { get; init; } = "7z";
    public string? Method { get; init; }
    /// <summary>0..9, or -1 for the format default.</summary>
    public int Level { get; init; } = -1;
    public uint DictSizeMib { get; init; }
    /// <summary>PPMd order (2..64) or LZMA lc (0..4); 0 = default.</summary>
    public uint WordSize { get; init; }
    /// <summary>0 = fall back to <see cref="AndzipCore.NumThreads"/>.</summary>
    public uint NumThreads { get; init; }
    /// <summary>0 = fall back to <see cref="AndzipCore.MemoryLimitMiB"/>.</summary>
    public ulong MemoryLimitMib { get; init; }
    public string? Password { get; init; }
    public AzEncryption Encryption { get; init; } = AzEncryption.None;
    /// <summary>7z only: also encrypt the file list.</summary>
    public bool EncryptHeaders { get; init; }
    /// <summary>7z only: one solid block.</summary>
    public bool Solid { get; init; } = true;
    public bool MultiThread { get; init; } = true;
    /// <summary>
    /// Volume size spec such as "100M" (also "700m", "4G", or bare bytes). 7z only:
    /// the engine writes <c>name.7z.001, .002, ...</c> as it goes, so nothing is staged
    /// as a single file first. Below 1 MiB, or for any other format,
    /// <c>az_writer_open</c> fails rather than ignoring the request. Null = one file.
    /// </summary>
    public string? VolumeSize { get; init; }
    public string? Comment { get; init; }
    /// <summary>Newline-separated "name=value" passthrough, e.g. "mf=bt4\nfb=273".</summary>
    public string? ExtraProperties { get; init; }
    /// <summary>Reserve the output file's clusters before writing (see the native note:
    /// the reservation is the uncompressed total, an upper bound, released on failure to
    /// fit rather than by failing the write). No effect when writing to memory.</summary>
    public bool Preallocate { get; init; }
    /// <summary>Open members sharing write/delete, so other programs are not locked out
    /// of the sources while we compress. It does <b>not</b> let us read a file another
    /// process holds exclusively — nothing does (ERROR_SHARING_VIOLATION is returned even
    /// with backup semantics); such members are skipped and reported as a warning.</summary>
    public bool ShareLocked { get; init; }
    public Action<ProgressInfo>? Progress { get; init; }
    public Func<string?>? PasswordPrompt { get; init; }
}

/// <summary>Per-item metadata attached when adding to an archive. Zero fields mean
/// "derive from the source"; mirrors <c>AZ_ITEM_META_INIT</c> (is_dir defaults to -1,
/// derive).</summary>
public sealed record ItemMeta
{
    public long MTimeUnix { get; init; }
    public long CTimeUnix { get; init; }
    public long ATimeUnix { get; init; }
    public uint Attributes { get; init; }
    public uint PosixMode { get; init; }
    /// <summary>-1 = derive from source, 0 = file, 1 = directory.</summary>
    public int IsDir { get; init; } = -1;
}
