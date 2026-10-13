namespace Andzip.Core.Settings;

/// <summary>
/// 「设置 → 解压」那一页的默认值。
/// <para>
/// 与 <see cref="CompressionDefaults"/> 同样的理由单独立类型：键名与缺省值只在这一处出现，
/// 而且这些值能被测试钉住 —— 设置页本身是 GUI，测不了，但它读写的那份状态测得了。
/// </para>
/// <para>
/// 缺省值一律等于引入这一页之前的行为：没动过设置的用户不该被悄悄改口味。
/// </para>
/// <para>
/// <see cref="DoubleClick"/> 记的是 <c>double_click_action</c>，从 1.6.0 起归这一页管：
/// 它选的落点规则就是下面 <see cref="SingleFileToFolder"/> 那套，摆在两处会让两个页面
/// 抢同一个键 —— 而一个键只能有一个地方负责回显和写入。
/// </para>
/// </summary>
public sealed record ExtractionDefaults(
    bool SingleFileToFolder,
    bool OpenFolderAfterExtract,
    bool KeepProgressWindow,
    ArchiveDoubleClick DoubleClick)
{
    public static ExtractionDefaults Fallback { get; } = new(
        SingleFileToFolder: false,
        OpenFolderAfterExtract: false,
        KeepProgressWindow: false,
        // 与 SettingKeys.ShouldExtractDirectly 的缺省一致：认不出来就是开主窗口。
        DoubleClick: ArchiveDoubleClick.OpenInAndzip);

    public static ExtractionDefaults Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ExtractionDefaults(
            settings.GetBool(SettingKeys.ExtractSingleFileToFolder, fallback: false),
            settings.GetBool(SettingKeys.ExtractOpenFolderAfter, fallback: false),
            settings.GetBool(SettingKeys.ExtractKeepProgress, fallback: false),
            SettingKeys.ParseDoubleClick(settings.GetString(SettingKeys.DoubleClickAction)));
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.SetBool(SettingKeys.ExtractSingleFileToFolder, SingleFileToFolder);
        settings.SetBool(SettingKeys.ExtractOpenFolderAfter, OpenFolderAfterExtract);
        settings.SetBool(SettingKeys.ExtractKeepProgress, KeepProgressWindow);
        settings.SetString(SettingKeys.DoubleClickAction, DoubleClick.ToStorage());
    }
}
