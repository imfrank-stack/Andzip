namespace Andzip.Native;

/// <summary>
/// One progress report from the engine. For extract/test the byte counter is packed
/// bytes read; for compress it is source bytes consumed (see the ABI's
/// <c>az_progress</c> comment). The _out pair is filled only when both directions
/// are known.
/// </summary>
public sealed record ProgressInfo(
    AzOp Op,
    string? CurrentEntry,
    ulong EntryIndex,
    ulong EntryCount,
    ulong BytesIn,
    ulong TotalIn,
    ulong BytesOut,
    ulong TotalOut)
{
    /// <summary>Completion in 0..1 when the total is known, otherwise null.</summary>
    public double? Fraction => TotalIn > 0 ? Math.Min(1.0, (double)BytesIn / TotalIn) : null;
}

/// <summary>A detected container format. Snapshot of the native
/// <c>az_format_info</c>; the backing <c>_bag</c> is released before this is returned.</summary>
public sealed record FormatInfo(
    uint Index,
    string Name,
    string? Extension,
    string? AddExtension,
    bool CanRead,
    bool CanWrite,
    uint Flags);

/// <summary>A codec the build exposes. Snapshot of the native <c>az_codec_info</c>;
/// <see cref="IdHex"/> is the lower-case method id (e.g. "21" for LZMA2).</summary>
public sealed record CodecInfo(
    uint Index,
    string Name,
    string IdHex,
    bool CanEncode,
    bool CanDecode,
    bool IsFilter);

/// <summary>An archive member. Detached snapshot of <c>az_entry_info</c>; the native
/// <c>_bag</c> is released the moment this is materialised, so it is safe to keep.</summary>
public sealed record ArchiveEntry(
    ulong Index,
    string Name,
    string? Method,
    ulong? Size,
    ulong? PackedSize,
    uint Crc,
    bool IsDirectory,
    bool IsEncrypted,
    bool IsAnti,
    uint Attributes,
    DateTimeOffset? ModifiedTime,
    uint PosixMode,
    string? Comment,
    ulong NumberOfSubFiles)
{
    /// <summary>True when the format stored an uncompressed size.</summary>
    public bool HasSize => Size.HasValue;
}
