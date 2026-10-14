namespace Andzip.Core.Archive;

/// <summary>
/// 在当前压缩包清单里按名字找成员（「查找 → 文件查找」的数据侧）。
/// <para>放在 Core 而不是写在窗口的 <c>TextChanged</c> 里，是因为这三条规则在界面上都看不出来、
/// 却都会被人改坏：匹配的是<strong>完整路径</strong>（找 <c>readme</c> 应当连
/// <c>docs/readme.md</c> 一起出来，只比文件名会让深层的包"搜不到"）、
/// <strong>不区分大小写</strong>、<strong>空查询不返回整份清单</strong>
/// （整份清单主列表里已经摆着了，搜索框空着时再抄一遍只是噪声）。</para>
/// </summary>
public static class ArchiveSearch
{
    /// <summary>
    /// 挑出路径里含有 <paramref name="query"/> 的成员，按路径字典序排。
    /// <para>排序与主列表当前的排序<strong>无关</strong> —— 查找窗口是一处独立的读数，
    /// 结果顺序跟着上一个包的排序列走会让人以为"同一个词两次搜出来不一样"。</para>
    /// </summary>
    public static IReadOnlyList<ArchiveEntryInfo> Find(ArchiveListing listing, string query)
    {
        var text = query.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        return [.. listing.Entries
            .Where(entry => entry.Path.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)];
    }
}
