namespace Andzip.Core.Lang;

/// <summary>
/// 语言目录：管理多份 <see cref="LangFile"/>，提供当前语言查询、缺省语言回退与 ${placeholder} 格式化。
/// 回退顺序：当前语言 → 缺省语言（English）→ 返回键名本身（便于在界面上一眼发现缺失文案）。
/// <para><strong>换语言是当场生效的</strong>（5.1.0）：切档时广播 <see cref="Changed"/>，
/// 已经画好的界面听到之后自己重做文字。这里<strong>不存</strong>任何界面状态 —— 目录只管"现在说哪种语言"，
/// 谁需要跟着变由各自订阅（与 <c>Design.ActiveChanged</c> 换主题同一条路子，因为撞的是同一个问题：
/// 本工程没有 XAML，文字是在控件构造时取一次就固化到实例上的）。</para>
/// </summary>
public sealed class LangCatalog
{
    public const string DefaultLanguage = "English";

    private readonly Dictionary<string, LangFile> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 语言换了。已经建好的界面不会自己改文字，订阅的一方要重做或就地改写自己那部分。
    /// <para>只在<strong>真的换了档</strong>时触发（与 <c>Design.Use</c> 同一条守卫）：启动时
    /// <see cref="LangLoader.CreateCatalog"/> 会连着调两次 <see cref="SetLanguage"/>，
    /// 那时一扇窗都还没有，而"没变也叫一声"会让以后任何一次刷新都把界面重建一遍。</para>
    /// </summary>
    public event Action? Changed;

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

    /// <summary>换到这一档（必须是加载到的语言包）。返回值问的是"<strong>这个代码有没有</strong>"，
    /// 不是"有没有换"——<see cref="LangLoader.CreateCatalog"/> 靠它决定要不要走中文回退，
    /// 所以"设成当前那一档"也得返回 true。</summary>
    public bool SetLanguage(string languageCode)
    {
        if (!_files.ContainsKey(languageCode))
        {
            return false;
        }

        if (!string.Equals(CurrentLanguage, languageCode, StringComparison.OrdinalIgnoreCase))
        {
            CurrentLanguage = languageCode;
            Changed?.Invoke();
        }

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