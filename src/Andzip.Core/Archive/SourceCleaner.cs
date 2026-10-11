namespace Andzip.Core.Archive;

/// <summary>
/// “压缩后删除原始文件”实现：创建成功后删除输入项。
/// 保护规则：若某个输入目录内包含任一产物（例如输出文件落在被压缩目录内），跳过该输入，避免误删产物。
/// </summary>
public static class SourceCleaner
{
    public static void Delete(IReadOnlyList<ArchiveInputItem> inputs, IReadOnlyList<string> protectedPaths)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        protectedPaths ??= [];

        var protectedFullPaths = protectedPaths.Select(Path.GetFullPath).ToList();

        foreach (var input in inputs)
        {
            var fullPath = Path.GetFullPath(input.FullPath);

            if (protectedFullPaths.Any(p => IsUnder(p, fullPath)))
            {
                continue;
            }

            if (input.IsDirectory)
            {
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                }
            }
            else if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        if (path.Equals(directory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}