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
        if (!File.Exists(path))
        {
            return settings;
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

            settings._values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return settings;
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
