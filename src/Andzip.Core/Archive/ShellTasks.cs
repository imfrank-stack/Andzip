namespace Andzip.Core.Archive;

/// <summary>
/// 右键菜单 / 双击要执行的任务。注册表里每个动词的命令行只带一个 <c>/task=…</c>，
/// 具体做什么由这个枚举决定 —— 与「双击行为」一样，行为放在命令行里，不放在动词键是否存在上。
/// </summary>
public enum ShellTask
{
    /// <summary>在 Andzip 窗口里浏览压缩包内容。</summary>
    Open,

    /// <summary>解压到压缩包所在目录下一个与压缩包同名的文件夹。</summary>
    Extract,

    /// <summary>直接解压到压缩包所在目录，不套一层文件夹。</summary>
    ExtractHere,

    /// <summary>压缩成同名 .zip。</summary>
    CompressZip,

    /// <summary>压缩成同名 .7z。</summary>
    CompressSevenZip,
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
        ShellTask.ExtractHere => "extract-here",
        ShellTask.CompressZip => "compress-zip",
        ShellTask.CompressSevenZip => "compress-7z",
        _ => "open",
    };

    /// <summary>
    /// 从命令行里取任务。没有任务开关时返回 false，由调用方按「双击」设置决定
    /// （那是 <see cref="Settings.SettingKeys.ShouldExtractDirectly"/> 的职责，不在这里越权）。
    /// </summary>
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
                    "extract" => ShellTask.Extract,
                    "extract-here" => ShellTask.ExtractHere,
                    "compress-zip" => ShellTask.CompressZip,
                    "compress-7z" => ShellTask.CompressSevenZip,
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

    /// <summary>这个任务是不是「压缩」（决定走压缩执行体还是解压执行体）。</summary>
    public static bool IsCompression(ShellTask task)
        => task is ShellTask.CompressZip or ShellTask.CompressSevenZip;

    /// <summary>
    /// 解压落点。<c>Extract</c> 到压缩包旁边的同名文件夹，<c>ExtractHere</c> 就在压缩包所在目录里铺开。
    /// 非解压任务没有落点可言，交给调用方的分支去保证。
    /// </summary>
    public static string ExtractDestination(string archivePath, ShellTask task) => task switch
    {
        ShellTask.Extract => SiblingFolder(archivePath),
        ShellTask.ExtractHere => ParentFolder(archivePath),
        _ => throw new ArgumentOutOfRangeException(nameof(task), task, "只有解压任务有解压落点。"),
    };

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
}
