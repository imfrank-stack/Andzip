using System.Globalization;

namespace Andzip.Core.Settings;

/// <summary>
/// 应用设置：一个扁平的 <c>key = value</c> 文本文件。
/// <para>
/// 为什么不用 JSON：设置项全是标量与字符串列表，而 <c>System.Text.Json</c> 的反射序列化在
/// 发布裁剪（<c>PublishTrimmed</c>）下需要额外的保留配置，为一个键值文件把裁剪复杂度引进来
/// 不划算。语言包与 VersionNo.ini 本来就是这个语法子集，解析器风格也能保持一致。
/// </para>
/// <para>
/// 存储位置是<strong>当前用户</strong>目录而不是程序目录：绿色版常被放在只读位置或 U 盘之外，
/// 而“关联了哪些格式”是这台机器上的状态（注册表里也是），跟着用户走才对。
/// </para>
/// </summary>
public sealed class AppSettings
{
    private const string DirectoryName = "Andzip";
    private const string FileName = "settings.ini";

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 这个实例改过、但还没落盘的键。<see cref="Save"/> 只写这些，见 <see cref="Save"/> 那段。
    /// </summary>
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>被显式删掉（<c>SetString(key, null)</c>）的键 —— 合并写回时要真的从磁盘上抹掉。</summary>
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 设置文件的默认路径（%APPDATA%\Andzip\settings.ini）。拿不到用户目录时退回临时目录。
    /// <para><strong>整个程序问"设置在哪"只有这一个口子</strong>，所以它可以被整体换掉 ——
    /// 见 <see cref="RedirectForTests"/>。主窗口那几处 <c>_settings.Save(DefaultFilePath)</c>
    /// 都从这里取值，才使得"换掉它"是一件事而不是二十件事。</para>
    /// </summary>
    public static string DefaultFilePath => Redirected ?? Resolve();

    /// <summary>被 <see cref="RedirectForTests"/> 换掉之后的路径；null 表示没换过。</summary>
    private static string? Redirected;

    /// <summary>
    /// 把"设置文件在哪"整个换到 <paramref name="directory"/> 下面。<strong>只给测试与自检用。</strong>
    /// <para>为什么要有这个东西：任何一条构造 <c>MainWindow</c> 并打开一个包的用例，都会顺手往
    /// <c>recent_files</c> 记一笔并落盘 —— 落的是这台机器真的 <c>%APPDATA%\Andzip\settings.ini</c>。
    /// 2026-10-16 一次门禁之后，用户那份文件里的"最近的文件"变成了一个临时目录里的 <c>box.zip</c>。
    /// 逐个用例传自己的路径要改二十处、并且漏一个就回到原样；换掉这一个口子，测试进程里
    /// 没有任何一条路能写到用户的文件上。</para>
    /// </summary>
    public static void RedirectForTests(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Redirected = Path.Combine(directory, FileName);
    }

    private static string Resolve()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, DirectoryName, FileName);
    }

    public static AppSettings Load(string path)
    {
        var settings = new AppSettings();
        settings.Reload(path);
        return settings;
    }

    /// <summary>
    /// 用磁盘上的内容<strong>就地</strong>替换这个实例里的值。
    /// <para>为什么要有这个方法：设置窗口写文件时用的是它自己 <see cref="Load"/> 出来的一份实例，
    /// 而主窗口手里那份是启动时读的。不重读的话有两件事会同时发生 ——
    /// ① 主窗口"重新应用界面设置"读到的是旧值，用户点了「立即应用」却像没生效；
    /// ② 更糟：主窗口之后任何一次写盘（记最近的文件、记住上次输出目录）都会拿旧快照
    /// 把整个文件覆盖掉，刚才那一项设置<strong>连重启都找不回来</strong>。
    /// 就地替换而不是换新实例，是为了让已经持有引用的地方（主窗口、设置窗口）一起看到新值。</para>
    /// </summary>
    public void Reload(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _values.Clear();
        _dirty.Clear();
        _removed.Clear();
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            _values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
    }

    /// <summary>
    /// 落盘：<strong>只把这个实例改过的那几个键并回去</strong>，不是把内存里记得的全部重写一遍。
    /// <para>为什么必须这样：这台机器上同时开着几个 Andzip 是常态 —— 每次双击压缩包都起一个新进程，
    /// 每个进程手里是它<strong>启动那一刻</strong>读到的整份快照。旧写法（整文件覆盖）让任何一个
    /// 老快照都能把别人刚写的键抹掉，而且抹掉的键连重启都找不回来（文件已经被覆盖了）。
    /// 用户报的「双击直接解压怎么设都没用」就是这么来的：在 B 窗口里选了「直接解压」并落盘，
    /// 回到早就开着的 A 窗口打开一个包，A 记一笔最近文件，顺手把 <c>double_click_action</c>
    /// 一起擦掉了。</para>
    /// <para>所以这里先重读磁盘，把<strong>自己改过的</strong>键盖上去、<strong>自己删过的</strong>键抹掉，
    /// 再写回；写完把合并结果同步回本实例，下一次保存不带旧值。
    /// 两个进程<strong>同时</strong>改同一个键仍然是后写的赢 —— 那是没有锁的键值文件的边界，
    /// 但"谁没碰过的键都不该消失"这一条从此成立。</para>
    /// </summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var merged = new AppSettings();
        merged.Reload(path);

        foreach (var key in _dirty)
        {
            if (_values.TryGetValue(key, out var value))
            {
                merged._values[key] = value;
            }
        }

        foreach (var key in _removed)
        {
            merged._values.Remove(key);
        }

        var lines = new List<string>(merged._values.Count + 2)
        {
            "; Andzip 设置文件。删除本文件即恢复全部默认值。",
            "; 手工编辑后需重启程序生效。",
        };
        lines.AddRange(merged._values.Select(pair => $"{pair.Key} = {pair.Value}"));
        File.WriteAllLines(path, lines);

        // 本实例跟着磁盘上的这一份：刚写出去的键不再是"待写"，而别人写的键从这里开始也算见过。
        _values.Clear();
        foreach (var pair in merged._values)
        {
            _values[pair.Key] = pair.Value;
        }

        _dirty.Clear();
        _removed.Clear();
    }

    public string GetString(string key, string fallback = "")
        => _values.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>value 为 null 表示删除该键（回到默认）。</summary>
    public void SetString(string key, string? value)
    {
        if (value is null)
        {
            _values.Remove(key);
            _dirty.Remove(key);
            _removed.Add(key);
        }
        else
        {
            _values[key] = value;
            _removed.Remove(key);
            _dirty.Add(key);
        }
    }

    public bool GetBool(string key, bool fallback = false)
        => _values.TryGetValue(key, out var value)
            ? value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            : fallback;

    /// <summary>写布尔一律走 <see cref="SetString"/>：脏键只在那一处登记，漏一处就等于白改。</summary>
    public void SetBool(string key, bool value) => SetString(key, value ? "1" : "0");

    /// <summary>
    /// 整数值。存的不是数字（手改过、或来自更新版本）时回落到 <paramref name="fallback"/>，
    /// 与 <see cref="SettingKeys.ParseDoubleClick"/> 同一套容错理由：设置文件不该让程序起不来。
    /// </summary>
    public int GetInt(string key, int fallback = 0)
        => _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    public void SetInt(string key, int value) => SetString(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>分号分隔的字符串列表；空值表示空列表。</summary>
    public IReadOnlyList<string> GetStringList(string key)
    {
        var raw = GetString(key);
        return raw.Length == 0
            ? []
            : [.. raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public void SetStringList(string key, IEnumerable<string> values)
    {
        var joined = string.Join(';', values);
        SetString(key, joined.Length == 0 ? null : joined);
    }

    /// <summary>是否显式设置过某个键（用于区分“没设过”与“设成了默认值”）。</summary>
    public bool Has(string key) => _values.ContainsKey(key);
}
