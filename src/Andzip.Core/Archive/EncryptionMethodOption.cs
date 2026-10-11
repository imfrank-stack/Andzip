namespace Andzip.Core.Archive;

/// <summary>
/// 加密算法类别。Core 自己的枚举 —— 引擎侧的 <c>AzEncryption</c> 属于 P/Invoke 细节，
/// 不能出现在 GUI 必须理解的接口上。映射在 <c>NativeEngine</c> 里完成。
/// </summary>
public enum ArchiveEncryption
{
    /// <summary>不加密。</summary>
    None,

    /// <summary>由引擎按格式选默认算法。</summary>
    Default,

    /// <summary>ZIP 专属：ZipCrypto，弱，仅为兼容旧包保留。</summary>
    ZipCrypto,

    /// <summary>ZIP 的 WinZip AE-2 / 7z 的 AES-256 + SHA-256 KDF。</summary>
    Aes256,
}

/// <summary>
/// 加密算法选项（FR-2.5；仅在设置了密码时生效）。
/// </summary>
public sealed record EncryptionMethodOption(int Id, string TextKey, ArchiveEncryption Kind)
{
    public static readonly EncryptionMethodOption ZipCrypto =
        new(0, "TEXT_ENCRYPTIONMETHOD_ZIPCRYPTO", ArchiveEncryption.ZipCrypto);

    public static readonly EncryptionMethodOption Aes256 =
        new(1, "TEXT_ENCRYPTIONMETHOD_AES256", ArchiveEncryption.Aes256);

    /// <summary>兼容旧调用点：ZIP 的默认（对齐参考软件实测的 ZipCrypto）。</summary>
    public static EncryptionMethodOption Default { get; } = ZipCrypto;

    public static IReadOnlyList<EncryptionMethodOption> All { get; } = [ZipCrypto, Aes256];

    /// <summary>该格式能写的加密算法。7z 只有 AES-256；不加密密码的格式返回空列表。</summary>
    public static IReadOnlyList<EncryptionMethodOption> ForFormat(ArchiveFormatOption format)
        => format switch
        {
            { SupportsPassword: false } => [],
            { Id: "7Z" } => [Aes256],
            _ => [ZipCrypto, Aes256],
        };

    /// <summary>是否为 WinZip AES（密钥长度由引擎固定为 256 位）。</summary>
    public bool IsAes => Kind == ArchiveEncryption.Aes256;

    public static EncryptionMethodOption FromId(int id)
        => All.FirstOrDefault(o => o.Id == id)
           ?? throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown encryption method id.");
}
