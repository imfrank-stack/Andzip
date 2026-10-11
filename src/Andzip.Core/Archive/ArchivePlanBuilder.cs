namespace Andzip.Core.Archive;

internal sealed record ArchivePlanEntry(string EntryPath, string SourcePath, long Size);

internal sealed record ArchivePlan(
    IReadOnlyList<ArchivePlanEntry> Files,
    IReadOnlyList<string> Directories,
    long TotalInputBytes);

/// <summary>把输入项展开为压缩包内的条目清单（纯逻辑，供各格式引擎共用并便于单元测试）。</summary>
internal static class ArchivePlanBuilder
{
    /// <summary>参考软件默认不打包的缩略图缓存（原来挂在已删除的 ZipArchiveCreator 上）。</summary>
    public const string ThumbsDbName = "Thumbs.db";
    public static ArchivePlan Build(
        IReadOnlyList<ArchiveInputItem> inputs,
        ArchiveCreationSettings settings,
        string outputFullPath)
    {
        var files = new List<ArchivePlanEntry>();
        var directories = new List<string>();
        long totalBytes = 0;

        // 单个文件夹且“不创建根文件夹”时，条目相对该文件夹（去掉一层根目录）
        var stripSingleRoot = inputs.Count == 1
            && inputs[0].IsDirectory
            && !settings.IncludeRootFolder;

        foreach (var input in inputs)
        {
            if (!input.IsDirectory)
            {
                if (ShouldExclude(Path.GetFileName(input.FullPath), settings))
                {
                    continue;
                }

                var size = new FileInfo(input.FullPath).Length;
                files.Add(new ArchivePlanEntry(NormalizeEntryPath(input.Name), input.FullPath, size));
                totalBytes += size;
                continue;
            }

            var rootBase = Path.GetFullPath(input.FullPath);
            var prefix = stripSingleRoot ? string.Empty : input.Name;

            foreach (var directory in Directory.EnumerateDirectories(rootBase, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(rootBase, directory);
                directories.Add(CombineEntryPath(prefix, relative) + "/");
            }

            foreach (var file in Directory.EnumerateFiles(rootBase, "*", SearchOption.AllDirectories))
            {
                var fileFullPath = Path.GetFullPath(file);
                if (string.Equals(fileFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 输出文件位于输入目录内时跳过，避免自我包含
                }

                if (ShouldExclude(Path.GetFileName(file), settings))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(rootBase, file);
                var size = new FileInfo(file).Length;
                files.Add(new ArchivePlanEntry(CombineEntryPath(prefix, relative), file, size));
                totalBytes += size;
            }
        }

        // 目录条目去重（多个输入项可能产生重复），保证没有对应文件的空目录也会入库
        var distinctDirectories = directories.Distinct(StringComparer.Ordinal).ToList();
        return new ArchivePlan(files, distinctDirectories, totalBytes);
    }

    private static bool ShouldExclude(string fileName, ArchiveCreationSettings settings)
        => (settings.ExcludeThumbsDb && fileName.Equals(ThumbsDbName, StringComparison.OrdinalIgnoreCase))
           || FileNamePattern.MatchesAny(settings.ResolvedExcludePatterns, fileName);

    private static string CombineEntryPath(string prefix, string relative)
    {
        var normalized = NormalizeEntryPath(relative);
        return string.IsNullOrEmpty(prefix) ? normalized : prefix + "/" + normalized;
    }

    private static string NormalizeEntryPath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}

/// <summary>各格式创建引擎共用的输入校验与输出准备。</summary>
internal static class ArchiveCreateValidator
{
    public static string Prepare(IReadOnlyList<ArchiveInputItem> inputs, ArchiveCreationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.OutputPath);

        if (inputs.Count == 0)
        {
            throw new EmptyArchiveInputException();
        }

        var outputFullPath = Path.GetFullPath(settings.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputFullPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        return outputFullPath;
    }
}

/// <summary>失败/取消时清理半成品输出（与参考行为一致：不留损坏产物）。</summary>
internal static class ArchiveOutputGuard
{
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失败不掩盖原始异常
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}