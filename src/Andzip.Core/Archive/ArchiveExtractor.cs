using NativeArchiveReader = Andzip.Native.ArchiveReader;

namespace Andzip.Core.Archive;

/// <summary>
/// 解压门面（FR-3）。
/// <para>
/// 职责划分：引擎负责“把字节解出来”，Core 负责“落到磁盘的哪里”。重名策略（询问 / 自动重命名）、
/// 非法文件名清洗、智能解压剥顶层目录、zip-slip 防护都是文件系统的策略问题，引擎只提供
/// 覆盖/跳过两档，所以这些留在 Core。
/// </para>
/// <para>
/// 两条落地路径不是冗余：默认走整包原生解压（固实归档只需解码一次，逐条读会把固实块重复解压）；
/// 只有确实需要改名、清洗非法字符或剥顶层目录时才退到逐条解压 —— 那三种情况下目标文件名
/// 已经和归档里存的不一样，整包解压无法表达。
/// </para>
/// </summary>
public static class ArchiveExtractor
{
    public static ExtractionResult Extract(ExtractionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.DestinationPath);

        var archivePath = Path.GetFullPath(settings.ArchivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Archive to extract was not found.", archivePath);
        }

        var destination = Path.GetFullPath(settings.DestinationPath);

        var result = NativeEngine.Run("extract archive", () =>
        {
            using var single = SingleFileArchive.Open(archivePath, cancellationToken);
            using var reader = ArchiveReader.OpenNative(single.FilePath, settings.Password);

            var plan = BuildPlan(reader, destination, settings, cancellationToken);

            Directory.CreateDirectory(destination);

            return plan.NeedsPerEntry
                ? ExtractEntryByEntry(reader, destination, plan, settings, cancellationToken)
                : ExtractWholeArchive(reader, destination, plan, settings, cancellationToken);
        }, badFormatMeansUnsupported: true);

        // 删包必须在 reader 释放之后：归档句柄还开着时 File.Delete 会失败，
        // 而“解压后删除压缩包”是用户明确勾选的，不能静默不生效。
        if (settings.DeleteArchiveAfterExtraction)
        {
            TryDeleteArchive(archivePath, result);
        }

        return result;
    }

    /// <summary>
    /// 是否需要密码。头加密的 7z 在没密码时连打开都会失败，这种“失败即需要密码”也返回 true，
    /// 因此比旧的“能打开才看得到位位”更准。任何其它失败都返回 false（交给正式解压报错）。
    /// <para>
    /// 注意引擎对"头加密且没给口令"报的是 <c>BAD_FORMAT</c> 而不是 <c>PASSWORD</c>
    /// （7-Zip 的处理器只回一句"这不是我的格式"），所以那一类要靠签名改判 ——
    /// 不这么判的话解压路径连口令框都不会弹，见 <see cref="EncryptedHeaderProbe"/>。
    /// </para>
    /// </summary>
    public static bool IsPasswordProtected(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return false;
        }

        try
        {
            using var single = SingleFileArchive.Open(Path.GetFullPath(archivePath));
            using var reader = ArchiveReader.OpenNative(single.FilePath, password: null);
            return reader.IsEncrypted || reader.Entries.Any(entry => entry.IsEncrypted);
        }
        catch (Andzip.Native.AndzipException ex) when (ex.Result == Andzip.Native.AzResult.ErrorPassword)
        {
            return true;
        }
        catch (Andzip.Native.AndzipException ex)
            when (ex.Result is Andzip.Native.AzResult.ErrorBadFormat or Andzip.Native.AzResult.ErrorUnsupported
                  && EncryptedHeaderProbe.HasSevenZipSignature(archivePath))
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 只把指定的几个成员解出来（「编辑 → 将文件复制到剪贴板」把选中的东西落到临时目录时用它）。
    /// <para>与 <see cref="Extract(ExtractionSettings, CancellationToken)"/> 的差别只有两点：
    /// 只解列出的那几个成员，并且<strong>原样保留包内路径</strong> —— 不剥顶层目录、不清洗文件名、
    /// 不查重名。剪贴板里那几个文件的路径要能对上用户刚看见的那棵树，任何"顺手整理"都是在拆台。</para>
    /// <para><paramref name="memberIndices"/> 里若有目录，它底下每一个成员的展开由调用方完成
    /// （界面那边有完整的路径树）；这里只补上<strong>沿途的目录项</strong>，
    /// 否则包里的空目录不会出现。</para>
    /// </summary>
    public static ExtractionResult ExtractMembers(
        string archivePath,
        IReadOnlyList<int> memberIndices,
        string destinationPath,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memberIndices);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Archive to extract was not found.", fullPath);
        }

        var destination = Path.GetFullPath(destinationPath);

        return NativeEngine.Run("extract archive members", () =>
        {
            using var single = SingleFileArchive.Open(fullPath, cancellationToken);
            using var reader = ArchiveReader.OpenNative(single.FilePath, password);

            var entries = reader.Entries;
            var picked = new HashSet<int>();
            foreach (var index in memberIndices)
            {
                if (index < 0 || index >= entries.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(memberIndices), index, "这个包里没有这个下标的成员。");
                }

                picked.Add(index);
            }

            var indices = ResolveMemberIndices(entries, picked, cancellationToken);

            Directory.CreateDirectory(destination);

            var options = NativeEngine.BuildExtractOptions(
                password,
                Andzip.Native.AzOverwrite.Overwrite,
                preserveMTime: true,
                progress);
            reader.Extract(indices, destination, options);

            cancellationToken.ThrowIfCancellationRequested();
            return SummarizeMembers(entries, indices, destination);
        }, badFormatMeansUnsupported: true);
    }

    /// <summary>
    /// 选中的下标 → 真正要交给引擎的那一串。目录项与它们的祖先目录都要在里面：
    /// "我选了整个文件夹"本身就包含"这个文件夹要在"，少了它界面上的那棵树会缺一层。
    /// </summary>
    private static List<ulong> ResolveMemberIndices(
        IReadOnlyList<Andzip.Native.ArchiveEntry> entries,
        IReadOnlySet<int> picked,
        CancellationToken cancellationToken)
    {
        var neededDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in picked)
        {
            var path = NormalizeEntryPath(entries[index].Name);
            if (entries[index].IsDirectory)
            {
                neededDirs.Add(path);
            }

            for (var cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
            {
                neededDirs.Add(path[..cut]);
            }
        }

        var indices = new List<ulong>(picked.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[index];
            var wanted = picked.Contains(index)
                || (entry.IsDirectory && neededDirs.Contains(NormalizeEntryPath(entry.Name)));
            if (wanted)
            {
                indices.Add((ulong)index);
            }
        }

        return indices;
    }

    private static ExtractionResult SummarizeMembers(
        IReadOnlyList<Andzip.Native.ArchiveEntry> entries,
        IReadOnlyList<ulong> indices,
        string destination)
    {
        var files = new List<string>();
        int directories = 0;
        long totalBytes = 0;

        foreach (var index in indices)
        {
            var entry = entries[(int)index];
            var relative = NormalizeEntryPath(entry.Name);
            if (relative.Length == 0)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                directories++;
                continue;
            }

            files.Add(Path.GetFullPath(Path.Combine(destination, relative)));
            totalBytes += entry.Size is { } size ? (long)Math.Min(size, long.MaxValue) : 0L;
        }

        return new ExtractionResult(destination, files.Count, directories, totalBytes, 0, files);
    }

    private static string NormalizeEntryPath(string name)
        => name.Replace('\\', '/').Trim('/');

    // ------------------------------------------------------------------ 计划

    /// <summary>一个待落地条目：目标路径 + 是否目录。</summary>
    private sealed record Target(int EntryIndex, string DestinationPath, bool IsDirectory, long Size);

    /// <summary>解压前算好的完整落地计划。</summary>
    private sealed record ExtractionPlan(
        IReadOnlyList<Target> Directories,
        IReadOnlyList<Target> Files,
        /// <summary>需要改名（重名冲突选了自动重命名）或清洗过文件名的条目 —— 只能逐条解压。</summary>
        bool NeedsPerEntry,
        IReadOnlySet<int> Skipped,
        long TotalBytes);

    private static ExtractionPlan BuildPlan(
        NativeArchiveReader reader,
        string destination,
        ExtractionSettings settings,
        CancellationToken cancellationToken)
    {
        var directories = new List<Target>();
        var files = new List<Target>();
        var skipped = new HashSet<int>();
        long totalBytes = 0;
        var needsPerEntry = false;

        // 智能解压：只有当所有条目都挂在同一个顶层目录下时才剥掉那一层。
        // 顶层同时有文件和目录的包不剥（否则结构会被压平）。
        var commonRoot = settings.StripSingleRootFolder ? CommonSingleRoot(reader.Entries) : null;
        if (commonRoot is not null)
        {
            needsPerEntry = true;
        }

        var entries = reader.Entries;
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];

            var relative = RelativeFromEntry(entry.Name, commonRoot);

            // 引擎侧 SafeJoin 已挡住穿越，但目标路径由 Core 决定，所以这里再校验一次：
            // 任何跳出目标目录的路径都是攻击特征，直接失败而不是静默跳过。
            if (!TrySafeCombine(destination, relative, out var target))
            {
                throw new InvalidDataException(
                    $"archive entry '{entry.Name}' resolves outside the destination directory");
            }

            var sanitized = SanitizePath(target);
            if (!string.Equals(sanitized, target, StringComparison.Ordinal))
            {
                needsPerEntry = true;
            }

            if (entry.IsDirectory)
            {
                directories.Add(new Target(index, sanitized, IsDirectory: true, Size: 0));
                continue;
            }

            var size = entry.Size is { } entrySize ? (long)Math.Min(entrySize, long.MaxValue) : 0L;
            totalBytes += size;

            var decision = Decide(sanitized, entry.Name, settings);
            switch (decision)
            {
                case DuplicateDecision.Skip:
                    skipped.Add(index);
                    continue;
                case DuplicateDecision.Rename:
                    sanitized = BuildRenamedPath(sanitized);
                    needsPerEntry = true;
                    break;
            }

            files.Add(new Target(index, sanitized, IsDirectory: false, Size: size));
        }

        return new ExtractionPlan(directories, files, needsPerEntry, skipped, totalBytes);
    }

    /// <summary>所有条目共有的顶层目录名；不满足“唯一顶层且是目录”时返回 null。</summary>
    private static string? CommonSingleRoot(IReadOnlyList<Andzip.Native.ArchiveEntry> entries)
    {
        string? root = null;
        foreach (var entry in entries)
        {
            var segments = entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            // 顶层就是一个文件 —— 没有可剥的目录。
            if (segments.Length == 1 && !entry.IsDirectory)
            {
                return null;
            }

            if (root is null)
            {
                root = segments[0];
            }
            else if (!string.Equals(root, segments[0], StringComparison.Ordinal))
            {
                return null;
            }
        }

        return root;
    }

    /// <summary>条目路径 → 相对目标目录的路径，必要时剥掉公共顶层目录。</summary>
    private static string RelativeFromEntry(string entryName, string? commonRoot)
    {
        var relative = entryName.Replace('\\', '/').TrimStart('/');
        if (commonRoot is null)
        {
            return relative;
        }

        return relative.StartsWith(commonRoot + "/", StringComparison.Ordinal)
            ? relative[(commonRoot.Length + 1)..]
            : relative;
    }

    /// <summary>Windows 不允许文件名里出现 &lt;&gt;:"/\|?* 与控制字符；逐段替换为下划线。</summary>
    private static string SanitizePath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var leaf = Path.GetFileName(path);
        if (leaf.Length == 0)
        {
            return path;
        }

        var chars = leaf.ToCharArray();
        var changed = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c < 0x20 || Array.IndexOf(InvalidLeafChars, c) >= 0)
            {
                chars[i] = '_';
                changed = true;
            }
        }

        // 结尾的点与空格会被 Windows 悄悄吃掉，显式换成下划线以保证可预测。
        while (chars.Length > 0 && (chars[^1] == '.' || chars[^1] == ' '))
        {
            chars[^1] = '_';
            changed = true;
        }

        if (!changed)
        {
            return path;
        }

        return string.IsNullOrEmpty(directory) ? new string(chars) : Path.Combine(directory, new string(chars));
    }

    private static readonly char[] InvalidLeafChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>重名策略 → 本次落地的决定。</summary>
    private static DuplicateDecision Decide(string target, string entryName, ExtractionSettings settings)
    {
        if (!File.Exists(target))
        {
            return DuplicateDecision.Overwrite;
        }

        return settings.Overwrite switch
        {
            OverwritePolicy.Overwrite => DuplicateDecision.Overwrite,
            OverwritePolicy.Skip => DuplicateDecision.Skip,
            OverwritePolicy.Rename => DuplicateDecision.Rename,
            // Ask 且没有回调时按重命名处理，避免静默覆盖（与旧实现一致）。
            _ => settings.DuplicateResolver?.Invoke(entryName) ?? DuplicateDecision.Rename,
        };
    }

    /// <summary>
    /// 把归档内的相对路径安全地拼到目标目录下。返回 false 表示这是 zip-slip 尝试。
    /// </summary>
    private static bool TrySafeCombine(string destination, string relative, out string combined)
    {
        combined = string.Empty;
        if (Path.IsPathRooted(relative))
        {
            return false;
        }

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        if (segments.Any(s => s == ".."))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine([destination, .. segments]));
        var prefix = destination.EndsWith(Path.DirectorySeparatorChar)
            ? destination
            : destination + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        combined = candidate;
        return true;
    }

    /// <summary>参考软件的重命名样式：<c>name.ext</c> → <c>name (2).ext</c>，依次递增。</summary>
    private static string BuildRenamedPath(string target)
    {
        var directory = Path.GetDirectoryName(target) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return target;
    }

    // ------------------------------------------------------------------ 落地

    /// <summary>整包原生解压：一次解码，固实归档友好；目录、时间与名字都由引擎按归档原样处理。</summary>
    private static ExtractionResult ExtractWholeArchive(
        NativeArchiveReader reader,
        string destination,
        ExtractionPlan plan,
        ExtractionSettings settings,
        CancellationToken cancellationToken)
    {
        foreach (var directory in plan.Directories)
        {
            Directory.CreateDirectory(directory.DestinationPath);
        }

        var indices = plan.Files.Select(file => (ulong)file.EntryIndex).ToList();
        if (indices.Count > 0)
        {
            var options = NativeEngine.BuildExtractOptions(
                settings.Password,
                Andzip.Native.AzOverwrite.Overwrite,
                preserveMTime: true,
                // 整包解压时引擎自己上报字节级进度；逐条解压时由 Core 计算。
                settings.Progress);
            reader.Extract(indices, destination, options);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Summarize(destination, plan);
    }

    /// <summary>逐条解压：需要改名 / 清洗非法字符 / 剥顶层目录时才走，代价是固实归档会被多次解码。</summary>
    private static ExtractionResult ExtractEntryByEntry(
        NativeArchiveReader reader,
        string destination,
        ExtractionPlan plan,
        ExtractionSettings settings,
        CancellationToken cancellationToken)
    {
        foreach (var directory in plan.Directories)
        {
            Directory.CreateDirectory(directory.DestinationPath);
        }

        var done = 0L;
        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parent = Path.GetDirectoryName(file.DestinationPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using (var stream = new FileStream(
                file.DestinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1 << 16,
                // 整条链路是同步阻塞的解压任务，异步没有收益。
                useAsync: false))
            {
                reader.ReadEntry((ulong)file.EntryIndex, stream);
            }

            var modified = reader.Entries[file.EntryIndex].ModifiedTime;
            if (modified is { } timestamp)
            {
                File.SetLastWriteTimeUtc(file.DestinationPath, timestamp.UtcDateTime);
            }

            done += file.Size;
            settings.Progress?.Report(new ArchiveProgress(
                ArchiveOperation.Extract, done, plan.TotalBytes,
                Path.GetFileName(file.DestinationPath), 0, plan.Files.Count));
        }

        return Summarize(destination, plan);
    }

    private static ExtractionResult Summarize(string destination, ExtractionPlan plan)
        => new(
            destination,
            plan.Files.Count,
            plan.Directories.Count,
            plan.TotalBytes,
            plan.Skipped.Count,
            [.. plan.Files.Select(file => file.DestinationPath)]);

    /// <summary>
    /// 解压成功后才删包；删除失败不影响解压结果（与参考行为一致）。
    /// <para>分卷包连卷一起删：只删主卷会留下一地 <c>.z01</c>，那比一个不剩更糟。</para>
    /// </summary>
    private static void TryDeleteArchive(string archivePath, ExtractionResult result)
    {
        if (result.FileCount == 0 && result.DirectoryCount == 0)
        {
            return;
        }

        foreach (var part in SplitVolumeSupport.EnumerateParts(archivePath))
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
