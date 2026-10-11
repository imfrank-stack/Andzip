namespace Andzip.Core.Archive;

/// <summary>
/// 文件名通配匹配，用于「设置 → 压缩 → 压缩时要排除的文件类型」。
/// <para>
/// 只有两条规则：<c>*</c> 匹配任意个字符（含零个），<c>?</c> 匹配恰好一个，大小写不敏感。
/// 刻意<strong>不</strong>照抄 Win32 的那几个历史怪癖（<c>*.txt</c> 会连没有扩展名的
/// <c>TXT</c> 一起匹配上、末尾的点被吞掉）—— 那套规则来自 8.3 文件名时代，普通用户照着
/// 「<c>*.bak; *.tmp;</c>」这个格式写模式时，预期就是朴素的 glob，按 Win32 语义反而会
/// 多排除掉他没点名的文件。
/// </para>
/// </summary>
public static class FileNamePattern
{
    /// <summary>
    /// 整个模式是否匹配整个文件名（不是"包含"，所以 <c>*.bak</c> 不会吃掉 <c>x.bak.txt</c>）。
    /// 模式非法（null / 空）时返回 false —— 空模式不该把所有文件都排除掉。
    /// </summary>
    public static bool IsMatch(string fileName, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        // 逐段推进，星号处记下"回溯点"：这是线性时间的经典 glob 匹配，
        // 比把模式翻译成正则更省，也不会让用户写的模式变成一次正则注入。
        var (name, expr) = (0, 0);
        var (star, mark) = (-1, 0);

        while (name < fileName.Length)
        {
            if (expr < pattern.Length
                && (pattern[expr] == '?' || char.ToUpperInvariant(pattern[expr]) == char.ToUpperInvariant(fileName[name])))
            {
                expr++;
                name++;
            }
            else if (expr < pattern.Length && pattern[expr] == '*')
            {
                star = expr;
                mark = name;
                expr++;
            }
            else if (star >= 0)
            {
                expr = star + 1;
                name = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (expr < pattern.Length && pattern[expr] == '*')
        {
            expr++;
        }

        return expr == pattern.Length;
    }

    /// <summary>任一模式匹配即为真。空集合返回 false。</summary>
    public static bool MatchesAny(IReadOnlyList<string> patterns, string fileName)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(fileName, pattern))
            {
                return true;
            }
        }

        return false;
    }
}
