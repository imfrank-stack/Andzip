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
/// <para>
/// <strong>原来还有第四项 <c>KeepProgressWindow</c>（<c>extract_keep_progress_window</c>），
/// 4.9.0 整条删了</strong>（用户要求）。它只影响主窗口那一颗「解压」：开着就跑完了把进度框
/// 留在屏幕上。而留在屏幕上的那一屏永远写着「正在解压… 100%」，读起来是卡住 ——
/// 4.8.1 刚把那句话修成「解压完成」，4.9.0 把这个开关本身去掉。
/// 老用户的 <c>settings.ini</c> 里那一行不再有人读，也就没人写：
/// <c>AppSettings</c> 的写盘是按读进来的整份合并，不认识的那一行会原地留着，不影响任何行为。
/// 「设置 → 压缩」里那一档（<c>compress_keep_progress</c>）留着 —— 那颗框留着的时候外面还压着
/// 「新建压缩文件」对话框，立刻关会让用户以为没压成，两件事不一样。
/// </para>
/// </summary>
public sealed record ExtractionDefaults(
    bool SingleFileToFolder,
    bool OpenFolderAfterExtract,
    ArchiveDoubleClick DoubleClick)
{
    public static ExtractionDefaults Fallback { get; } = new(
        SingleFileToFolder: false,
        OpenFolderAfterExtract: false,
        // 与 SettingKeys.ShouldExtractDirectly 的缺省一致：认不出来就是开主窗口。
        DoubleClick: ArchiveDoubleClick.OpenInAndzip);

    public static ExtractionDefaults Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ExtractionDefaults(
            settings.GetBool(SettingKeys.ExtractSingleFileToFolder, fallback: false),
            settings.GetBool(SettingKeys.ExtractOpenFolderAfter, fallback: false),
            SettingKeys.ParseDoubleClick(settings.GetString(SettingKeys.DoubleClickAction)));
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.SetBool(SettingKeys.ExtractSingleFileToFolder, SingleFileToFolder);
        settings.SetBool(SettingKeys.ExtractOpenFolderAfter, OpenFolderAfterExtract);
        settings.SetString(SettingKeys.DoubleClickAction, DoubleClick.ToStorage());
    }
}
