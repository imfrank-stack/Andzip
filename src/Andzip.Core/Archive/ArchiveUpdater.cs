using NativeArchiveWriter = Andzip.Native.ArchiveWriter;

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
    /// 这个包改得动吗 —— 判据不在这里，在界面上：格式与"文件头有没有加密"从清单里就看得见
    /// （见 <see cref="EncryptedHeaderProbe"/>），不必为了灰掉一颗菜单项再去开一次文件。
    /// 真按下去了改不动，<see cref="Update"/> 会如实抛出来。
    /// </summary>
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
/// 一个要加进去的成员。<paramref name="SourcePath"/> 有值就从磁盘上的文件加，
/// 否则写 <paramref name="Data"/> 里自带的字节（重命名走这条：把原成员解到临时文件
/// 再按新名字加回来，比在内存里放一个 100 GB 成员安全）。
/// </summary>
public sealed record ArchiveAddedMember(string EntryPath, string? SourcePath = null, ReadOnlyMemory<byte> Data = default)
{
    internal void WriteTo(NativeArchiveWriter writer)
    {
        if (SourcePath is not null)
            writer.AddFile(SourcePath, EntryPath, meta: null, recurse: false);
        else
            writer.AddData(Data, EntryPath);
    }
}
