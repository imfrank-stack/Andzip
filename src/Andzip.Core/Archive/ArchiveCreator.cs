using NativeArchiveWriter = Andzip.Native.ArchiveWriter;

namespace Andzip.Core.Archive;

/// <summary>
/// 创建压缩包门面（FR-2）：统一处理“独立压缩 / 压缩后测试 / 压缩后删除原始文件”，
/// 并把实际压缩交给 native 引擎。
/// <para>
/// 旧实现按格式分发到 Zip/Tar/Gz 三个写入器；现在只有一个写入路径，格式差异体现在
/// <see cref="ArchiveFormatOption.CreateFormat"/> 与 <see cref="ArchiveFormatOption.CreateWrapper"/>
/// 两个数据字段上。新增可写格式（7z / xz / bzip2 / wim）不再需要新代码。
/// </para>
/// <para>UI（新建压缩文件对话框）应调用本类，而不是直接调用引擎外壳。</para>
/// </summary>
public static class ArchiveCreator
{
    public static ArchiveCreationResult Create(
        IReadOnlyList<ArchiveInputItem> inputs,
        ArchiveCreationSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(settings);

        // 整个创建过程都在翻译范围内：引擎的错误码必须变成 Core 的异常类型，
        // 否则 Andzip.Native.AndzipException 会漏到 GUI。
        return NativeEngine.Run("create archive", () => CreateInner(inputs, settings, cancellationToken));
    }

    private static ArchiveCreationResult CreateInner(
        IReadOnlyList<ArchiveInputItem> inputs,
        ArchiveCreationSettings settings,
        CancellationToken cancellationToken)
    {
        var result = settings.SeparateArchives && inputs.Count > 0
            ? CreateSeparateArchives(inputs, settings, cancellationToken)
            : CreateSingleArchive(inputs, settings with { SeparateArchives = false }, cancellationToken);

        if (settings.TestAfterCreation)
        {
            // 分卷时 CreatedArchives 是一地卷，而它们只是字节切片：只测"自己就能被打开的
            // 那一个"——WinZip 风格是末卷 name.zip，7z 风格是首卷 name.7z.001（内核从它
            // 往后拼）。判据在 SplitVolumeSupport.IsVolumePart 里，两族各说各的入口。
            foreach (var archivePath in result.CreatedArchives.Where(path => !SplitVolumeSupport.IsVolumePart(path)))
            {
                ArchiveTester.Test(archivePath, settings.Password, settings.Progress, cancellationToken);
            }
        }

        if (settings.DeleteSourceAfterCreation)
        {
            SourceCleaner.Delete(inputs, result.CreatedArchives);
        }

        return result;
    }

    private static ArchiveCreationResult CreateSingleArchive(
        IReadOnlyList<ArchiveInputItem> inputs,
        ArchiveCreationSettings settings,
        CancellationToken cancellationToken)
    {
        var outputFullPath = ArchiveCreateValidator.Prepare(inputs, settings);
        var format = settings.ResolvedFormat;

        if (format.CreateFormat is null || !format.CanCreate)
        {
            throw new ArchiveConstraintException(ArchiveConstraintReason.FormatNotSupported);
        }

        // ZIP 的写入端在 7-Zip 里要求密码是纯 ASCII；提前说清楚，
        // 而不是让引擎抛一个看不懂的 Win32 87。
        if (settings.HasPassword
            && !format.SupportsNonAsciiPassword
            && !settings.Password!.All(char.IsAscii))
        {
            throw new ArchiveConstraintException(ArchiveConstraintReason.NonAsciiPasswordNotSupported);
        }

        var wantsVolumes = settings.HasVolume && format.SupportsVolume;
        // 两族分卷不是一条路：7z 由引擎逐卷写（x.7z.001…，首卷就是入口），中间不落整包；
        // ZIP 那一族是 WinZip 命名，引擎不产它，所以先把整包写到一个中间文件里再切块。
        var engineVolumes = wantsVolumes && format.IsSevenZip;

        // 写入目标有三种：
        //   普通      直接写最终文件（7z 分卷也走这条，卷由引擎分）
        //   分卷      先写中间文件再切块 —— Split 会先删掉它的目标路径，所以不能就地切
        //   复合(tgz) 先写 <stem>.tar 中间文件，再把它整体压成最终文件
        string? staging = (wantsVolumes && !engineVolumes) || format.IsCompound
            ? (wantsVolumes && !engineVolumes ? outputFullPath + ".tmp-full" : CompoundStagingPath(outputFullPath))
            : null;
        var innerTarget = staging ?? outputFullPath;
        var plan = ArchivePlanBuilder.Build(inputs, settings, innerTarget);

        // 单流格式只能装一个成员，这是格式的限制而不是实现的限制。
        // 判据用展开后的计划而不是输入个数：一个文件夹输入也会展开成多个成员。
        if (string.Equals(format.CreateFormat, "gzip", StringComparison.OrdinalIgnoreCase)
            && !format.IsCompound
            && (plan.Files.Count + plan.Directories.Count != 1))
        {
            throw new ArchiveConstraintException(ArchiveConstraintReason.GzRequiresSingleFile);
        }

        List<string> created;
        var warnings = new List<string>();
        try
        {
            if (engineVolumes)
            {
                // 引擎逐卷是"创建"而不是覆盖：卷号已被上一次的残留占了位，这一次会直接报
                // 文件已存在。旧 sets 留下的 .005 更坏 —— 三包的结果看起来像四包。
                SplitVolumeSupport.DeleteStaleSevenZipParts(outputFullPath);
            }

            warnings.AddRange(CreateOne(format.CreateFormat!, plan, settings, innerTarget, cancellationToken));

            if (format.IsCompound)
            {
                // 外层只装一个成员（内层归档）。密码与注释属于容器的语义，压一层流时不再传，
                // 否则 gzip 会因为收到它不支持的属性而整个写入失败。
                var innerSize = NewSize(innerTarget);
                var outerPlan = new ArchivePlan(
                    [new ArchivePlanEntry(Path.GetFileName(innerTarget), innerTarget, innerSize)],
                    [],
                    innerSize);
                warnings.AddRange(CreateOne(
                    format.CreateWrapper!,
                    outerPlan,
                    settings with { Method = null, Password = null, EncryptHeaders = false },
                    outputFullPath,
                    cancellationToken));
            }

            if (engineVolumes)
            {
                // 引擎已经一卷一卷写完了，这里只是把落地的卷数清楚 —— 没有整包可切。
                created = [.. SplitVolumeSupport.EnumerateSevenZipParts(outputFullPath)];
            }
            else if (wantsVolumes)
            {
                SplitVolumeSupport.DeleteStaleParts(outputFullPath);
                created = [.. SplitVolumeSupport.Split(
                    innerTarget,
                    outputFullPath,
                    settings.VolumeSizeBytes!.Value,
                    overwrite: true,
                    cancellationToken)];
            }
            else
            {
                created = [outputFullPath];
            }
        }
        finally
        {
            if (staging is not null)
            {
                ArchiveOutputGuard.TryDelete(staging);
            }
        }

        // 「文件说明」需要逐条目注释，7-Zip 的写入端只在 ZIP 上支持且走的是字节截断的
        // 窄路径，本引擎没有把它接到 ABI 上。与其静默丢弃用户的输入，明确说没做。
        if (settings.HasEntryComment)
        {
            warnings.Add("entry comments are not supported by the native engine and were not written");
        }

        return new ArchiveCreationResult(
            outputFullPath,
            plan.Files.Count,
            plan.Directories.Count,
            plan.TotalInputBytes,
            created.Sum(NewSize),
            created,
            warnings);
    }

    /// <summary>
    /// 复合格式的中间文件名。用 <c>&lt;stem&gt;.tar</c> 而不是随机临时名：它会成为外层流的
    /// 成员名，用户用别的工具解开 .tgz 时看到的就是 <c>k9.tar</c>。已有同名文件时改用唯一名，
    /// 绝不覆盖用户的数据。
    /// </summary>
    private static string CompoundStagingPath(string outputFullPath)
    {
        var directory = Path.GetDirectoryName(outputFullPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(outputFullPath);
        var candidate = Path.Combine(directory, stem + ".tar");
        return File.Exists(candidate)
            ? Path.Combine(directory, $"{stem}.{Guid.NewGuid():N}.tar")
            : candidate;
    }

    /// <summary>用引擎写一个归档；返回引擎提示（被忽略的设置项）。</summary>
    private static IReadOnlyList<string> CreateOne(
        string engineFormat,
        ArchivePlan plan,
        ArchiveCreationSettings settings,
        string outputPath,
        CancellationToken cancellationToken)
    {
        // 先确认这个目标此刻真的能写（见 EnsureTargetWritable），再让引擎去开它。
        EnsureTargetWritable(outputPath);
        var options = NativeEngine.BuildCreateOptions(engineFormat, settings, settings.Progress);

        try
        {
            using var writer = NativeArchiveWriter.Create(outputPath, options);

            foreach (var directory in plan.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.AddDirectory(directory.TrimEnd('/'));
            }

            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 目录树已由 ArchivePlanBuilder 展开，这里逐条加入以便保留“排除 Thumbs.db /
                // 不自我包含 / 是否带根目录”这些 Core 侧策略。
                writer.AddFile(file.SourcePath, file.EntryPath, meta: null, recurse: false);
            }

            writer.Finish();
            return writer.Warnings;
        }
        catch
        {
            // 与参考行为一致：失败或取消时不留半成品。
            ArchiveOutputGuard.TryDelete(outputPath);
            throw;
        }
    }

    /// <summary>
    /// 目标已经存在时，先按<strong>引擎自己那一份访问方式</strong>试开一次。
    /// <para>
    /// 引擎写包走的是 <c>COutFile::Create_ALWAYS</c>：<c>GENERIC_WRITE</c> 配
    /// <c>FILE_SHARE_READ</c>（third_party 的 <c>CPP/Windows/FileIO.cpp</c>）—— 也就是说
    /// <strong>只要还有任何人开着这个文件</strong>（另一个程序，或者本程序自己那个正在显示
    /// 这个包的窗口），创建就整单失败，而引擎能说的只有一句
    /// <c>cannot create '&lt;路径&gt;'</c>。用户看到的是"换个压缩方法还是失败"，
    /// 因为失败跟方法确实无关。这里用同样的 access + share 试开：开得动就立刻关掉交给引擎，
    /// 开不动就抛一个说得出名字的原因。
    /// </para>
    /// <para>
    /// 只查"已存在"的那一种：目标不存在时失败的原因是目录不可写或盘满，那是另一类话，
    /// 而且这种文件本来也不归这一步管（引擎会自己报）。
    /// </para>
    /// </summary>
    private static void EnsureTargetWritable(string outputPath)
    {
        if (!File.Exists(outputPath))
        {
            return;
        }

        try
        {
            using var probe = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveFailureException(ArchiveFailureReason.TargetLocked, outputPath, ex);
        }
    }

    /// <summary>“把每个文件/文件夹添加到单独的压缩文件”：每个输入各生成一个以其名称命名的压缩包。</summary>
    private static ArchiveCreationResult CreateSeparateArchives(
        IReadOnlyList<ArchiveInputItem> inputs,
        ArchiveCreationSettings settings,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(settings.OutputPath))
                              ?? Directory.GetCurrentDirectory();
        var extension = settings.ResolvedFormat.Extension;
        var warnings = new List<string>();

        var createdPaths = new List<string>();
        int fileCount = 0, directoryCount = 0;
        long inputBytes = 0, outputBytes = 0;

        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 文件使用去扩展名的主名（one.txt → one.zip），文件夹使用目录名（docs → docs.zip）
            var baseName = input.IsDirectory ? input.Name : Path.GetFileNameWithoutExtension(input.Name);
            var perOutput = Path.Combine(outputDirectory, baseName + extension);
            var perSettings = settings with
            {
                OutputPath = perOutput,
                SeparateArchives = false,
                TestAfterCreation = false,
                DeleteSourceAfterCreation = false,
            };

            var result = CreateSingleArchive([input], perSettings, cancellationToken);
            createdPaths.AddRange(result.CreatedArchives);
            fileCount += result.FileCount;
            directoryCount += result.DirectoryCount;
            inputBytes += result.TotalInputBytes;
            outputBytes += result.OutputBytes;
            warnings.AddRange(result.Warnings ?? []);
        }

        return new ArchiveCreationResult(
            settings.OutputPath,
            fileCount,
            directoryCount,
            inputBytes,
            outputBytes,
            createdPaths,
            warnings);
    }

    private static long NewSize(string path) => new FileInfo(path).Length;
}
