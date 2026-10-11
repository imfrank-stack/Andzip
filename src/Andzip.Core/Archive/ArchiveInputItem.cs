namespace Andzip.Core.Archive;

/// <summary>待压缩的输入项（文件或文件夹），对应“新建压缩文件”对话框列表中的一行（FR-2.1）。</summary>
public sealed record ArchiveInputItem(string FullPath, bool IsDirectory)
{
    public string Name => Path.GetFileName(
        FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>文件大小（字节）；文件夹返回 null（列表显示为空，递归统计留待后续里程碑）。</summary>
    public long? Size => IsDirectory ? null : new FileInfo(FullPath).Length;

    /// <summary>从磁盘路径创建；文件或文件夹必须存在。</summary>
    public static ArchiveInputItem FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Directory.Exists(path))
        {
            return new ArchiveInputItem(path, IsDirectory: true);
        }

        if (File.Exists(path))
        {
            return new ArchiveInputItem(path, IsDirectory: false);
        }

        throw new FileNotFoundException("Input path does not exist.", path);
    }
}