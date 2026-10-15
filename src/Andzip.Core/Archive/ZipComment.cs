using System.Buffers.Binary;
using System.Text;

namespace Andzip.Core.Archive;

/// <summary>
/// ZIP 归档注释（结束记录尾部那一句文字）的读与写。
/// <para><strong>为什么这件事不经过引擎</strong>：7-Zip 的 out-handler 属性表里根本没有归档注释项
/// —— <c>native/src/api/az_writer.cpp</c> 里那条 "archive comments are not supported by the engine"
/// 就是实测记下来的（传一个未知属性名会让整次写入以 <c>E_INVALIDARG</c> 失败）。引擎只在
/// <strong>更新既有的包</strong>时把输入包已有的注释原样带过去，从来没法"设一句新的"。</para>
/// <para>而归档注释在 ZIP 里只是结束记录（EOCD）后面的一串字节，长度记在 EOCD 偏移 20 的那两个字节里。
/// 改它<strong>不碰任何一个成员的数据</strong>，也不动中央目录 —— 于是这里自己做：
/// 定位 EOCD、把那串字节换成新的、改掉长度、把文件尾部截到该在的地方
/// （<see cref="ZipScanner"/> 的 EOCD 查找要求"签名 + 22 + 注释长 == 文件尾"，所以必须截）。</para>
/// <para><strong>写不是原子的</strong>：这仍是就地改文件，不是"写临时文件再替换"——
/// 一个 100 GB 的包为了一句注释复制一遍不可接受。崩溃窗口被压到最小：先写注释正文、再写长度、
/// 最后截断，并在同一个句柄上复验一遍结束记录还读得出来。这条取舍写在测试与开发记录里。</para>
/// <para><strong>为什么不顺手用 <see cref="ArchiveListing.Comment"/></strong>：那是引擎给的，
/// 而引擎（7-Zip 的 <c>ZipHandler.cpp</c>）把注释字节按 <c>CP_ACP</c> 解 —— 我们写进去的 UTF-8 中文
/// 在那里是一串乱码（实测：写成 <c>中文注释</c> 读回来是 <c>涓枃娉ㄩ噴</c>）。
/// 那条判据在 vendor 进来的第三方便码里，而本工程对它的约定是<strong>逐字符原样、不改一处</strong>
/// （见 <c>native/README.md</c>），所以注释这一格由本类自己读字节，谁也不欠谁。</para>
/// </summary>
public static class ZipComment
{
    /// <summary>EOCD 用两个字节记注释长度，所以一句话最多这么长（字节，不是字符）。</summary>
    public const int MaxBytes = 0xFFFF;

    /// <summary>
    /// 这个格式有没有"归档注释"这一格。现在只有 ZIP 有。
    /// <para>界面拿它决定「压缩文件注释」那一项灰不灰 —— 与
    /// <see cref="ArchiveUpdater.CanUpdate"/> 一样，判据只写一份在这里，
    /// 界面里再抄一遍就成了第二份真相。</para>
    /// </summary>
    public static bool CanEdit(string? formatName)
        => formatName is not null && formatName.Equals("zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读归档注释。<strong>没有注释返回 <c>null</c>；根本不是 ZIP 则抛
    /// <see cref="ArchiveConstraintException"/></strong> —— 两者必须分得开，
    /// 否则 7z 会被界面说成"这个包没有注释"，而事实是这个格式压根没有注释这一格。
    /// </summary>
    public static string? Read(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        var map = ZipScanner.Inspect(fullPath);

        if (map.EocdOffset < 0)
        {
            throw new ArchiveConstraintException(ArchiveConstraintReason.ArchiveCommentNotSupported);
        }

        if (map.CommentLength <= 0)
        {
            return null;
        }

        using var stream = File.OpenRead(fullPath);
        stream.Position = map.EocdOffset + 22;
        var bytes = new byte[map.CommentLength];
        stream.ReadExactly(bytes);
        return Decode(bytes);
    }

    /// <summary>
    /// 写归档注释；<paramref name="comment"/> 为 null 或空白就是把注释去掉。
    /// <exception cref="ArchiveConstraintException">不是 ZIP，或注释超过 <see cref="MaxBytes"/> 字节
    /// （<strong>此时文件一个字节都没动</strong>）。</exception>
    /// </summary>
    public static void Write(string archivePath, string? comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        var map = ZipScanner.Inspect(fullPath);

        if (map.EocdOffset < 0)
        {
            throw new ArchiveConstraintException(ArchiveConstraintReason.ArchiveCommentNotSupported);
        }

        var bytes = string.IsNullOrWhiteSpace(comment) ? [] : Encoding.UTF8.GetBytes(comment);
        if (bytes.Length > MaxBytes)
        {
            // 先在这里挡住：写进去一半再报错，等于把一个包弄坏之后再告诉用户。
            throw new ArchiveConstraintException(ArchiveConstraintReason.ArchiveCommentTooLong);
        }

        // FileShare.None：写注释这几毫秒里不许别人同时改这个包（我们是在文件尾部原地改）。
        using var file = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var lengthAt = map.EocdOffset + 20;
        var commentAt = map.EocdOffset + 22;

        // 顺序是有理由的：先写正文、再写长度、最后截断。反过来先声明"我有 N 字节"，
        // 一旦在这两步之间出事，读者会按新长度去读一段还不存在的字节。
        file.Position = commentAt;
        file.Write(bytes);
        file.Position = lengthAt;
        file.Write([(byte)(bytes.Length & 0xFF), (byte)(bytes.Length >> 8)]);
        file.SetLength(commentAt + bytes.Length);
        file.Flush();

        // 复验：这个句柄还没放，别人塞不进来。结束记录读不回来就直说，不假装写成功了。
        var after = ZipScanner.Inspect(file);
        if (after.EocdOffset != map.EocdOffset || after.CommentLength != bytes.Length)
        {
            throw new InvalidDataException(
                $"写完之后这个包的结束记录读不回来（EOCD {after.EocdOffset}，注释 {after.CommentLength} 字节）。");
        }
    }

    /// <summary>
    /// 解码那句注释。先按 UTF-8 严格解（本程序与 Bandizip 都写 UTF-8，纯 ASCII 也走这条），
    /// 解不通才按 Latin-1 逐字节还原。
    /// <para><strong>为什么退回 Latin-1 而不是系统代码页</strong>：ZIP 规范没有给归档注释留编码位，
    /// 编码只能靠猜。Latin-1 是唯一"不丢字节、不抛异常、不把字变成问号"的兜底 ——
    /// 别的工具用系统代码页写的中文注释在我们这里会显示成一串西文，这一点无法可靠解决。
    /// （另外 <c>Encoding.GetEncoding(0)</c> 在 .NET Core 上是 <em>UTF-8</em>，不是"系统当前代码页"：
    /// 实测 <c>GetEncoding(0) == utf-8 (65001)</c>，所以 <c>ZipScanner.DecodeName</c> 里那句兜底
    /// 其实也落在 UTF-8 上。这里不跟着那一套走。）</para>
    /// <para>读取端的不确定性还有一个后果写在调用方：<strong>原文没改就不要写回去</strong> ——
    /// 否则"打开看一眼再点确定"会把别人用系统代码页写的注释改写成 UTF-8。</para>
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
