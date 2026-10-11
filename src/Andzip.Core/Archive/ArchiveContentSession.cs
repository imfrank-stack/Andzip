using NativeArchiveReader = Andzip.Native.ArchiveReader;

namespace Andzip.Core.Archive;

/// <summary>
/// 一个长驻的压缩包读取会话：按条目序号回读原始字节（压缩包视图的图像缩略图）。
/// <para>
/// 存在的理由是**不重复打开**。清单只需要读一次目录结构，但缩略图要按条目回读数据；
/// 如果每张图都走一遍 <see cref="ArchiveReader.Read"/>，400 张图就是 400 次解析中央目录。
/// </para>
/// <para>
/// 引擎的句柄不是线程安全的（一个句柄同一时刻只能被一个线程碰），所以这里用一把锁把
/// 访问串行化。缩略图是低优先级后台工作，串行不会让用户感觉到等待。
/// </para>
/// <para>序号取自 <see cref="ArchiveEntryInfo.Index"/>，与会话内 <c>OpenNative</c> 的枚举顺序一致
/// （两者都走嵌套展开的同一条路径，所以 .tar.gz 的序号也对得上）。</para>
/// </summary>
public sealed class ArchiveContentSession : IDisposable
{
    private readonly SingleFileArchive _single;
    private readonly NativeArchiveReader _reader;
    private readonly object _gate = new();
    private bool _disposed;

    private ArchiveContentSession(SingleFileArchive single, NativeArchiveReader reader)
    {
        _single = single;
        _reader = reader;
    }

    public static ArchiveContentSession Open(string archivePath, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var fullPath = Path.GetFullPath(archivePath);
        return NativeEngine.Run("open archive for content", () =>
        {
            var single = SingleFileArchive.Open(fullPath);
            try
            {
                return new ArchiveContentSession(single, ArchiveReader.OpenNative(single.FilePath, password));
            }
            catch
            {
                single.Dispose();
                throw;
            }
        });
    }

    /// <summary>解压单个条目到内存。条目大于 <paramref name="maxBytes"/> 时返回 null，
    /// 而不是先把 2 GB 塞进 LOH —— 调用方（缩略图）要的就是这个拒绝语义。</summary>
    public byte[]? ReadEntry(int index, long maxBytes)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ArchiveContentSession));
            }

            var entry = _reader.GetEntry((ulong)index);
            if (entry.Size is { } size && size > (ulong)Math.Max(0, maxBytes))
            {
                return null;
            }

            return _reader.ReadEntry((ulong)index);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _reader.Dispose();
        _single.Dispose();
    }
}
