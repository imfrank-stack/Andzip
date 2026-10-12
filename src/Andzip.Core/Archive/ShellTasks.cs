namespace Andzip.Core.Archive;

/// <summary>
/// 右键菜单 / 双击要执行的任务。注册表里每个动词的命令行只带一个 <c>/task=…</c>，
/// 具体做什么由这个枚举决定 —— 与「双击行为」一样，行为放在命令行里，不放在动词键是否存在上。
/// </summary>
/// <remarks>
/// 0.9.12 之前这里有 <c>Extract</c>（固定套一层同名文件夹）/ <c>AutoExtract</c>（看包里的形状）/
/// <c>ExtractHere</c>（就地铺开）三条解压，界面上叫「智能解压」与「解压到当前文件夹」。
/// 用户的要求是"合并成一个功能，软件里不再出现智能解压"，所以只剩 <see cref="Extract"/> 一条，
/// 落点规则沿用原来那套内容判断 —— 一整棵单子树的包得到 <c>docs\</c> 而不是 <c>docs\docs\</c>，
/// 这本来就该是「解压」默认的样子。
/// </remarks>
public enum ShellTask
{
    /// <summary>在 Andzip 窗口里浏览压缩包内容。右键「用 Andzip 打开」走这条，<strong>不</strong>受双击设置影响。</summary>
    Open,

    /// <summary>解压：落点按包里的形状决定（见 <see cref="ExtractionLayout"/>）。</summary>
    Extract,

    /// <summary>解压，成功后把包删掉（分卷包连卷一起删）。</summary>
    ExtractDelete,

    /// <summary>压缩成同名 .zip。</summary>
    CompressZip,

    /// <summary>打开「新建压缩文件」对话框，把选中的路径预填进去，由用户点开始。</summary>
    CompressDialog,
}

/// <summary>右键任务的命令行写法与落点规则。</summary>
public static class ShellTasks
{
    /// <summary>任务开关前缀。命令行形如 <c>Andzip.exe "C:\a.7z" /task=extract</c>。</summary>
    public const string SwitchPrefix = "/task=";

    /// <summary>v0.9.4 之前注册的裸开关，读到就当 Extract，保证老注册项不用重做也能用。</summary>
    public const string LegacyExtractSwitch = "/extract";

    public static string Switch(ShellTask task) => SwitchPrefix + Name(task);

    public static string Name(ShellTask task) => task switch
    {
        ShellTask.Extract => "extract",
        ShellTask.ExtractDelete => "extract-delete",
        ShellTask.CompressZip => "compress-zip",
        ShellTask.CompressDialog => "compress-dialog",
        _ => "open",
    };

    /// <summary>
    /// 从命令行里取任务。没有任务开关时返回 false，由调用方按「双击」设置决定
    /// （那是 <see cref="Settings.SettingKeys.ShouldExtractDirectly"/> 的职责，不在这里越权）。
    /// </summary>
    /// <remarks>
    /// 三条老写法仍然认：<c>auto-extract</c> 与 <c>extract-here</c> 是 0.9.12 及以前的两个解压动词，
    /// <c>compress-7z</c> 是撤掉的压缩动词。动词键在写注册表时会被删掉，但用户桌面上可能还留着
    /// 指向老命令行的快捷方式 —— 认成现在最接近的那一条，比退回"打开"更合用户预期。
    /// </remarks>
    public static bool TryParse(IEnumerable<string>? arguments, out ShellTask task)
    {
        task = ShellTask.Open;
        var found = false;

        foreach (var argument in arguments ?? [])
        {
            if (argument.StartsWith(SwitchPrefix, StringComparison.OrdinalIgnoreCase))
            {
                task = argument[SwitchPrefix.Length..].Trim().ToLowerInvariant() switch
                {
                    "extract" or "auto-extract" or "extract-here" => ShellTask.Extract,
                    "extract-delete" => ShellTask.ExtractDelete,
                    "compress-zip" or "compress-7z" => ShellTask.CompressZip,
                    "compress-dialog" => ShellTask.CompressDialog,
                    _ => ShellTask.Open,
                };
                found = true;
            }
            else if (argument.Equals(LegacyExtractSwitch, StringComparison.OrdinalIgnoreCase))
            {
                task = ShellTask.Extract;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// 这条任务要不要先问口令。<see cref="ShellTask.Open"/> 不要 —— 打开时口令在真正读内容那一刻才需要。
    /// </summary>
    public static bool IsExtraction(ShellTask task)
        => task is ShellTask.Extract or ShellTask.ExtractDelete;

    /// <summary>所在目录。</summary>
    public static string ParentFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("路径没有父目录：" + path);
    }

    /// <summary>
    /// 同目录下一个与压缩包同名的文件夹。
    /// 复合后缀只剥最后一段：<c>archive.tar.gz</c> 解到 <c>archive.tar</c>，
    /// 与「选择解压路径」里的同名规则保持一致，不另起一套。
    /// </summary>
    public static string SiblingFolder(string archivePath)
        => Path.Combine(ParentFolder(archivePath), Path.GetFileNameWithoutExtension(archivePath));

    /// <summary>
    /// 压缩输出的默认文件名：与源同名的兄弟位置，扩展名换成目标格式。
    /// 文件夹用文件夹名，文件去掉它自己的扩展名（<c>photo.jpg</c> → <c>photo.zip</c>）。
    /// </summary>
    /// <param name="isDirectory">由调用方判断并传进来：Core 不去猜一个路径是不是目录，
    /// 因为目录名里带点（<c>my.app</c>）时剥掉"扩展名"会得到与文件夹对不上的包名。</param>
    public static string ArchivePathFor(string sourcePath, string extension, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var full = Path.GetFullPath(sourcePath);
        var directory = ParentFolder(full);
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);

        return Path.Combine(directory, (stem.Length == 0 ? "archive" : stem) + extension);
    }

    /// <summary>
    /// 已存在同名压缩包时往后加序号，而不是覆盖 —— 右键压缩是用户随手点的，
    /// 静默删掉一个已有的包是不可原谅的。
    /// </summary>
    public static string UniqueArchivePath(string preferred)
    {
        if (!File.Exists(preferred))
        {
            return preferred;
        }

        var directory = Path.GetDirectoryName(preferred)!;
        var stem = Path.GetFileNameWithoutExtension(preferred);
        var extension = Path.GetExtension(preferred);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return preferred;
    }

    /// <summary>
    /// 新建解压目录时的同名避让：<c>photos</c> 已被占用就依次试 <c>photos (2)</c>、<c>photos (3)</c>……
    /// <para>
    /// 与 <see cref="UniqueArchivePath"/> 不同，这里<strong>不</strong>把最后一段当扩展名剥掉：
    /// 解压目标 <c>photos.tar</c> 撞名时要得到 <c>photos.tar (2)</c>，而不是 <c>photos (2).tar</c>。
    /// 判占用时文件也算 —— 一个与目标目录同名的<strong>文件</strong>同样会让建目录失败。
    /// </para>
    /// </summary>
    public static string UniqueFolder(string preferred)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferred);

        if (IsFree(preferred))
        {
            return preferred;
        }

        var directory = Path.GetDirectoryName(preferred)
            ?? throw new InvalidOperationException("路径没有父目录：" + preferred);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(preferred));

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(directory, $"{name} ({n})");
            if (IsFree(candidate))
            {
                return candidate;
            }
        }

        return preferred;

        static bool IsFree(string path) => !File.Exists(path) && !Directory.Exists(path);
    }
}
