using System.Text;

namespace Andzip.Core.Lang;

/// <summary>
/// 解析后的 .lang 文件（INI 风格：分节 + key = value）。
/// 机制与参考软件一致（见需求文档 §7.3）：支持 ";" / "#" 注释、[SECTION]、[br] 换行标记。
/// </summary>
public sealed class LangFile
{
    private readonly List<string> _sectionOrder = new();
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>[GENERAL] LANG_NAME 的值（如“中文 (简体)”），用于语言选择界面展示。</summary>
    public string LangName { get; private set; } = string.Empty;

    /// <summary>节出现顺序（按文件内的声明顺序）。</summary>
    public IReadOnlyList<string> SectionOrder => _sectionOrder;

    /// <summary>全部键值对数量（去重后的合计）。</summary>
    public int Count => _sections.Values.Sum(s => s.Count);

    public static LangFile Parse(string text)
    {
        var file = new LangFile();
        var current = string.Empty;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                file.EnsureSection(current);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            file.EnsureSection(current)[key] = value;
        }

        if (file._sections.TryGetValue("GENERAL", out var general)
            && general.TryGetValue("LANG_NAME", out var langName))
        {
            file.LangName = langName;
        }

        return file;
    }

    public static LangFile Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>按节查询；<paramref name="section"/> 为 null 时在全部节中按声明顺序查找第一个命中项。</summary>
    public bool TryGet(string key, out string value, string? section = null)
    {
        if (section is not null)
        {
            if (_sections.TryGetValue(section, out var sec) && sec.TryGetValue(key, out var v))
            {
                value = v;
                return true;
            }

            value = string.Empty;
            return false;
        }

        foreach (var name in _sectionOrder)
        {
            if (_sections[name].TryGetValue(key, out var v))
            {
                value = v;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public string? Get(string key, string? section = null)
        => TryGet(key, out var value, section) ? value : null;

    /// <summary>把官方风格的 [br] 标记展开为系统换行符。</summary>
    public static string ExpandBreaks(string value) => value.Replace("[br]", Environment.NewLine);

    private Dictionary<string, string> EnsureSection(string name)
    {
        if (!_sections.TryGetValue(name, out var section))
        {
            section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[name] = section;
            _sectionOrder.Add(name);
        }

        return section;
    }
}