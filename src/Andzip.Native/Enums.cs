namespace Andzip.Native;

/// <summary>
/// Result codes returned by every native call (mirrors <c>az_result</c>).
/// Anything other than <see cref="Ok"/> is surfaced by the shell as a thrown
/// <see cref="AndzipException"/> carrying this value plus the native message.
/// </summary>
public enum AzResult
{
    Ok = 0,
    /// <summary>Engine reported a failure; the native message has the detail.</summary>
    ErrorGeneric = 1,
    /// <summary>A bad argument — most often a struct whose <c>struct_size</c> was not set.</summary>
    ErrorInvalidArg = 2,
    /// <summary>File or archive entry does not exist.</summary>
    ErrorNotFound = 3,
    /// <summary>Underlying read/write failure.</summary>
    ErrorIo = 4,
    ErrorOutOfMemory = 5,
    /// <summary>Format or method not supported by this build (e.g. split-volume output).</summary>
    ErrorUnsupported = 6,
    /// <summary>Not an archive, or corrupt.</summary>
    ErrorBadFormat = 7,
    /// <summary>Missing or wrong password.</summary>
    ErrorPassword = 8,
    /// <summary>Data integrity check failed.</summary>
    ErrorCrc = 9,
    /// <summary>A callback asked the operation to stop.</summary>
    ErrorCancelled = 10,
    /// <summary>Handle already finished/closed.</summary>
    ErrorAlreadyDone = 11,
    /// <summary>Caller buffer too small — distinguishable via <see cref="AndzipException.IsBufferTooSmall"/>.</summary>
    ErrorBufferSmall = 12,
    ErrorInternal = 13,
}

/// <summary>Operation a progress callback is reporting on (mirrors <c>az_op</c>).</summary>
public enum AzOp
{
    Scan = 0,
    Compress = 1,
    Extract = 2,
    Test = 3,
    Hash = 4,
}

/// <summary>Conflict policy when extracting over existing files (mirrors <c>az_overwrite</c>).</summary>
public enum AzOverwrite
{
    /// <summary>Not implemented natively; treated as an error.</summary>
    Ask = 0,
    Overwrite = 1,
    Skip = 2,
    /// <summary>Appends ~1, ~2, ... to avoid a clash.</summary>
    Rename = 3,
    Fail = 4,
}

/// <summary>Encryption selection for <see cref="ArchiveWriter"/> (mirrors <c>az_encryption</c>).</summary>
public enum AzEncryption
{
    None = 0,
    /// <summary>AES-256 for 7z, AES-256 (WinZip AE) for zip.</summary>
    Default = 1,
    /// <summary>zip only; weak, kept for compatibility.</summary>
    ZipCrypto = 2,
    /// <summary>zip: WinZip AE-2, 7z: AES-256 + SHA-256 KDF.</summary>
    Aes256 = 3,
    /// <summary>7z only.</summary>
    SevenZAes = 4,
}
