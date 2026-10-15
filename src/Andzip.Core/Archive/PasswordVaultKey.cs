using System.Security.Cryptography;
using System.Text;

namespace Andzip.Core.Archive;

/// <summary>
/// 把"哪个压缩包"变成"凭据存储里的那一条"的键。
/// <para><strong>为什么哈希而不是直接把路径当键</strong>：凭据的目标名会出现在
/// Windows「凭据管理器」的界面里，也会出现在任何一次枚举结果的字符串里。
/// 直接写路径等于把"这台机器上哪些包是加密的"暴露给每一个能读到凭据列表的东西 ——
/// 而那本身就已经是一句关于用户的信息。口令要藏，"藏了口令的到底是哪个文件"同样不该摊开。</para>
/// <para><strong>路径先归一化再哈希</strong>，规则与 <see cref="PasswordCache"/> 那本内存字典必须一致
/// （完整路径 + 去掉尾分隔符 + 忽略大小写）：两边不一致的话，同一个包会被存成两条，
/// 用户删掉一条之后仍然能凭另一条打开 —— 那比只存一条更糟，因为"我已经删了"是假的。</para>
/// </summary>
public static class PasswordVaultKey
{
    /// <summary>
    /// 目标名的前缀。枚举时按这个前缀过滤，就只会拿到本程序自己存的那些 ——
    /// 「全部删除」绝不能顺手删掉别的软件存在同一个凭据管理器里的东西。
    /// </summary>
    public const string Prefix = "Andzip/archive/";

    /// <summary>某个压缩包对应的凭据目标名。</summary>
    public static string For(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(archivePath))));
    }

    /// <summary>
    /// 归一化成"同一个包的唯一写法"：绝对路径、去掉末尾的 <c>\</c> 或 <c>/</c>、转大写。
    /// <para>转大写而不是直接忽略：Windows 文件系统本来就不区分大小写，
    /// <c>C:\Box.zip</c> 与 <c>c:\box.ZIP</c> 是同一个包，必须落到同一个键上。</para>
    /// </summary>
    public static string Normalize(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return Path.GetFullPath(archivePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
    }

    /// <summary>这条目标名是不是本程序存的（枚举结果里可能混进别的前缀）。</summary>
    public static bool BelongsToUs(string? targetName)
        => targetName is not null && targetName.StartsWith(Prefix, StringComparison.Ordinal);
}
