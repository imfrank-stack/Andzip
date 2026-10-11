namespace Andzip.Core.Archive;

/// <summary>
/// <see cref="ArchiveFailureException"/> 的大类。具体的引擎错误码在 <see cref="Message"/> 里，
/// UI 只需要按本枚举分支，不需要知道 native 层的 az_result。
/// </summary>
public enum ArchiveFailureReason
{
    /// <summary>native 引擎（andzip_core）没能加载，所有压缩/解压操作都不可用。</summary>
    EngineMissing,

    /// <summary>缺少密码或密码错误。</summary>
    Password,

    /// <summary>底层读写失败。</summary>
    Io,

    /// <summary>操作被取消（回调要求停止）。</summary>
    Cancelled,

    /// <summary>其它引擎内部错误。</summary>
    Internal,
}

/// <summary>
/// 引擎层失败的统一异常。
/// <para>
/// 存在的意义：把 P/Invoke 外壳的 <c>AndzipException</c> 挡在 Core 接口之外，
/// 否则 native 的错误码会变成 GUI 必须知道的公开契约。
/// 能映射到既有类型的都优先映射（找不到文件 → <see cref="FileNotFoundException"/>；
/// 数据损坏 → <see cref="InvalidDataException"/>；格式不支持 →
/// <see cref="ArchiveConstraintException"/>；取消 → <see cref="OperationCanceledException"/>），
/// 剩下的才用本类型。
/// </para>
/// </summary>
public sealed class ArchiveFailureException : Exception
{
    public ArchiveFailureException(ArchiveFailureReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public ArchiveFailureException(ArchiveFailureReason reason, string message, Exception? innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public ArchiveFailureReason Reason { get; }

    /// <summary>是否应当重新询问密码后重试。</summary>
    public bool IsPassword => Reason == ArchiveFailureReason.Password;
}
