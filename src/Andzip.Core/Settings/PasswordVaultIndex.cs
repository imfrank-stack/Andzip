namespace Andzip.Core.Settings;

/// <summary>
/// 口令管理器的那份<strong>索引</strong>：本程序为哪些压缩包存过口令。
/// <para><strong>为什么要有它</strong>：口令存在操作系统的凭据里，而凭据的目标名是路径的哈希
/// （见 <c>PasswordVaultKey</c>）—— 哈希没法给人看。界面上要列出
/// 「D:\备份\税单2025.zip · 3 天前保存」，就得有个地方把哈希还原成路径。
/// 于是分工是：<strong>口令在凭据里，路径在这里</strong>。
/// 这个文件里本来就有 <c>recent_files</c>（同样是完整路径），所以这不是<strong>新增</strong>一类暴露 ——
/// 而把口令写进这个文件才是新增，那一条仍然禁止。</para>
/// <para>分隔符与 <see cref="RecentFiles"/> 同一个理由：用 <c>|</c>，
/// 因为 <c>;</c> 在 Windows 文件名里是合法字符，拿它分隔会把一条路径劈成两条。</para>
/// <para><strong>它不是权威</strong>：权威是凭据存储本身。索引里有条目而凭据没了（用户在
/// 系统面板里手工删了那条凭据），界面上就是一条打不开也删不掉的僵尸记录 ——
/// 所以 <see cref="Prune"/> 会在每次列出来之前把对不上的那些挑掉，
/// 而「全部删除」走的是按前缀枚举凭据的路子，不依赖这份索引。</para>
/// </summary>
public static class PasswordVaultIndex
{
    private const char Separator = '|';

    public static IReadOnlyList<string> Load(AppSettings settings)
    {
        var raw = settings.GetString(SettingKeys.PasswordVaultPaths);
        return raw.Length == 0
            ? []
            : [.. raw.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>记下一个包（按不区分大小写去重，跟 Windows 文件系统一致）。</summary>
    public static void Add(AppSettings settings, string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var full = Path.GetFullPath(archivePath);
        var kept = Load(settings)
            .Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase))
            .ToList();

        kept.Add(full);
        Save(settings, kept);
    }

    public static void Remove(AppSettings settings, string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        var full = Path.GetFullPath(archivePath);
        var kept = Load(settings)
            .Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Save(settings, kept);
    }

    public static void Clear(AppSettings settings) => Save(settings, []);

    /// <summary>
    /// 把已经不存在的包从索引里去掉，返回去掉几条。
    /// <para>与 <see cref="RecentFiles.Prune"/> 同一个道理：包被删走或改名之后，
    /// 那条记录不该一直挂在管理器里，更不该让用户对着一行"删不掉"的东西。</para>
    /// </summary>
    public static int Prune(AppSettings settings)
    {
        var all = Load(settings);
        var alive = all.Where(File.Exists).ToList();

        if (alive.Count == all.Count)
        {
            return 0;
        }

        Save(settings, alive);
        return all.Count - alive.Count;
    }

    private static void Save(AppSettings settings, IEnumerable<string> paths)
    {
        var joined = string.Join(Separator, paths);
        settings.SetString(SettingKeys.PasswordVaultPaths, joined.Length == 0 ? null : joined);
    }
}
