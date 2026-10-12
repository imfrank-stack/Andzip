namespace Andzip.Core.Archive;

/// <summary>
/// 压缩包<strong>顶层</strong>的形状。「解压」按它决定内容铺在哪儿 ——
/// 分成三种不是为了分类好看，而是用户能直接感知的三种预期：单文件包想直接拿到那个文件、
/// 整棵目录树想直接拿到那个目录、零散一堆才需要一个袋子把它们装起来。
/// </summary>
public enum ArchiveTopLevel
{
    /// <summary>顶层只有一个文件（<c>.gz</c> / <c>.xz</c> 这类单流压缩包天然是这种）。</summary>
    SingleFile,

    /// <summary>所有条目都在同一个顶层文件夹里。</summary>
    SingleFolder,

    /// <summary>多个平级文件或平级目录混在一起。</summary>
    Mixed,
}

/// <summary>
/// <see cref="ArchiveTopLevel"/> 加上判定过程中顺手拿到的那个顶层名。
/// </summary>
/// <param name="Kind">顶层形状。</param>
/// <param name="RootName">
/// <see cref="ArchiveTopLevel.SingleFile"/> 时就是那个文件的名字 —— 要照它建同名文件夹就得知道
/// 叫什么，而它跟压缩包的名字并不总是一致（<c>photo.jpg.gz</c> 里装的是 <c>photo.jpg</c>）。
/// 其余情形为 null。
/// </param>
public readonly record struct ArchiveShape(ArchiveTopLevel Kind, string? RootName);

/// <summary>
/// 解压的落点判定。
/// <para>
/// <see cref="Inspect"/> 只看清单的顶层，不碰数据；<see cref="Destination"/> 把它翻译成目录。
/// </para>
/// </summary>
public static class ExtractionLayout
{
    /// <summary>
    /// 顶层只有一个条目且它是文件 → <see cref="ArchiveTopLevel.SingleFile"/>；所有条目共享同一个
    /// 顶层目录 → <see cref="ArchiveTopLevel.SingleFolder"/>；其余（含空包）→
    /// <see cref="ArchiveTopLevel.Mixed"/>。
    /// </summary>
    public static ArchiveShape Inspect(ArchiveListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var rootFiles = new HashSet<string>(StringComparer.Ordinal);
        var rootFolders = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in listing.Entries)
        {
            // 清单里的路径恒用 '/' 分隔，但目录条目带结尾斜杠、也偶尔见到反斜杠与开头的斜杠，
            // 这里一次归一，免得判定被 "" 和 "a" 当成两个不同的顶层名。
            var path = (entry.Path ?? string.Empty).Replace('\\', '/').Trim('/');
            if (path.Length == 0)
            {
                continue;
            }

            var separator = path.IndexOf('/');
            if (separator < 0)
            {
                if (entry.IsDirectory)
                {
                    rootFolders.Add(path);
                }
                else
                {
                    rootFiles.Add(path);
                }
            }
            else
            {
                // 深层条目：它的顶层一定是个目录，哪怕包里根本没写那条目录条目
                // （很多 ZIP 只存文件）。
                rootFolders.Add(path[..separator]);
            }
        }

        if (rootFiles.Count + rootFolders.Count != 1)
        {
            // 0 个（空包）也算 Mixed：真要放东西的时候，宁可往一个明确的袋子里放。
            return new ArchiveShape(ArchiveTopLevel.Mixed, null);
        }

        return rootFolders.Count == 1
            ? new ArchiveShape(ArchiveTopLevel.SingleFolder, null)
            : new ArchiveShape(ArchiveTopLevel.SingleFile, rootFiles.First());
    }

    /// <summary>
    /// 这个形状该解压到哪个目录。返回的是<strong>内容铺进去的那个目录</strong>。
    /// <para>
    /// <see cref="ArchiveTopLevel.SingleFile"/> 与 <see cref="ArchiveTopLevel.SingleFolder"/> 都铺在
    /// 压缩包所在目录 —— 于是那个文件或那个目录直接出现在眼前，<strong>不会</strong>再套一层与包
    /// 同名的空壳；<see cref="ArchiveTopLevel.Mixed"/> 才新建一个以压缩包命名的文件夹收纳。
    /// </para>
    /// </summary>
    /// <param name="singleFileToFolder">
    /// 「设置 → 解压 → 单个文件也解压到新建文件夹」。只影响单文件情形：建一个以<strong>那个文件</strong>
    /// （不是压缩包）命名的文件夹。默认关 —— 压一个 <c>photo.jpg.gz</c> 却得到
    /// <c>photo.jpg\photo.jpg</c> 是多数人没预期的结果。
    /// </param>
    public static string Destination(string archivePath, ArchiveShape shape, bool singleFileToFolder = false)
        => shape.Kind switch
        {
            ArchiveTopLevel.SingleFile when singleFileToFolder && shape.RootName is { Length: > 0 } name
                => ShellTasks.UniqueFolder(Path.Combine(
                    ShellTasks.ParentFolder(archivePath), Path.GetFileNameWithoutExtension(name))),
            ArchiveTopLevel.SingleFile or ArchiveTopLevel.SingleFolder
                => ShellTasks.ParentFolder(archivePath),
            _ => ShellTasks.UniqueFolder(ShellTasks.SiblingFolder(archivePath)),
        };
}
