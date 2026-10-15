namespace Andzip.Core.Archive;

/// <summary>
/// 一条<strong>跨重启</strong>保存的口令记录。
/// <para>刻意<strong>不含口令本身</strong>：这份清单是要显示在屏幕上给人看的
/// （「工具 → 密码管理器」那一扇），把口令一起带出来就等于把它画进了截图、
/// 也画进了任何一次异常日志。管理器只需要知道"哪个包存过"，不需要知道"存的是什么"。</para>
/// </summary>
/// <param name="ArchivePath">当时保存的那个压缩包的完整路径。</param>
/// <param name="SavedAt">最后一次写入的时间。</param>
public sealed record PasswordVaultEntry(string ArchivePath, DateTimeOffset SavedAt);

/// <summary>
/// 口令的<strong>第二层</strong>存储：跨进程、跨重启还在的那一种。
/// <para>接口放在 Core、实现放在 App 的原因很直接 —— 这一层要用的东西是操作系统的凭据保护
/// （Windows 上是凭据管理器，由 LSASS 加密后落盘），而 Core 还要在 Linux 上构建。
/// 把"存在哪儿"抽成一个接口，Core 这边就能把双层逻辑（先内存、再持久、命中后提升）
/// 测干净，实现那一侧只需要测"真的写进去又读得回来"。</para>
/// <para><strong>这条线是安全边界，不是缓存</strong>：口令<strong>绝不</strong>写
/// <c>settings.ini</c>、不写注册表、不进日志 —— 那是本项目从 0.9.x 就立下的规矩。
/// 明文写进设置文件不是"简化版口令管理器"，是一个谁都会踩的坑；要做对就得交给操作系统保护，
/// 这也是当初参考软件那个跨重启的管理器没有跟着做的原因（现在按它应有的做法补上）。</para>
/// </summary>
public interface IPersistentPasswordStore
{
    /// <summary>这台机器上到底有没有这一层。假的时候界面对它只字不提，而不是给一个按不动的开关。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 这个口令存不存得下 —— <strong>不碰存储</strong>，只是先问一句。
    /// <para>存在的理由是不"静默失败"：操作系统的凭据存储对每条凭据的字节数有上限，
    /// 一条超长的口令如果写不进去又没人说，用户会以为自己已经保存了。</para>
    /// </summary>
    /// <returns>null 表示存得下；否则是一句<strong>可以直接显示给用户</strong>的原因。</returns>
    string? Rejects(string password);

    /// <summary>保存。<see cref="Rejects"/> 之外的失败（比如系统拒绝写入）也回一句能显示的原因。</summary>
    string? Save(string archivePath, string password);

    /// <summary>读回某个包的口令。没有就读不到，不抛。</summary>
    bool TryGet(string archivePath, out string password);

    /// <summary>删掉某个包的口令。本来就没有也算成功（幂等 —— "取消"不该报错）。</summary>
    void Remove(string archivePath);

    /// <summary>本程序存过的所有条目，<strong>不含口令</strong>，按时间从新到旧。</summary>
    IReadOnlyList<PasswordVaultEntry> List();

    /// <summary>全部删掉。界面上"关掉这个开关"就是走这条路，所以它必须真的一个不剩。</summary>
    void Clear();
}
