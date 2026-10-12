using Andzip.Core.Archive;

namespace Andzip.Core.Settings;

/// <summary>
/// 「新建压缩文件」对话框打开时的默认值，从 <see cref="AppSettings"/> 读一次。
/// <para>
/// 单独立一个类型而不是让对话框直接去 <c>settings.GetBool("compress_...")</c>，有两个理由：
/// 键名与默认值只在这一处出现（加一项不用改三个地方），以及这些默认值可以被测试钉住 ——
/// 对话框本身测不了，但它读进来的东西测得了。
/// </para>
/// <para>
/// 刻意<strong>不</strong>包含口令与分卷：那两项每次都不一样，记住反而危险
/// （上次压的是加密包，这次压的是要发给别人的公开目录）。
/// </para>
/// </summary>
/// <param name="ExcludePatternText">设置框里那一串模式的<strong>原文</strong>（<c>*.bak; *.tmp;</c>）。
/// 存文本而不是解析好的列表有两个原因：记录级的相等比较对 <c>IReadOnlyList</c> 走的是引用相等，
/// 存列表会让"读回来等于写出去"这种断言假失败；而设置页编辑的本来就是那个文本框，
/// 存原文意味着不需要序列化 —— <see cref="Save"/> 写什么，文本框里就显示什么。</param>
public sealed record CompressionDefaults(
    CompressionLevelOption Level,
    bool IncludeRootFolder,
    bool ExcludeThumbsDb,
    string ExcludePatternText,
    bool TestAfterCreation,
    bool KeepProgressWindow,
    SettingKeys.OutputFolderMode OutputMode,
    string? FixedOutputFolder,
    // 带默认值，所以它们必须排在最后：前面那些参数是位置式的，老调用点不用改。
    bool PreallocateDisk = false,
    bool ShareSources = false)
{
    /// <summary>解析过的排除模式，直接喂给 <see cref="ArchiveCreationSettings.ExcludePatterns"/>。</summary>
    public IReadOnlyList<string> ParsedExcludePatterns => ArchiveCreationSettings.ParseExcludePatterns(ExcludePatternText);

    /// <summary>没有设置文件时的样子 —— 与引入这些设置之前的行为完全一致。</summary>
    public static CompressionDefaults Fallback { get; } = new(
        CompressionLevelOption.Default,
        IncludeRootFolder: false,
        ExcludeThumbsDb: true,
        ExcludePatternText: string.Empty,
        TestAfterCreation: false,
        KeepProgressWindow: false,
        SettingKeys.OutputFolderMode.LastUsed,
        FixedOutputFolder: null);

    public static CompressionDefaults Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 级别存的是数字，而 CompressionLevelOption.FromId 对认不出来的值回落到标准档：
        // 绿色版可能读到更新版本写下的档位号，那不该让程序起不来。
        var levelId = settings.GetInt(SettingKeys.CompressLevel, Fallback.Level.Id);

        return Fallback with
        {
            Level = CompressionLevelOption.FromId(levelId),
            // 缺省必须是"不建根目录"（true），才等于引入这一页之前的行为 ——
            // ArchiveCreationSettings.IncludeRootFolder 一直是 false。写成 fallback: false
            // 会让每个没动过设置的用户突然多出一层目录，而且没人会想到是设置页干的。
            IncludeRootFolder = !settings.GetBool(SettingKeys.CompressNoRootFolder, fallback: true),
            ExcludeThumbsDb = settings.GetBool(SettingKeys.CompressExcludeThumbsDb, fallback: true),
            ExcludePatternText = settings.GetString(SettingKeys.CompressExcludePatterns),
            TestAfterCreation = settings.GetBool(SettingKeys.CompressTestAfter, fallback: false),
            KeepProgressWindow = settings.GetBool(SettingKeys.CompressKeepProgress, fallback: false),
            OutputMode = SettingKeys.ParseOutputFolderMode(settings.GetString(SettingKeys.CompressOutputFolderMode)),
            FixedOutputFolder = NullIfBlank(settings.GetString(SettingKeys.CompressOutputFolder)),
            // 两项都缺省为 false，等于引入它们之前的行为：不占磁盘、按老规矩独占源文件。
            PreallocateDisk = settings.GetBool(SettingKeys.CompressPreallocate, fallback: false),
            ShareSources = settings.GetBool(SettingKeys.CompressShareSources, fallback: false),
        };
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.SetInt(SettingKeys.CompressLevel, Level.Id);
        settings.SetBool(SettingKeys.CompressNoRootFolder, !IncludeRootFolder);
        settings.SetBool(SettingKeys.CompressExcludeThumbsDb, ExcludeThumbsDb);
        settings.SetString(SettingKeys.CompressExcludePatterns, NullIfBlank(ExcludePatternText));
        settings.SetBool(SettingKeys.CompressTestAfter, TestAfterCreation);
        settings.SetBool(SettingKeys.CompressKeepProgress, KeepProgressWindow);
        settings.SetString(SettingKeys.CompressOutputFolderMode, OutputMode.ToStorage());
        settings.SetString(SettingKeys.CompressOutputFolder, FixedOutputFolder);
        settings.SetBool(SettingKeys.CompressPreallocate, PreallocateDisk);
        settings.SetBool(SettingKeys.CompressShareSources, ShareSources);
    }

    /// <summary>空与纯空白一律当作"没设过"，免得设置文件里留一堆 <c>key =</c>。</summary>
    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
