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
/// </summary>
public sealed record ExtractionDefaults(
    bool SingleFileToFolder,
    bool OpenFolderAfterExtract,
    bool KeepProgressWindow)
{
    public static ExtractionDefaults Fallback { get; } = new(
        SingleFileToFolder: false,
        OpenFolderAfterExtract: false,
        KeepProgressWindow: false);

    public static ExtractionDefaults Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ExtractionDefaults(
            settings.GetBool(SettingKeys.ExtractSingleFileToFolder, fallback: false),
            settings.GetBool(SettingKeys.ExtractOpenFolderAfter, fallback: false),
            settings.GetBool(SettingKeys.ExtractKeepProgress, fallback: false));
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.SetBool(SettingKeys.ExtractSingleFileToFolder, SingleFileToFolder);
        settings.SetBool(SettingKeys.ExtractOpenFolderAfter, OpenFolderAfterExtract);
        settings.SetBool(SettingKeys.ExtractKeepProgress, KeepProgressWindow);
    }
}
