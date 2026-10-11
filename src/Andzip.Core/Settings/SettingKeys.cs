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

    /// <summary>双击解压的落盘位置：压缩包所在目录下一个与压缩包同名的文件夹。</summary>
    public static string DirectExtractDestination(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(archivePath))
            ?? throw new InvalidOperationException("压缩包没有父目录：" + archivePath);

        return Path.Combine(directory, Path.GetFileNameWithoutExtension(archivePath));
    }
}
