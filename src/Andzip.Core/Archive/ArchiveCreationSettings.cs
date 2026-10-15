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
    /// <summary>先按未压缩总量占住磁盘簇再开始写。省的是「压到 90% 才发现放不下」，
    /// 代价是临时占用可能远大于最终包 —— 见设置页的提示。</summary>
    bool Preallocate = false,
    /// <summary>压缩时不独占源文件。读不到别人独占的文件这件事，任何开关都改变不了。</summary>
    bool ShareSources = false,
    IReadOnlyList<string>? ExcludePatterns = null,
    IProgress<ArchiveProgress>? Progress = null)
{
    public ArchiveFormatOption ResolvedFormat => Format ?? ArchiveFormatOption.Default;

    /// <summary>
    /// 额外的排除模式（<c>*.bak</c> / <c>*.tmp</c> 这类），来自「设置 → 压缩 → 压缩时要排除的文件类型」。
    /// 空列表表示只按 <see cref="ExcludeThumbsDb"/> 决定。
    /// </summary>
    public IReadOnlyList<string> ResolvedExcludePatterns => ExcludePatterns ?? [];

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

    /// <summary>
    /// 解析设置框里那一串 <c>*.bak; *.tmp;</c>：按分号切开、去空白、去重。
    /// <para>
    /// 分隔符只认分号不认逗号，因为 Windows 文件名里逗号是合法的
    /// （<c>report,final.txt</c> 是个真名字），按逗号切会把一条模式劈成两条不相干的。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ParseExcludePatterns(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var patterns = new List<string>();
        foreach (var raw in text.Split(';'))
        {
            var pattern = raw.Trim();
            if (pattern.Length > 0 && !patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            {
                patterns.Add(pattern);
            }
        }

        return patterns;
    }
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

/// <summary>「这件事对这个输入做不到」的原因（UI 映射为本地化文案）。
/// 既管创建（GZ 只能压一个文件、ZIP 的写入端只吃 ASCII 口令），也管就地编辑与归档注释
/// （这个格式改不动、这个格式没有注释、注释过长、目录改名做不了）。</summary>
public enum ArchiveConstraintReason
{
    /// <summary>该格式尚不支持创建。</summary>
    FormatNotSupported,

    /// <summary>GZ 只能压缩单个文件。</summary>
    GzRequiresSingleFile,

    /// <summary>该格式的密码只接受 ASCII 字符（ZIP 的写入端限制）。</summary>
    NonAsciiPasswordNotSupported,

    /// <summary>这个格式没有"归档注释"这一说（只有 ZIP 的结束记录里带得下一句）。</summary>
    ArchiveCommentNotSupported,

    /// <summary>注释本身超过了 ZIP 结束记录能表达的长度（65535 字节）。</summary>
    ArchiveCommentTooLong,

    /// <summary>这个格式<strong>只能读、改不动</strong>（引擎的 out-handler 不支持就地更新它）。
    /// 与"不支持创建"是两件事：GZ 能创建却改不动，ISO 两样都不行。</summary>
    ArchiveNotEditable,

    /// <summary>目录改名要连它底下每一个成员的路径一起改，而"照抄存储字节"那条路一次只搬一个成员。
    /// 半个树留在包里比拒绝更糟，所以这里明说做不到。</summary>
    DirectoryRenameNotSupported,
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
