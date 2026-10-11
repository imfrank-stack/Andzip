namespace Andzip.Core.Archive;

/// <summary>
/// 压缩包<strong>顶层</strong>的形状 —— 「智能解压」就是按它决定内容铺在哪儿。
/// <para>
/// 分成三种不是为了分类好看，而是用户能直接感知的三种预期：单文件包想直接拿到那个文件、
/// 整棵目录树想直接拿到那个目录、零散一堆才需要一个袋子把它们装起来。
/// </para>
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
/// 智能解压的落点判定。
/// <para>
/// <see cref="Inspect"/> 只看清单的顶层，不碰数据；<see cref="Destination"/> 把它翻译成目录：
/// 前两种情形就解压到压缩包所在目录（于是那个文件或那个目录直接出现在眼前，
/// <strong>不会</strong>再套一层与包同名的空壳），第三种新建一个以压缩包命名的文件夹收纳。
/// </para>
/// </summary>
public static class ExtractionLayout
{
    /// <summary>
    /// 顶层只有一个条目且它是文件 → <see cref="SingleFile"/>；所有条目共享同一个顶层目录 →
    /// <see cref="SingleFolder"/>；其余（含空包）→ <see cref="Mixed"/>。
    /// </summary>
    public static ArchiveTopLevel Inspect(ArchiveListing listing)
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

        var roots = rootFiles.Count + rootFolders.Count;
        if (roots != 1)
        {
            // 0 个（空包）也算 Mixed：真发生什么的时候，宁可往一个明确的袋子里放。
            return ArchiveTopLevel.Mixed;
        }

        return rootFolders.Count == 1 ? ArchiveTopLevel.SingleFolder : ArchiveTopLevel.SingleFile;
    }

    /// <summary>
    /// 这个形状的包该解压到哪个目录。返回的是<strong>内容铺进去的那个目录</strong>，
    /// 不是"新建的那个文件夹" —— 只有 <see cref="ArchiveTopLevel.Mixed"/> 时两者才不同。
    /// </summary>
    public static string Destination(string archivePath, ArchiveTopLevel shape)
        => shape switch
        {
            ArchiveTopLevel.SingleFile or ArchiveTopLevel.SingleFolder
                => ShellTasks.ParentFolder(archivePath),
            _ => ShellTasks.UniqueFolder(ShellTasks.SiblingFolder(archivePath)),
        };
}
