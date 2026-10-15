using NativeArchiveWriter = Andzip.Native.ArchiveWriter;
using NativeItemMeta = Andzip.Native.ItemMeta;

namespace Andzip.Core.Archive;

/// <summary>
/// 往一个已经存在的包里增删成员（「编辑」菜单那几项的地基）。
/// <para>
/// 引擎侧只有一个动作：<strong>把要留的成员列出来，其余什么都不说</strong>。
/// 所以删除是"不列出"，添加是"另外加几个新成员"，重命名是"保留其余 + 用新名字加回这一个"。
/// 没有第四种操作，也就不存在三种操作各自不一致的可能。
/// </para>
/// <para>
/// 列出的成员是<strong>照抄存储字节</strong>，不是重压一遍 —— 一个 100 GB 的成员留着，
/// 代价是搬 100 GB，不是压 100 GB。这也是为什么这里不需要用户再选一次压缩级别：
/// 新加的成员用格式默认值，老成员保持它们本来的样子。
/// </para>
/// </summary>
public static class ArchiveUpdater
{
    /// <summary>
    /// 就地改写一个包。<paramref name="settings"/>.<see cref="ArchiveUpdateSettings.Password"/>
    /// 是<strong>读</strong>这个包要用的口令（加密文件头的 7z 没有它就列不出成员），
    /// 不是写新包要用的口令。
    /// <exception cref="ArchiveConstraintException">这个包改不动（格式只读、加密文件头且没给口令、
    /// 头已损坏）。</exception>
    /// </summary>
    public static void Update(ArchiveUpdateSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        NativeEngine.Run("update archive", () =>
        {
            var options = new Native.CreateOptions
            {
                // Format 故意不设：改写时格式来自那个文件本身，不是这里说的。
                Password = string.IsNullOrEmpty(settings.Password) ? null : settings.Password,
                MultiThread = true,
                Progress = NativeEngine.Bridge(settings.Progress, ArchiveOperation.Compress),
            };

            using var writer = NativeArchiveWriter.OpenExisting(settings.ArchivePath, options);
            foreach (var index in settings.KeepIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.KeepMember(index);
            }

            foreach (var added in settings.Added)
            {
                cancellationToken.ThrowIfCancellationRequested();
                added.WriteTo(writer);
            }

            writer.Finish();
            return true;
        });
    }

    /// <summary>
    /// 这个包改得动吗 —— 判据是<strong>文件本身的格式</strong>（见 <see cref="CanUpdate"/>）。
    /// <para>界面拿它决定「编辑」那几项灰不灰，下面三个门面拿它当门口的闸 ——
    /// 一处定义、两处用；重复一份写死在界面里，早晚会与这里说的不是同一件事。</para>
    /// </summary>
    public static bool CanUpdate(string? formatName)
        => formatName is not null && UpdatableFormats.Contains(formatName, StringComparer.OrdinalIgnoreCase);

    /// <summary>引擎的 out-handler 支持就地更新的那几种。判据是"改得动"而不是"我们愿意改"：
    /// GZ / BZIP2 / XZ / ZSTD 能创建却只有一条流，RAR / ISO / CAB 连创建都没有。</summary>
    private static readonly string[] UpdatableFormats = ["zip", "7z", "tar"];

    /// <summary>
    /// 往包里加成员。要加的东西已经在磁盘上（或自带字节），其余的成员原样留下。
    /// <para><paramref name="replaceExisting"/> 打开时，包里与要加的路径<strong>同名（或同名目录下）</strong>
    /// 的成员会被替换掉 —— 引擎那边没有"覆盖"这个动作，它是"保留名单里不列它们"。
    /// 默认关着：静默把包里原有的东西顶掉，是用户完全预料不到的后果，该由界面问过之后才打开。</para>
    /// <paramref name="added"/> 里的一项若是<strong>目录</strong>，它底下的整棵树会一起进来。
    /// <exception cref="ArchiveConstraintException">这个格式改不动。</exception>
    /// </summary>
    public static void Add(
        string archivePath,
        IReadOnlyList<ArchiveAddedMember> added,
        bool replaceExisting = false,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(added);
        var listing = ReadForUpdate(archivePath, password, cancellationToken);

        var keep = replaceExisting
            ? listing.Entries
                .Where(entry => !added.Any(member => Covers(member.EntryPath, entry.Path)))
                .Select(entry => (uint)entry.Index)
                .ToArray()
            : AllIndices(listing);

        Update(new ArchiveUpdateSettings(archivePath, keep, added, password, progress),
            cancellationToken);
    }

    /// <summary>要加的这个成员"占住"了包内的哪条路径 —— 它自己，以及（若是目录）它底下的每一个。</summary>
    private static bool Covers(string entryPath, string memberPath)
    {
        var self = Normalize(entryPath);
        var other = Normalize(memberPath);
        return other.Equals(self, StringComparison.Ordinal)
            || other.StartsWith(self + "/", StringComparison.Ordinal);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>
    /// 从包里删掉几个成员。传的是<strong>要删的下标</strong>，不是要留的 ——
    /// "没列出来的都会被删掉"这种语义交给这一层算，界面永远只说它想删谁。
    /// <exception cref="ArchiveConstraintException">这个格式改不动。</exception>
    public static void Remove(
        string archivePath,
        IReadOnlyList<int> removeIndices,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removeIndices);
        var listing = ReadForUpdate(archivePath, password, cancellationToken);
        var doomed = new HashSet<int>(removeIndices);

        foreach (var index in doomed)
        {
            if (listing.Entries.All(e => e.Index != index))
            {
                throw new ArgumentOutOfRangeException(nameof(removeIndices), index, "这个包里没有这个下标的成员。");
            }
        }

        var keep = listing.Entries.Where(e => !doomed.Contains(e.Index)).Select(e => (uint)e.Index).ToArray();
        Update(new ArchiveUpdateSettings(archivePath, keep, [], password, progress), cancellationToken);
    }

    /// <summary>
    /// 给一个成员改名（<strong>只能改文件</strong>，见 <see cref="ArchiveConstraintException"/> 那段）。
    /// <para>做法是"把它读出来、按新名字加回去、其余原样留"：存储字节因此会被重新压一遍，
    /// 时间与属性从清单里带过去（否则新加的那一份会继承临时文件的时间）。
    /// 代价写在开发记录里：改一个 1 GB 的成员就要解压 + 重压它一次，这不是零成本操作。</para>
    /// </summary>
    /// <param name="newEntryPath">新名字（包内路径）。可以带目录前缀，但只是换名字用 —— 界面只让改叶子名。</param>
    /// <exception cref="ArchiveConstraintException">这个格式改不动、或这是个目录。</exception>
    public static void Rename(
        string archivePath,
        int memberIndex,
        string newEntryPath,
        string? password = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newEntryPath);
        var listing = ReadForUpdate(archivePath, password, cancellationToken);

        var entry = listing.Entries.FirstOrDefault(e => e.Index == memberIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(memberIndex), memberIndex, "这个包里没有这个下标的成员。");

        if (entry.IsDirectory)
        {
            throw new ArchiveConstraintException(ArchiveConstraintReason.DirectoryRenameNotSupported);
        }

        var staged = StageMemberToTemp(archivePath, password, memberIndex, cancellationToken);
        try
        {
            var keep = listing.Entries.Where(e => e.Index != memberIndex).Select(e => (uint)e.Index).ToArray();
            var added = new ArchiveAddedMember(
                newEntryPath,
                SourcePath: staged,
                Modified: entry.Modified,
                Attributes: entry.Attributes,
                PosixMode: entry.PosixMode);

            Update(new ArchiveUpdateSettings(archivePath, keep, [added], password, progress), cancellationToken);
        }
        finally
        {
            TryDelete(staged);
        }
    }

    /// <summary>读清单并顺手挡掉改不动的格式。清单也是"要留哪些"的唯一来源 —— 见 <see cref="AllIndices"/>。</summary>
    private static ArchiveListing ReadForUpdate(string archivePath, string? password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var listing = ArchiveReader.Read(archivePath, password, cancellationToken);

        if (!CanUpdate(listing.FormatName))
        {
            throw new ArchiveConstraintException(ArchiveConstraintReason.ArchiveNotEditable);
        }

        return listing;
    }

    /// <summary>清单里的全部下标。目录也在里面 —— 少列一个就等于把它悄悄删掉。</summary>
    private static uint[] AllIndices(ArchiveListing listing)
        => [.. listing.Entries.Select(e => (uint)e.Index)];

    /// <summary>
    /// 把一个成员的内容落到临时文件。走的是与缩略图同一条"按序号回读"的路
    /// （<see cref="ArchiveReader.OpenNative(string, string?)"/> 认嵌套、认口令），不经过内存 ——
    /// 一个 4 GB 的成员不该为了改个名字先进 LOH。
    /// </summary>
    private static string StageMemberToTemp(
        string archivePath,
        string? password,
        int memberIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var single = SingleFileArchive.Open(archivePath, cancellationToken);
        using var reader = ArchiveReader.OpenNative(single.FilePath, password);

        var staged = Path.Combine(Path.GetTempPath(), $"andzip-rename-{Guid.NewGuid():N}.tmp");
        using (var file = File.Create(staged))
        {
            reader.ReadEntry((ulong)memberIndex, file);
        }

        return staged;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件删不掉不该让一次成功的改名变成失败；系统自己会收拾 %TEMP%。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>一次改写的完整描述：留哪些成员、加哪些成员。</summary>
/// <param name="ArchivePath">要就地改写的包。</param>
/// <param name="KeepIndices">要留下的成员下标，取自<strong>改写前</strong>的清单顺序。
/// 不在这个列表里的成员会被删掉 —— 包括用户没想到的：一个都不列就是清空整个包。</param>
/// <param name="Added">新加进来的成员。</param>
/// <param name="Password">读这个包要用的口令。</param>
public sealed record ArchiveUpdateSettings(
    string ArchivePath,
    IReadOnlyList<uint> KeepIndices,
    IReadOnlyList<ArchiveAddedMember> Added,
    string? Password = null,
    IProgress<ArchiveProgress>? Progress = null);

/// <summary>
/// 一个要加进去的成员。<paramref name="SourcePath"/> 有值就从磁盘上的文件加
/// （<strong>若是目录，整棵树一起进来</strong>），否则写 <paramref name="Data"/> 里自带的字节。
/// <para><paramref name="Modified"/> / <paramref name="Attributes"/> / <paramref name="PosixMode"/>
/// 是<strong>从清单里带过来的</strong>原件属性：不带的话引擎会按磁盘上那个源文件现取，
/// 而改名走的临时文件"修改时间"是刚刚 —— 屏幕上就是"改个名字，日期变成今天"。</para>
/// </summary>
public sealed record ArchiveAddedMember(
    string EntryPath,
    string? SourcePath = null,
    ReadOnlyMemory<byte> Data = default,
    DateTime? Modified = null,
    uint? Attributes = null,
    uint PosixMode = 0)
{
    internal void WriteTo(NativeArchiveWriter writer)
    {
        var meta = ToMeta();
        if (SourcePath is not null)
            // recurse: 加一个目录就要把底下的东西一起带进来（从剪贴板粘一个文件夹走的就是这条路）。
            // 传进来的是文件时，这一项没有区别。
            writer.AddFile(SourcePath, EntryPath, meta, recurse: true);
        else
            writer.AddData(Data, EntryPath, meta);
    }

    private NativeItemMeta? ToMeta()
    {
        if (Modified is null && Attributes is null && PosixMode == 0)
        {
            return null;   // 什么都没带：让引擎按源文件推导（新建压缩时那一堆走的就是这条路）
        }

        return new NativeItemMeta
        {
            MTimeUnix = Modified is { } modified ? new DateTimeOffset(modified).ToUnixTimeSeconds() : 0,
            Attributes = Attributes ?? 0,
            PosixMode = PosixMode,
        };
    }
}
