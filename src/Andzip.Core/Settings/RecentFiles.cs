namespace Andzip.Core.Settings;

/// <summary>
/// 「文件 → 最近的文件」那一份列表。最新的一次打开排在最前面。
/// <para>
/// 分隔符用 <c>|</c> 而不是 <see cref="AppSettings"/> 列表助手用的 <c>;</c>：
/// 分号在 Windows 文件名里是<strong>合法字符</strong>（<c>a;b.zip</c> 是个真名字），
/// 拿它当分隔符会把一条记录劈成两条；而 <c>|</c> 是 Windows 明令禁止出现在文件名里的字符，
/// 所以路径里不可能出现它。这里因此没有复用 <c>GetStringList</c>。
/// </para>
/// <para>存的是完整路径。同一个包换个写法（相对路径、大小写不同）不该占两个格子。</para>
/// </summary>
public static class RecentFiles
{
    /// <summary>列表长度上限 —— 再多就只是把菜单拉成找不到东西的长条。</summary>
    public const int Limit = 10;

    private const char Separator = '|';

    /// <summary>按存储顺序读出记录（不做存在性过滤，那交给 <see cref="Prune"/>）。</summary>
    public static IReadOnlyList<string> Load(AppSettings settings)
    {
        var raw = settings.GetString(SettingKeys.RecentFiles);
        return raw.Length == 0
            ? []
            : [.. raw.Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// 记下一次打开：挪到最前面、按不区分大小写去重、截到 <see cref="Limit"/> 条。
    /// <para>去重用不区分大小写是跟着 Windows 文件系统来的 —— 区分了就会给同一个包留两个格子。</para>
    /// </summary>
    public static void Record(AppSettings settings, string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var full = Path.GetFullPath(archivePath);
        var kept = Load(settings)
            .Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase))
            .ToList();

        kept.Insert(0, full);
        Save(settings, kept.Take(Limit));
    }

    public static void Clear(AppSettings settings) => Save(settings, []);

    /// <summary>
    /// 丢掉已经不存在的记录并写回，返回被丢掉几条。
    /// <para>在菜单展开时调一次就够了：文件被删走之后，那条记录不该一直挂在菜单上，
    /// 也不该等到攒满十条才自然淘汰。</para>
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
        settings.SetString(SettingKeys.RecentFiles, joined.Length == 0 ? null : joined);
    }
}
