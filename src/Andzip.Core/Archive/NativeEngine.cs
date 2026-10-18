using Andzip.Native;

namespace Andzip.Core.Archive;

/// <summary>
/// Core 与 native 引擎（<c>Andzip.Native</c> P/Invoke 外壳）之间唯一的适配点。
/// <para>
/// 四件只允许存在一份的事情都收在这里：引擎能力表的读取、native 错误到 Core 异常类型的翻译、
/// <see cref="IProgress{T}"/> 到引擎回调的桥接、以及 native 选项记录的组装。
/// 这样 <c>Andzip.Native</c> 的类型不会从 Core 的公开接口漏到 GUI。
/// </para>
/// </summary>
internal static class NativeEngine
{
    private static readonly Lazy<EngineState> LazyState = new(Load);

    private sealed record EngineState(
        bool Available,
        string? MissingDetail,
        BuildInformation? Build,
        IReadOnlyList<FormatInfo> Formats,
        IReadOnlyList<CodecInfo> Codecs);

    public static bool IsAvailable => LazyState.Value.Available;

    public static string? MissingDetail => LazyState.Value.MissingDetail;

    public static BuildInformation? Build => LazyState.Value.Build;

    public static IReadOnlyList<FormatInfo> Formats => LazyState.Value.Formats;

    public static IReadOnlyList<CodecInfo> Codecs => LazyState.Value.Codecs;

    /// <summary>引擎在进程级的线程数（0 = 每个硬件线程一个）。必须在打开任何句柄之前设置。</summary>
    public static uint NumThreads
    {
        get => IsAvailable ? AndzipCore.NumThreads : 0u;
        set
        {
            if (IsAvailable)
            {
                AndzipCore.NumThreads = value;
            }
        }
    }

    /// <summary>解码器可分配内存上限（MiB，0 = 不限制）。同样必须在打开句柄前设置。</summary>
    public static ulong MemoryLimitMiB
    {
        get => IsAvailable ? AndzipCore.MemoryLimitMiB : 0ul;
        set
        {
            if (IsAvailable)
            {
                AndzipCore.MemoryLimitMiB = value;
            }
        }
    }

    private static EngineState Load()
    {
        try
        {
            // 触碰 BuildInfo 就会真正加载 DLL；失败时抛 AndzipLibraryNotFoundException。
            var build = AndzipCore.BuildInfo;
            return new EngineState(true, null, build, AndzipCore.Formats, AndzipCore.Codecs);
        }
        catch (Exception ex)
        {
            return new EngineState(false, ex.Message, null, [], []);
        }
    }

    /// <summary>引擎不可用时抛 <see cref="ArchiveFailureException"/>；可用时为空操作。</summary>
    public static void Require()
    {
        if (!IsAvailable)
        {
            throw new ArchiveFailureException(
                ArchiveFailureReason.EngineMissing,
                "compression engine is unavailable: " + MissingDetail);
        }
    }

    /// <summary>按引擎的规范名（"7z" / "zip" / "gzip"…）查格式；引擎缺失或未登记时返回 null。</summary>
    public static FormatInfo? FindFormat(string engineFormat)
        => Formats.FirstOrDefault(f => string.Equals(f.Name, engineFormat, StringComparison.OrdinalIgnoreCase));

    public static bool CanCreateWith(string engineFormat) => FindFormat(engineFormat)?.CanWrite == true;

    public static bool CanReadWith(string engineFormat) => FindFormat(engineFormat)?.CanRead == true;

    /// <summary>
    /// 引擎这次真的带了这个编码器的<strong>编码</strong>方向。
    /// <para>
    /// ZSTD / LZ4 / LZ5 / Lizard / Brotli 只有在用 <c>-ExtraCodecs</c> 编出来的内核里才登记；
    /// 发布的那版没有。所以"编码器在不在"必须由引擎回答。
    /// </para>
    /// <para>
    /// 但要注意它<strong>不能</strong>单独用来决定某个方法可选：登记 ≠ 能写 —— 7z 的写入路径
    /// 目前对这五个一律 E_INVALIDARG（<see cref="CompressionMethodOption.Table"/> 里没列它们）。
    /// 等内核那条路径修通、方法重新进表之后，这里才是「少筛一项」的那一层保险。
    /// </para>
    /// </summary>
    public static bool CanEncodeWith(string codecName)
        => Codecs.Any(c => c.CanEncode && string.Equals(c.Name, codecName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 引擎能读取的所有扩展名（小写、带点、已去重）。
    /// 引擎的 extension 字段是空格分隔的多扩展名，这里全部拆开。
    /// <para>
    /// 副扩展名不能丢：<c>.tgz</c> / <c>.txz</c> 这类复合写法只出现在第二、第三个位置，
    /// 而「打开」对话框的过滤器要覆盖全部受支持格式 —— 少一个就意味着那种包选不了。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ReadableExtensions()
    {
        var extensions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var format in Formats.Where(f => f.CanRead))
        {
            foreach (var raw in SplitExtensions(format.Extension))
            {
                extensions.Add("." + raw.ToLowerInvariant());
            }
        }

        return [.. extensions];
    }

    public static IReadOnlyList<string> SplitExtensions(string? extension)
        => string.IsNullOrWhiteSpace(extension)
            ? []
            : extension.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ------------------------------------------------------------- option builders

    public static OpenOptions BuildOpenOptions(string? password, IProgress<ArchiveProgress>? progress)
        => new()
        {
            Password = string.IsNullOrEmpty(password) ? null : password,
            Progress = Bridge(progress, ArchiveOperation.Open),
        };

    public static ExtractOptions BuildExtractOptions(
        string? password,
        AzOverwrite overwrite,
        bool preserveMTime,
        IProgress<ArchiveProgress>? progress,
        ArchiveOperation operation = ArchiveOperation.Extract)
        => new()
        {
            Password = string.IsNullOrEmpty(password) ? null : password,
            Overwrite = overwrite,
            PreserveMTime = preserveMTime,
            Progress = Bridge(progress, operation),
        };

    /// <summary>
    /// 把 Core 的创建设置翻译成引擎选项。<paramref name="engineFormat"/> 是引擎规范名，
    /// 由 <see cref="ArchiveFormatOption.EngineFormat"/> 提供；单流格式（gz/xz/bz2）时调用方
    /// 传目标格式，其余传容器格式。
    /// </summary>
    public static CreateOptions BuildCreateOptions(
        string engineFormat,
        ArchiveCreationSettings settings,
        IProgress<ArchiveProgress>? progress)
    {
        var format = settings.ResolvedFormat;
        var level = settings.Level;

        // 「0-仅存储」必须落到 Copy 方法：同时下发 m=Deflate 与 -mx=0 时，7-Zip 会按
        // Deflate 的默认级别压缩，把“不压缩”的意图吃掉（实测 200 KB 文本压成 397 字节）。
        // 但只有格式的方法表里真的有 Copy 才能这么做 —— tar 没有压缩方法属性，给它
        // 下发 m=Copy 会被引擎拒绝。用户显式选了方法时尊重他的选择。
        var method = settings.Method ?? InferMethod(format, level);
        var (extraProperties, dictSizeMib) = TierExtras(format, method, level);

        return new CreateOptions
        {
            Format = engineFormat,
            Method = string.IsNullOrEmpty(method.EngineMethod) ? null : method.EngineMethod,
            Level = level.EngineLevel,
            ExtraProperties = extraProperties,
            DictSizeMib = dictSizeMib,
            Password = string.IsNullOrEmpty(settings.Password) ? null : settings.Password,
            Encryption = string.IsNullOrEmpty(settings.Password)
                ? AzEncryption.None
                : ToEngine(settings.ResolvedEncryption.Kind),
            EncryptHeaders = format.SupportsEncryptHeaders && settings.EncryptHeaders,
            // 固实块是 7z 的概念；对别的格式不下发，免得引擎回一条用户从没要求的警告。
            Solid = format.IsSevenZip ? settings.Solid : false,
            MultiThread = true,
            // 分卷分两种：7z 把卷大小交给引擎，由它逐卷写；ZIP 那族是 Core 事后切块
            // （WinZip 的 .zNN 命名，引擎的卷机制不产那种名字），所以这里必须是 null。
            VolumeSize = format.IsSevenZip && settings.HasVolume
                ? VolumeSizeText(settings.VolumeSizeBytes!.Value)
                : null,
            Preallocate = settings.Preallocate,
            ShareLocked = settings.ShareSources,
            Progress = Bridge(progress, ArchiveOperation.Compress),
            // EntryComment 故意不下发：ABI 的 comment 是「归档注释」，而 7-Zip 的
            // out-handler 不支持设置它（见 az_writer.cpp）；Core 侧改为记一条警告。
            Comment = null,
        };
    }

    /// <summary>
    /// 档位除了级别号之外还捎带什么属性 —— 全项目只有这一处翻译“极限档到底下发了什么”，
    /// 界面上不摆第二个版本，门面里也不摆。两条都量过才写死（<c>tests/perf/tiers.ps1</c>
    /// 写的 <c>docs/perf-tiers.tsv</c>，128 MiB 单条目、这台机器）。
    /// </summary>
    private static (string? ExtraProperties, uint DictSizeMib) TierExtras(
        ArchiveFormatOption format, CompressionMethodOption method, CompressionLevelOption level)
    {
        // 极限 + ZIP + Deflate64 → a=0：huffman 用最快那一档。mixed 那一格
        // 39 657 487 字节 / 91.71 s 变成 40 105 064 字节 / 6.95 s —— 大 1.13%、快 13.2 倍。
        //
        // Deflate 在 5.6.0 和 Deflate64 一起发过 a=0（那一格 39 656 224 / 91.42 s 变成
        // 39 983 091 / 6.96 s）。5.7.0 把它从这一行里拿掉了，因为压 Deflate 的编码器
        // 换成了 zlib-ng（native/src/codecs/，P2-c），而 a 是上游那个编码器的旋钮：
        // 我们的 SetCoderProperties 认不得它，会按上游的规矩 E_INVALIDARG 拒掉。
        // 拒是对的 —— 静默收下会让"a=0 让极限档更快"变成一句没人验证的谎。
        //
        // 只给这两个方法是量出来的边界，不是谨慎的口味：同一个 a 在 ZIP 的 LZMA 上把
        // text 那一格从 2 840 撑到 3 712 字节（大 30.7%），在 BZip2 上整单被拒
        // （Win32 87，一个字节都不吐），PPMd 与 Copy 收了但产物一字不变。
        if (level.EngineLevel == 9 && format.IsZip && method.EngineMethod == "Deflate64")
        {
            return ("a=0", 0);
        }

        // 极限 + 7z + LZMA2 → 字典 64 MiB。引擎在这一档自己给的是 128 MiB（拿不带 -d 的
        // 产物字节去撞带 -d 的那些格，逐字节相同撞出来的，见 docs/perf-p4-p5.md 第 3.1 节）。
        // 压到 64 之后 mixed 那一格：35 644 620 → 35 636 412 字节（还小 0.023%），而进程的
        // 峰值工作集从 1 292 MiB 掉到 686 MiB —— 这一档买到的不是"包更小"，是内存：
        // 字典是每线程一份的，128 MiB 在小机器上不是慢，是可能压不完。
        if (level.EngineLevel == 9 && format.IsSevenZip && method.EngineMethod == "LZMA2")
        {
            return (null, 64);
        }

        return (null, 0);
    }

    /// <summary>
    /// 字节数 → 引擎听得懂的卷大小。内核认 <c>"100M"</c> / <c>"700m"</c> / <c>"4G"</c> 这种带单位的
    /// 写法，也认裸字节数；界面上给的是整 MB，所以能整除就用人话写，不能就老实写字节数。
    /// </summary>
    private static string? VolumeSizeText(long bytes)
    {
        if (bytes <= 0)
        {
            return null;
        }

        const long KiB = 1024, MiB = KiB * 1024, GiB = MiB * 1024, TiB = GiB * 1024;
        return bytes switch
        {
            _ when bytes % TiB == 0 => $"{bytes / TiB}t",
            _ when bytes % GiB == 0 => $"{bytes / GiB}g",
            _ when bytes % MiB == 0 => $"{bytes / MiB}m",
            _ when bytes % KiB == 0 => $"{bytes / KiB}k",
            _ => bytes.ToString(),
        };
    }

    /// <summary>该格式能写的加密算法。7z 只有 AES-256；不加密密码的格式返回空列表。</summary>
    private static CompressionMethodOption InferMethod(
        ArchiveFormatOption format,
        CompressionLevelOption level)
    {
        if (level.EngineLevel != 0)
        {
            return CompressionMethodOption.DefaultFor(format);
        }

        return format.Methods.FirstOrDefault(m => m.EngineMethod == "Copy")
               ?? CompressionMethodOption.Auto;
    }

    /// <summary>Core 的加密类别 → 引擎枚举。这是 native 类型不外泄的唯一兑换点。</summary>
    private static AzEncryption ToEngine(ArchiveEncryption kind) => kind switch
    {
        ArchiveEncryption.ZipCrypto => AzEncryption.ZipCrypto,
        ArchiveEncryption.Aes256 => AzEncryption.Aes256,
        ArchiveEncryption.Default => AzEncryption.Default,
        _ => AzEncryption.None,
    };

    // ------------------------------------------------------------- progress bridge

    /// <summary>
    /// 把调用方的 <see cref="IProgress{T}"/> 包成外壳要的委托。sink 为 null 时返回 null，
    /// 引擎就不会安装回调。
    /// </summary>
    public static Action<ProgressInfo>? Bridge(IProgress<ArchiveProgress>? sink, ArchiveOperation operation)
    {
        if (sink is null)
        {
            return null;
        }

        return info => sink.Report(new ArchiveProgress(
            operation,
            CheckedToLong(info.BytesIn),
            CheckedToLong(info.TotalIn),
            info.CurrentEntry,
            CheckedToInt(info.EntryIndex),
            CheckedToInt(info.EntryCount)));
    }

    // ------------------------------------------------------------- error translation

    public static void Run(string operation, Action action, bool badFormatMeansUnsupported = false)
        => Run<object?>(operation, () =>
        {
            action();
            return null;
        }, badFormatMeansUnsupported);

    public static T Run<T>(string operation, Func<T> action, bool badFormatMeansUnsupported = false)
    {
        Require();
        try
        {
            return action();
        }
        catch (AndzipException ex)
        {
            throw Translate(operation, ex, badFormatMeansUnsupported);
        }
    }

    /// <summary>
    /// 把 native 的错误码翻译成 Core 已有的异常类型。能对上既有契约的绝不新增类型，
    /// 这样 GUI 的 catch 分支（ArchiveConstraintException / InvalidDataException /
    /// OperationCanceledException）在换引擎后仍然成立。
    /// </summary>
    /// <param name="badFormatMeansUnsupported">
    /// “打开”阶段为 true：那时 ErrorBadFormat 的含义是“这不是我能识别的归档”，
    /// 对应 UI 的“格式不支持”提示；解压/测试阶段为 false，同样的错误码意味着包损坏。
    /// </param>
    internal static Exception Translate(string operation, AndzipException ex, bool badFormatMeansUnsupported)
    {
        var detail = string.IsNullOrWhiteSpace(ex.NativeMessage) ? ex.Message : ex.NativeMessage!;

        return ex.Result switch
        {
            AzResult.ErrorNotFound => new FileNotFoundException(detail),
            AzResult.ErrorBadFormat when badFormatMeansUnsupported
                => new ArchiveConstraintException(ArchiveConstraintReason.FormatNotSupported),
            AzResult.ErrorBadFormat or AzResult.ErrorCrc => new InvalidDataException(detail),
            AzResult.ErrorUnsupported => new ArchiveConstraintException(ArchiveConstraintReason.FormatNotSupported),
            AzResult.ErrorPassword => new ArchiveFailureException(ArchiveFailureReason.Password, detail, ex),
            AzResult.ErrorCancelled => new OperationCanceledException(detail, ex),
            AzResult.ErrorIo => new ArchiveFailureException(ArchiveFailureReason.Io, detail, ex),
            _ => new ArchiveFailureException(ArchiveFailureReason.Internal, $"{operation}: {detail}", ex),
        };
    }

    private static long CheckedToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static int CheckedToInt(ulong value) => value > int.MaxValue ? int.MaxValue : (int)value;
}
