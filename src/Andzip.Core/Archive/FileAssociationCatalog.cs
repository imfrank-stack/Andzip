namespace Andzip.Core.Archive;

/// <summary>
/// 文件关联页上的一个候选项：一个显示标签 + 它要注册的一组扩展名。
/// </summary>
/// <param name="Id">稳定标识（取主扩展名），用于设置存储与 ProgID 命名，不随显示文案变化。</param>
/// <param name="Label">界面上的显示名（ZIP / 7Z / TAR…）。格式名是专有名词，不进语言包。</param>
/// <param name="Extensions">小写、不含点的扩展名集合；只要有一个能被引擎读到，本项就可用。</param>
public sealed record FileAssociation(string Id, string Label, IReadOnlyList<string> Extensions)
{
    /// <summary>引擎可读集合里出现任一扩展名即为“支持”。集合为空时一律不支持（引擎缺失的兜底）。</summary>
    public bool IsSupportedBy(IReadOnlyCollection<string> readableExtensions)
        => readableExtensions.Count > 0 && Extensions.Any(readableExtensions.Contains);
}

/// <summary>文件关联的一个分组（压缩包文件 / UNIX / 其它 / CD-DVD）。</summary>
/// <param name="TextKey">分组标题的语言包键名。</param>
public sealed record AssociationGroup(string TextKey, IReadOnlyList<FileAssociation> Items);

/// <summary>
/// 文件关联候选目录。
/// <para>
/// 这里刻意**不**把引擎能读的 60 种格式全列出来：APFS / NTFS / FAT / ELF / PE / COFF / VHD
/// 这些是“能嗅探出来的容器与文件系统”，把它们注册成压缩包关联只会让右键“打开方式”变脏。
/// 同理排除 Compound（扩展名是 doc / xls / ppt）这类办公文档。它们仍然能打开 ——
/// 打开对话框与命令行走的是内容嗅探，与关联表无关。
/// </para>
/// <para>
/// 但“哪些算支持”不由本表说了算：<see cref="Supported"/> 拿引擎实际报告的扩展名集合来过滤，
/// 所以引擎哪天读不了某个格式，那一行就自己消失，不会出现勾了却打不开的关联。
/// </para>
/// </summary>
public static class FileAssociationCatalog
{
    /// <summary>全部候选，与引擎无关（测试与预览用）。界面上应当用 <see cref="Supported"/>。</summary>
    public static IReadOnlyList<AssociationGroup> All { get; } =
    [
        new("TEXT_GROUP_ARCHIVE",
        [
            new("zip", "ZIP", ["zip"]),
            new("zipx", "ZIPX", ["zipx"]),
            new("7z", "7Z", ["7z"]),
            new("rar", "RAR", ["rar", "r00"]),
            new("cab", "CAB", ["cab"]),
            new("arj", "ARJ", ["arj"]),
            new("lzh", "LHA", ["lzh", "lha"]),
            new("001", "001", ["001"]),
            new("zst", "ZSTD", ["zst", "tzst"]),
            new("chm", "CHM", ["chm", "chi", "chq", "chw"]),
            new("nsis", "NSIS", ["nsis"]),
            new("exe", "EXE", ["exe"]),
        ]),

        new("TEXT_GROUP_UNIX",
        [
            new("tar", "TAR", ["tar"]),
            new("gz", "GZ", ["gz", "gzip"]),
            new("tgz", "TGZ", ["tgz", "tpz"]),
            new("bz", "BZ", ["bz2", "bzip2"]),
            new("tbz", "TBZ", ["tbz"]),
            new("tbz2", "TBZ2", ["tbz2"]),
            new("xz", "XZ", ["xz"]),
            new("txz", "TXZ", ["txz"]),
            new("lzma", "LZMA", ["lzma"]),
            new("z", "Z", ["z", "taz"]),
            new("deb", "DEB", ["deb", "udeb"]),
            new("rpm", "RPM", ["rpm"]),
        ]),

        new("TEXT_GROUP_OTHER",
        [
            new("jar", "JAR", ["jar"]),
            new("apk", "APK", ["apk"]),
            new("ipa", "IPA", ["ipa"]),
            new("xpi", "XPI", ["xpi"]),
            new("appx", "APPX", ["appx"]),
            new("wim", "WIM", ["wim", "swm", "esd"]),
            new("cpio", "CPIO", ["cpio"]),
            new("ar", "AR", ["ar", "lib"]),
        ]),

        new("TEXT_GROUP_CD_DVD",
        [
            new("iso", "ISO", ["iso"]),
            new("img", "IMG", ["img"]),
            new("udf", "UDF", ["udf"]),
        ]),
    ];

    /// <summary>
    /// 按引擎实际可读的扩展名过滤，分组内没有候选项的分组整个去掉。
    /// <paramref name="readableExtensions"/> 直接喂引擎的返回值即可（带不带点、大小写都不敏感）。
    /// </summary>
    public static IReadOnlyList<AssociationGroup> Supported(IReadOnlyCollection<string> readableExtensions)
    {
        var readable = new HashSet<string>(
            readableExtensions.Select(NormaliseExtension),
            StringComparer.OrdinalIgnoreCase);

        return [.. All
            .Select(group => new AssociationGroup(
                group.TextKey,
                [.. group.Items.Where(item => item.IsSupportedBy(readable))]))
            .Where(group => group.Items.Count > 0)];
    }

    /// <summary>按 Id 查候选项（找不到返回 null）。</summary>
    public static FileAssociation? Find(string id)
        => All.SelectMany(g => g.Items).FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// 右键「压缩为 …」应当对哪些扩展名隐身 —— 也就是本表认为"这是个压缩包"的那些后缀。
    /// <para>
    /// 对压缩包提供压缩是个没有意义的动作（把 zip 再压成 zip 几乎不省空间，还多套一层），
    /// 用户的话是"右键压缩包还能压缩这种蠢事"。所以这些后缀上只留 打开 / 解压 / 解压并删除。
    /// </para>
    /// <para>
    /// <strong><c>exe</c> 是刻意排除的</strong>：它对 Andzip 是 SFX 自解压包，对用户就是一个
    /// "我想把这个安装包压小一点"的普通文件。把压缩项从 exe 身上摘掉是帮倒忙。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> CompressionSuppressedExtensions { get; } =
        [.. All.SelectMany(g => g.Items)
            .Where(item => item.Id != "exe")
            .SelectMany(item => item.Extensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.Ordinal)];

    /// <summary>
    /// 注册表动词的 <c>AppliesTo</c> 查询串：让资源管理器在压缩包上隐藏「压缩为 …」。
    /// <para>
    /// 结构是 <c>NOT (System.FileExtension:=.zip OR System.FileExtension:=.7z …)</c>。
    /// 这不是我猜的写法：在本机往 <c>HKCU\Software\Classes\*\shell\Andzip.compress-zip</c>
    /// 手写这个值之后，<c>Shell.Application</c> 枚举 <c>box.zip</c> 的动词表里「压缩为 ZIP」消失，
    /// 而 <c>note.txt</c> 上还在 —— 多扩展名的 OR 形式同样生效。
    /// </para>
    /// </summary>
    public static string CompressionSuppressionQuery()
        => "NOT (" + string.Join(
            " OR ",
            CompressionSuppressedExtensions.Select(e => "System.FileExtension:=." + e)) + ")";

    private static string NormaliseExtension(string extension)
        => extension.TrimStart('.').ToLowerInvariant();
}
