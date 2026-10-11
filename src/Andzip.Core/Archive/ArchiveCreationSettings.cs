namespace Andzip.Core.Archive;

/// <summary>
/// 创建压缩包的设置（字段对应“新建压缩文件”对话框，FR-2 / 附录 C.5）。
/// <para>
/// 进度：<see cref="Progress"/> 为 null 时引擎不安装回调。回调线程是解码器工作线程，
/// 所以设置端必须传 <see cref="Progress{T}"/>（构造时捕获 SynchronizationContext）而不是裸委托。
/// </para>
/// </summary>
public sealed record ArchiveCreationSettings(
    string OutputPath,
    CompressionLevelOption Level,
    ArchiveFormatOption? Format = null,
    bool ExcludeThumbsDb = true,
    bool IncludeRootFolder = false,
    bool Overwrite = true,
    bool TestAfterCreation = false,
    bool DeleteSourceAfterCreation = false,
    bool SeparateArchives = false,
    string? Password = null,
    long? VolumeSizeBytes = null,
    CompressionMethodOption? Method = null,
    EncryptionMethodOption? Encryption = null,
    string? EntryComment = null,
    bool EncryptHeaders = false,
    bool Solid = true,
    IProgress<ArchiveProgress>? Progress = null)
{
    public ArchiveFormatOption ResolvedFormat => Format ?? ArchiveFormatOption.Default;

    /// <summary>是否设置了非空密码。</summary>
    public bool HasPassword => !string.IsNullOrEmpty(Password);

    /// <summary>是否启用分卷（FR-2.3；当前仅 ZIP 由 Core 切块实现）。</summary>
    public bool HasVolume => VolumeSizeBytes is > 0;

    /// <summary>压缩方法；未显式指定时取所选格式的第一项。</summary>
    public CompressionMethodOption ResolvedMethod
        => Method ?? CompressionMethodOption.DefaultFor(ResolvedFormat);

    /// <summary>加密算法（仅在设置了密码时生效）。</summary>
    public EncryptionMethodOption ResolvedEncryption
        => Encryption
           ?? EncryptionMethodOption.ForFormat(ResolvedFormat).FirstOrDefault()
           ?? EncryptionMethodOption.Aes256;

    /// <summary>条目「文件说明」（FR-2.5；写入归档注释）。</summary>
    public bool HasEntryComment => !string.IsNullOrEmpty(EntryComment);
}

/// <summary>
/// 创建结果（用于 UI 展示与测试断言）。
/// <para><see cref="Warnings"/> 是引擎回传的“请求成功了，但某项设置被忽略”的提示，
/// 对应 native ABI 的 AZ_OK + 非空消息通道，UI 应作为警告呈现而不是丢弃。</para>
/// </summary>
public sealed record ArchiveCreationResult(
    string OutputPath,
    int FileCount,
    int DirectoryCount,
    long TotalInputBytes,
    long OutputBytes,
    IReadOnlyList<string> CreatedArchives,
    IReadOnlyList<string>? Warnings = null);

/// <summary>输入为空时抛出（UI 据此显示本地化提示，而不是依赖 Core 层文案）。</summary>
public sealed class EmptyArchiveInputException : Exception
{
    public EmptyArchiveInputException()
        : base("At least one input item is required.")
    {
    }
}

/// <summary>输入与所选格式不满足约束的原因（UI 映射为本地化文案）。</summary>
public enum ArchiveConstraintReason
{
    /// <summary>该格式尚不支持创建。</summary>
    FormatNotSupported,

    /// <summary>GZ 只能压缩单个文件。</summary>
    GzRequiresSingleFile,

    /// <summary>该格式的密码只接受 ASCII 字符（ZIP 的写入端限制）。</summary>
    NonAsciiPasswordNotSupported,
}

/// <summary>输入与所选格式不兼容时抛出。</summary>
public sealed class ArchiveConstraintException : Exception
{
    public ArchiveConstraintException(ArchiveConstraintReason reason)
        : base($"Archive constraint violated: {reason}.")
    {
        Reason = reason;
    }

    public ArchiveConstraintReason Reason { get; }
}
