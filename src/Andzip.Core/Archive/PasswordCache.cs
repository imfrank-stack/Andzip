namespace Andzip.Core.Archive;

/// <summary>
/// 口令记忆：<strong>两层</strong> —— 本次运行的内存，和（可选的）跨重启的系统凭据存储。
/// <para>
/// 第一层从来没有变过：同一个包不必反复输，进程退出就没了。第二层是 3.0.0 补的
/// 「工具 → 密码管理器」，它<strong>只</strong>能落在操作系统的凭据保护上
/// （Windows 上是凭据管理器，由 LSASS 加密后落盘）。
/// </para>
/// <para>
/// <strong>口令绝不进 <c>settings.ini</c>、不进注册表、不进日志</strong> —— 这条从 0.9.x 就在，
/// 有了第二层之后仍然一个字不改：第二层是一个独立的存储，不是设置文件里多出来的一个键。
/// 把口令明文写进设置文件不是"简化版口令管理器"，是一个谁都会踩的坑。
/// </para>
/// <para>
/// 读的顺序是<strong>先内存、再持久</strong>，命中之后把值提升到内存：
/// 这样同一个包在这一次运行里只会真读一次凭据，而不是每次解压都去问一遍系统。
/// </para>
/// <para>
/// 键用<strong>完整路径</strong>并忽略大小写：Windows 文件系统本来就不区分大小写，
/// 区分了会对同一个包问两次口令。第二层的键由 <see cref="PasswordVaultKey"/> 归一化，
/// 规则与这里的字典必须一致（否则"删掉一条"删不干净）。
/// </para>
/// </summary>
public sealed class PasswordCache
{
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 总开关。关掉时 <see cref="Remember"/> 什么都不做、<see cref="TryGet"/> 永远返回 false ——
    /// <strong>连持久层也不读</strong>：用户说"别记我的口令"，那就一条都不给，
    /// 而不是"内存里不记但系统凭据里照用"。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 跨重启那一层。null 表示这台机器上没有（非 Windows，或者根本没实现），
    /// 此时 <see cref="PersistPasswords"/> 开着也不会写任何东西。
    /// </summary>
    public IPersistentPasswordStore? Persistent { get; set; }

    /// <summary>
    /// 要不要把口令写进 <see cref="Persistent"/>。关掉它应当<strong>连带清空</strong>持久层 ——
    /// 这件事由调用方（设置 → 安全那一页）负责，理由与内存那一层同一句：
    /// 用户关掉"记住口令"之后以为没了、其实还在，是最糟的一种"没生效"。
    /// </summary>
    public bool PersistPasswords { get; set; }

    /// <summary>本次运行内存里已记住的包数（设置页上显示，让用户知道"清掉"清的是什么）。</summary>
    public int Count => _passwords.Count;

    public IReadOnlyCollection<string> KnownArchives => _passwords.Keys;

    private bool PersistentUsable => Enabled && PersistPasswords && Persistent is { IsSupported: true };

    /// <summary>
    /// 记住一个口令：先进内存，开了持久层再写一份。
    /// </summary>
    /// <returns>
    /// null 表示一切正常；否则是一句<strong>可以直接显示给用户</strong>的原因 ——
    /// 内存那份已经记上了，但持久层没收（口令太长、系统拒绝写入）。
    /// 回一句而不是闷声吞掉，是因为界面上"已经保存"和"只在这次运行里记得"是两件事。
    /// </returns>
    public string? Remember(string archivePath, string? password)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var full = Path.GetFullPath(archivePath);
        _passwords[full] = password;

        if (!PersistentUsable)
        {
            return null;
        }

        var store = Persistent!;
        if (store.Rejects(password) is { } refused)
        {
            return refused;
        }

        return store.Save(full, password);
    }

    public bool TryGet(string archivePath, out string password)
    {
        password = string.Empty;
        if (!Enabled || string.IsNullOrWhiteSpace(archivePath))
        {
            return false;
        }

        var full = Path.GetFullPath(archivePath);
        if (_passwords.TryGetValue(full, out password!))
        {
            return true;
        }

        if (!PersistentUsable || !Persistent!.TryGet(full, out password))
        {
            password = string.Empty;
            return false;
        }

        // 提升到内存：这一次运行里之后再来开同一个包，不必再问一遍系统。
        _passwords[full] = password;
        return true;
    }

    /// <summary>忘掉某个包。<strong>两层都清</strong> —— 只清内存的话，下次启动它又回来了。</summary>
    public void Forget(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        var full = Path.GetFullPath(archivePath);
        _passwords.Remove(full);

        if (Enabled && PersistPasswords && Persistent is { IsSupported: true } store)
        {
            store.Remove(full);
        }
    }

    /// <summary>
    /// 清掉<strong>内存</strong>那一层。持久层不动 —— 它有自己的入口（密码管理器里的「全部删除」，
    /// 以及关掉那个开关时设置页显式做的清空）。把两者合成一个动作，
    /// 用户点「清除本次运行的口令」就会顺手删掉他本来想留下的东西。
    /// </summary>
    public void Clear() => _passwords.Clear();
}
