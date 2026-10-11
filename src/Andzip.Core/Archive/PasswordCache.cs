namespace Andzip.Core.Archive;

/// <summary>
/// 本次运行内的口令记忆：同一个压缩包不必反复输。
/// <para>
/// 刻意<strong>只在内存里</strong>，进程退出就没了。参考软件那个"口令管理器"是跨重启保存的，
/// 而要做对就得用操作系统的凭据保护（Windows 上是 DPAPI）—— 那需要一个 Windows 专用的
/// NuGet 包，而这个项目还要在 Linux 上构建。把口令写成明文放进 <c>settings.ini</c>
/// 不是"简化版"，是一个谁都会踩的安全坑，所以宁可少做一层并说清楚。
/// </para>
/// <para>
/// 键用<strong>完整路径</strong>并忽略大小写：Windows 文件系统本来就不区分大小写，
/// 区分了会对同一个包问两次口令。
/// </para>
/// </summary>
public sealed class PasswordCache
{
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>关掉时 <see cref="Remember"/> 什么都不做，<see cref="TryGet"/> 永远返回 false。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>已记住的包数（设置页上显示，让用户知道"清掉"清的是什么）。</summary>
    public int Count => _passwords.Count;

    public IReadOnlyCollection<string> KnownArchives => _passwords.Keys;

    public void Remember(string archivePath, string? password)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrEmpty(password))
        {
            return;
        }

        _passwords[Path.GetFullPath(archivePath)] = password;
    }

    public bool TryGet(string archivePath, out string password)
    {
        password = string.Empty;
        if (!Enabled || string.IsNullOrWhiteSpace(archivePath))
        {
            return false;
        }

        return _passwords.TryGetValue(Path.GetFullPath(archivePath), out password!);
    }

    public void Forget(string archivePath)
    {
        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            _passwords.Remove(Path.GetFullPath(archivePath));
        }
    }

    public void Clear() => _passwords.Clear();
}
