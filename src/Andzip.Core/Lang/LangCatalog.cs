namespace Andzip.Core.Lang;

/// <summary>
/// 语言目录：管理多份 <see cref="LangFile"/>，提供当前语言查询、缺省语言回退与 ${placeholder} 格式化。
/// 回退顺序：当前语言 → 缺省语言（English）→ 返回键名本身（便于在界面上一眼发现缺失文案）。
/// </summary>
public sealed class LangCatalog
{
    public const string DefaultLanguage = "English";

    private readonly Dictionary<string, LangFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentLanguage { get; private set; } = DefaultLanguage;

    public IReadOnlyCollection<string> Languages => _files.Keys;

    public void Add(string languageCode, LangFile file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        ArgumentNullException.ThrowIfNull(file);
        _files[languageCode] = file;
    }

    public void AddRange(IEnumerable<(string Code, LangFile File)> items)
    {
        foreach (var (code, file) in items)
        {
            Add(code, file);
        }
    }

    public bool HasLanguage(string languageCode) => _files.ContainsKey(languageCode);

    public bool SetLanguage(string languageCode)
    {
        if (!_files.ContainsKey(languageCode))
        {
            return false;
        }

        CurrentLanguage = languageCode;
        return true;
    }

    /// <summary>语言的展示名（[GENERAL] LANG_NAME），无则返回代码本身。</summary>
    public string GetLanguageDisplayName(string languageCode)
        => _files.TryGetValue(languageCode, out var file) && file.LangName.Length > 0
            ? file.LangName
            : languageCode;

    public string Get(string key, string? section = null)
    {
        if (_files.TryGetValue(CurrentLanguage, out var current) && current.TryGet(key, out var value, section))
        {
            return value;
        }

        if (_files.TryGetValue(DefaultLanguage, out var fallback) && fallback.TryGet(key, out var fallbackValue, section))
        {
            return fallbackValue;
        }

        return key;
    }

    /// <summary>读取并展开 [br]（用于多行提示、说明文本）。</summary>
    public string GetDisplay(string key, string? section = null)
        => LangFile.ExpandBreaks(Get(key, section));

    /// <summary>替换 ${name} 占位符（参考软件模板风格，如 TEXT_MENU_ARCHIVE_ZIP = 压缩为“${str1}”）。</summary>
    public string Format(string key, params (string Name, string Value)[] args)
    {
        var text = Get(key);
        foreach (var (name, value) in args)
        {
            text = text.Replace("${" + name + "}", value, StringComparison.Ordinal);
        }

        return text;
    }
}