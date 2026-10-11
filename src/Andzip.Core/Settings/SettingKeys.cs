namespace Andzip.Core.Settings;

/// <summary>双击压缩包文件时做什么（常规设置页的第一项）。</summary>
public enum ArchiveDoubleClick
{
    /// <summary>打开 Andzip 主窗口浏览内容。</summary>
    OpenInAndzip,

    /// <summary>直接解压到与压缩包同名的文件夹，只显示进度条，完成后自动关闭。</summary>
    ExtractDirectly,
}

/// <summary>
/// 设置键名与取值解析。集中一处，免得键名在各处拼字符串拼歪。
/// </summary>
public static class SettingKeys
{
    /// <summary>「双击直接解压」的命令行开关，由注册表 shell\extract\command 带上。</summary>
    public const string ExtractSwitch = "/extract";

    public const string DoubleClickAction = "double_click_action";
    public const string AllowEscCancelProgress = "allow_esc_cancel_progress";

    /// <summary>枚举存成稳定的小写标识（不是数字），这样以后插入新值不会让老设置错位。</summary>
    public static string ToStorage(this ArchiveDoubleClick value)
        => value == ArchiveDoubleClick.ExtractDirectly ? "extract" : "open";

    /// <summary>认不出来的取值（手改过、或来自更新版本）回落到 <see cref="ArchiveDoubleClick.OpenInAndzip"/>。</summary>
    public static ArchiveDoubleClick ParseDoubleClick(string? stored)
        => stored?.Trim().ToLowerInvariant() == "extract"
            ? ArchiveDoubleClick.ExtractDirectly
            : ArchiveDoubleClick.OpenInAndzip;

    /// <summary>
    /// 这次启动要不要「直接解压」而不是打开主窗口。
    /// <para>
    /// 行为由<strong>设置</strong>决定，而不是由注册表里哪个动词被调用决定 —— 后者踩过坑：
    /// 把 <c>shell</c> 默认值改成 <c>extract</c> 之后，老版本注册出来的 ProgID 里根本没有
    /// <c>shell\extract\command</c>，Windows 找不到动词就退回 <c>open</c>，表现成「设了没用」。
    /// 而且绿色版的文件名带版本号，升级后注册表里那条命令还指着上一个 exe。
    /// </para>
    /// </summary>
    public static bool ShouldExtractDirectly(string? storedSetting, bool hasExplicitSwitch)
        => hasExplicitSwitch || ParseDoubleClick(storedSetting) == ArchiveDoubleClick.ExtractDirectly;

    /// <summary>双击解压的落盘位置：压缩包所在目录下一个与压缩包同名的文件夹。</summary>
    public static string DirectExtractDestination(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath))
            ?? throw new InvalidOperationException("压缩包没有父目录：" + archivePath);

        return Path.Combine(directory, Path.GetFileNameWithoutExtension(archivePath));
    }
}
