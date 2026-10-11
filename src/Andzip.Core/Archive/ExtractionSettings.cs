namespace Andzip.Core.Archive;

/// <summary>解压时同名文件（重名冲突）的处理策略（FR-3.7）。</summary>
public enum OverwritePolicy
{
    /// <summary>询问（由 DuplicateResolver 决定；未提供回调时按「自动重命名」处理，避免静默覆盖）。</summary>
    Ask,

    /// <summary>覆盖已存在的文件。</summary>
    Overwrite,

    /// <summary>跳过已存在的文件。</summary>
    Skip,

    /// <summary>自动重命名（<c>name (2).ext</c>）。</summary>
    Rename,
}

/// <summary>重名询问（FR-3.7「要解压的文件」对话框）的返回值。</summary>
public enum DuplicateDecision
{
    Rename,
    Overwrite,
    Skip,
}

/// <summary>
/// 解压设置（FR-3.1 / FR-3.3 / FR-3.7）。
/// DestinationPath 由 UI 解析为最终目标目录（是否附加压缩包同名子目录由对话框决定）。
/// <para>
/// 重名与路径安全由 Core 决定，不由引擎决定：引擎的 overwrite 只有覆盖/跳过两档，
/// 而“逐条解压”让 Core 能实现参考软件的「询问 / 自动重命名」并集中做 zip-slip 防护。
/// </para>
/// <para><see cref="Progress"/> 为 null 时引擎不安装回调；必须传 <see cref="Progress{T}"/>。</para>
/// </summary>
public sealed record ExtractionSettings(
    string ArchivePath,
    string DestinationPath,
    OverwritePolicy Overwrite = OverwritePolicy.Ask,
    string? Password = null,
    bool StripSingleRootFolder = false,
    bool DeleteArchiveAfterExtraction = false,
    Func<string, DuplicateDecision>? DuplicateResolver = null,
    IProgress<ArchiveProgress>? Progress = null)
{
    public bool HasPassword => !string.IsNullOrEmpty(Password);
}

/// <summary>解压结果（用于 UI 展示与测试断言）。</summary>
public sealed record ExtractionResult(
    string DestinationPath,
    int FileCount,
    int DirectoryCount,
    long TotalBytes,
    int SkippedCount,
    IReadOnlyList<string> ExtractedFiles);
