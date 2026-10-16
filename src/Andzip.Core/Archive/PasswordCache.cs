namespace Andzip.Core.Archive;

/// <summary>
/// 口令记忆：<strong>只在本次运行的内存里</strong>，进程退出就没了。
/// <para>同一个包不必反复输口令，这是这一层存在的唯一理由。3.0.0 起这里做过第二层
/// （跨重启、落在 Windows 凭据管理器上，配一扇「工具 → 密码管理器」），4.3.0 起整层撤掉
/// —— 撤的不是"少一个功能"，是"替用户保管口令这件事超出一个解压工具该承担的范围"。</para>
/// <para>
/// <strong>口令绝不进 <c>settings.ini</c>、不进注册表、不进日志</strong> —— 这条从 0.9.x 就在，
/// 撤掉第二层之后它反而更好守：现在整份存储就是一个进程内的字典，退出即散，
/// 没有哪条路能把口令写到盘上。
/// </para>
/// <para>
/// 键用<strong>完整路径</strong>并忽略大小写：Windows 文件系统本来就不区分大小写，
/// 区分了会对同一个包问两次口令。
/// </para>
/// </summary>
public sealed class PasswordCache
{
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>总开关。关掉时 <see cref="Remember"/> 什么都不做、<see cref="TryGet"/> 永远返回 false。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>内存里已记住的包数（设置页上显示，让用户知道"清掉"清的是什么）。</summary>
    public int Count => _passwords.Count;

    public IReadOnlyCollection<string> KnownArchives => _passwords.Keys;

    /// <summary>记住一个口令。关掉总开关、路径为空、口令为空时什么都不做。</summary>
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

    /// <summary>忘掉某个包。</summary>
    public void Forget(string archivePath)
    {
        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            _passwords.Remove(Path.GetFullPath(archivePath));
        }
    }

    /// <summary>清掉全部（界面上那句「清除本次运行的口令」）。</summary>
    public void Clear() => _passwords.Clear();
}
