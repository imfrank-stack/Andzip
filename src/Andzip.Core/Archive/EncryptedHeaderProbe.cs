namespace Andzip.Core.Archive;

/// <summary>
/// 「这个包其实是加密的，只是引擎没这么说」的探测。
/// <para>
/// 头加密的 7z 在不给口令时，7-Zip 的处理器返回的是 <c>S_FALSE</c>（"这不是我的格式"），
/// 到我们手里就成了 <c>AZ_ERROR_BAD_FORMAT</c> —— 和真正的坏文件、不认识的格式一模一样。
/// 于是界面回的是「该格式不支持」，用户连口令框都等不到，
/// "本次运行中记住用过的口令"这条设置自然永远没机会生效（0.11.2 修的就是这个）。
/// </para>
/// <para>
/// 唯一还能分开的线索是文件自己的魔数：<strong>签名对得上、处理器却说"不是压缩包"</strong>，
/// 那基本就是头部读不出来，也就是缺口令。误判的代价小且自洽 —— 真坏了的 7z 会多弹一次
/// 口令框，输完仍然报原来的错。
/// </para>
/// <para>
/// 只管 7z：ZIP 的中央目录是明文，打开成功、由 <c>IsEncrypted</c> 报出加密；
/// RAR 我们没有可写入的样本，没量过就不下结论。
/// </para>
/// </summary>
public static class EncryptedHeaderProbe
{
    /// <summary>7z 的归档起始签名（<c>7zHeader.cpp</c> 里的 <c>kSignature</c>）。</summary>
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>
    /// 这次失败是不是"其实是缺口令"。只在失败已经被报成"不是压缩包 / 不支持的格式"时才算数，
    /// 而且文件必须以 7z 签名开头 —— 两个条件缺一个就还回 false，不替真正的错误编理由。
    /// </summary>
    public static bool LooksLikeMissingPassword(Exception failure, string archivePath)
        => ReadsAsNotAnArchive(failure) && HasSevenZipSignature(archivePath);

    private static bool ReadsAsNotAnArchive(Exception failure) => failure switch
    {
        ArchiveConstraintException { Reason: ArchiveConstraintReason.FormatNotSupported } => true,
        InvalidDataException => true,
        _ => false,
    };

    /// <summary>文件头六个字节是不是 7z 签名。读不动（不存在、被占、太短）一律算不是。</summary>
    public static bool HasSevenZipSignature(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return false;
        }

        try
        {
            using var file = File.OpenRead(archivePath);
            var head = new byte[SevenZipSignature.Length];
            return file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
                   && head.AsSpan().SequenceEqual(SevenZipSignature);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
