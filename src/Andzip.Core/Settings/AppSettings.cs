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

    /// <summary>设置文件的默认路径（%APPDATA%\Andzip\settings.ini）。拿不到用户目录时退回临时目录。</summary>
    public static string DefaultFilePath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.GetTempPath();
            }

            return Path.Combine(root, DirectoryName, FileName);
        }
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

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = new List<string>(_values.Count + 2)
        {
            "; Andzip 设置文件。删除本文件即恢复全部默认值。",
            "; 手工编辑后需重启程序生效。",
        };
        lines.AddRange(_values.Select(pair => $"{pair.Key} = {pair.Value}"));
        File.WriteAllLines(path, lines);
    }

    public string GetString(string key, string fallback = "")
        => _values.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>value 为 null 表示删除该键（回到默认）。</summary>
    public void SetString(string key, string? value)
    {
        if (value is null)
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }
    }

    public bool GetBool(string key, bool fallback = false)
        => _values.TryGetValue(key, out var value)
            ? value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            : fallback;

    public void SetBool(string key, bool value) => _values[key] = value ? "1" : "0";

    /// <summary>
    /// 整数值。存的不是数字（手改过、或来自更新版本）时回落到 <paramref name="fallback"/>，
    /// 与 <see cref="SettingKeys.ParseDoubleClick"/> 同一套容错理由：设置文件不该让程序起不来。
    /// </summary>
    public int GetInt(string key, int fallback = 0)
        => _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    public void SetInt(string key, int value) => _values[key] = value.ToString(CultureInfo.InvariantCulture);

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
