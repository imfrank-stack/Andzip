using Andzip.Native;

namespace Andzip.Core.Archive;

/// <summary>
/// CRC 子菜单上那一行：<paramref name="Label"/> 是屏幕上写的，<paramref name="EngineName"/>
/// 是交给内核 <c>az_hasher_open</c> 的拼法。两者不一样：菜单跟 7-Zip 的界面一样写
/// "CRC-32"，内核与 7-Zip 的注册表一样收 "CRC32"。
/// <para><strong>标签不翻译</strong> —— 算法名在哪种语言里都是这几个字母，
/// 7-Zip 在 <c>FileManager/MyLoadMenu.cpp:219</c> 为同一件事留了一句话
/// （"we don't need lang change for CRC items"）。所以这一排不走语言包，
/// 也就不出现"第二份真相"：菜单、工具栏下拉、结果框表头、内核名字，都从这张表来。</para>
/// <para><c>EngineName</c> 为 null 的那一行是 <c>*</c>，意思是"每一样都算"
/// （与 7-Zip 的 IDM_HASH_ALL 同义）。</para>
/// </summary>
public sealed record ChecksumAlgorithm(string Label, string? EngineName)
{
    /// <summary>顺序照 7-Zip 文件管理器的 CRC 子菜单：短的在前，然后按系列。</summary>
    public static IReadOnlyList<ChecksumAlgorithm> Ten { get; } =
    [
        new("CRC-32", "CRC32"),
        new("CRC-64", "CRC64"),
        new("XXH64", "XXH64"),
        new("MD5", "MD5"),
        new("SHA-1", "SHA1"),
        new("SHA-256", "SHA256"),
        new("SHA-384", "SHA384"),
        new("SHA-512", "SHA512"),
        new("SHA3-256", "SHA3-256"),
        new("BLAKE2sp", "BLAKE2sp"),
    ];

    public static readonly ChecksumAlgorithm All = new("*", null);

    /// <summary>菜单上那十一行：十种算法加一个 <c>*</c>。</summary>
    public static IReadOnlyList<ChecksumAlgorithm> Menu { get; } = [.. Ten, All];

    /// <summary><c>*</c> 展开成全部十种；其余就是它自己。</summary>
    public IReadOnlyList<ChecksumAlgorithm> Expand() => IsAll ? Ten : [this];

    public bool IsAll => EngineName is null;
}

/// <summary>要算摘要的那一样东西：一个名字，加上"把它的字节倒进这条流"的办法。</summary>
/// <param name="Name">显示用的名字（包里的成员路径，或磁盘文件名）。</param>
/// <param name="Size">它解压之后有多大；不知道就传 0，进度只按项数走。</param>
/// <param name="WriteTo">把字节写进给定的流。实现方负责分块，调用方的那条流只是个接收器。</param>
public sealed record HashItem(string Name, long Size, Action<Stream> WriteTo);

/// <summary>一项的结果。摘要已按 7-Zip 的显示规矩格式化好，顺序与请求的算法一致。</summary>
public sealed record HashRow(string Name, long Size, IReadOnlyList<string> Digests, string? Error = null)
{
    public bool Failed => Error is not null;
}

/// <summary>一次进度回调。字节用来走那根真的会动的条；总量未知时 TotalBytes 为 0。</summary>
public sealed record HashProgress(int ItemsDone, int ItemCount, string Current, long DoneBytes, long TotalBytes);

/// <summary>
/// 算校验和 —— 「文件 → CRC」与工具栏那颗 CRC 背后就是这一层。
/// <para>为什么放在内核而不是托管：<see cref="Hasher"/> 就是内核 <c>az_hasher_*</c> 的壳，
/// 而那张算法表里 SHA-384 与 BLAKE2sp 两行是内核门面自己补出来的（vendored 的注册表里
/// 一个被注释掉、一个藏在 Rar5 处理器里）。用 <see cref="System.Security.Cryptography"/>
/// 只能覆盖其中一部分，且算出来的字节序与显示规矩还得自己再对一遍。</para>
/// <para><strong>同步阻塞</strong>：调用方自己放到后台线程上。整个过程只留调用方那一个
/// 64 KiB 的写缓冲，字节喂完就丢，所以一张 4 GB 的图和一段 4 GB 的视频花一样的内存 ——
/// 这也是这里不用 <c>ArchiveContentSession.ReadEntry(index, maxBytes)</c> 的原因：
/// 那个重载先把整格读进内存。</para>
/// </summary>
public static class ChecksumCalculator
{
    /// <param name="onProgress">每一项开始时一次，之后每写满一块再来一次。
    /// 从工作线程回调，编组是调用方的事（与 <c>ArchiveProgress</c> 同一条规矩）。</param>
    public static IReadOnlyList<HashRow> Compute(
        IReadOnlyList<ChecksumAlgorithm> requested,
        IReadOnlyList<HashItem> items,
        Action<HashProgress>? onProgress = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(items);

        var algorithms = requested.SelectMany(algorithm => algorithm.Expand())
            .DistinctBy(algorithm => algorithm.EngineName)
            .ToList();

        if (algorithms.Count == 0)
        {
            throw new ArgumentException("至少要一种算法", nameof(requested));
        }

        // Hasher 不可重用（内核的 IHasher 没有 Reset），所以每一项都要新开一组；
        // 但"这个名字内核认不认"与项无关，先各开一次问掉 —— 不支持的算法要在动手前
        // 就说一次，而不是让每一项各报一遍同样的错。
        foreach (var algorithm in algorithms)
        {
            Hasher.Open(algorithm.EngineName!).Dispose();
        }

        var totalBytes = items.Sum(item => Math.Max(0, item.Size));
        var finishedBytes = 0L;
        var rows = new List<HashRow>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();

            var item = items[i];
            var size = Math.Max(0, item.Size);
            var digests = new string[algorithms.Count];

            // 夹一道 Min：清单上记的大小与实际解出来的不一致（包坏了、或者那一格是 0）
            // 时，条子最多走到这一项的末尾，不会越过 100% 再回头。
            void Report(long withinItem) => onProgress?.Invoke(new HashProgress(
                i, items.Count, item.Name, finishedBytes + Math.Min(withinItem, size), totalBytes));

            Report(0);

            try
            {
                using var sink = new AggregateHashSink(algorithms, Report);
                item.WriteTo(sink);
                sink.Finish(digests);
                rows.Add(new HashRow(item.Name, item.Size, digests));
            }
            catch (OperationCanceledException)
            {
                throw;  // 取消不是"这一项算不出来"，别吞进错误列
            }
            catch (Exception error)
            {
                // 一项读不到不影响其余项 —— 7-Zip 也是把这一项记成错误继续往下走
                // （UI/FileManager/PanelCrc.cpp 里 NumErrors++ 然后接着下一个文件）。
                rows.Add(new HashRow(item.Name, item.Size,
                    Enumerable.Repeat(string.Empty, digests.Length).ToArray(), error.Message));
            }

            finishedBytes += size;
        }

        onProgress?.Invoke(new HashProgress(items.Count, items.Count, string.Empty, totalBytes, totalBytes));
        return rows;
    }

    /// <summary>
    /// 一条只写的流：写进来的字节分发给每一枚 hasher，一个字节也不留。
    /// <para>内核那侧 <c>az_entry_read_stream</c> 与 <c>FileStream.CopyTo</c> 都只会
    /// <c>Write(buffer, offset, count)</c>，所以这一条不需要读、不需要定位；
    /// 它们被调用到就说明有人在指望更多，宁可当场抛，也别给一个"看起来对"的摘要。</para>
    /// </summary>
    private sealed class AggregateHashSink : Stream
    {
        private readonly List<(ChecksumAlgorithm Algorithm, Hasher Hasher)> _hashers;
        private readonly Action<long> _onBytes;
        private long _written;

        public AggregateHashSink(IReadOnlyList<ChecksumAlgorithm> algorithms, Action<long> onBytes)
        {
            _onBytes = onBytes;
            _hashers = [.. algorithms.Select(algorithm => (algorithm, Hasher.Open(algorithm.EngineName!)))];
        }

        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // 数组段直接包成 ReadOnlyMemory —— Hasher 要把它交给内核，需要能钉住，
            // 而调用方给的就是数组，这里不拷。
            var slice = new ReadOnlyMemory<byte>(buffer, offset, count);
            foreach (var (_, hasher) in _hashers)
            {
                hasher.Update(slice);
            }

            _written += count;
            _onBytes(_written);
        }

        public void Finish(string[] into)
        {
            for (var i = 0; i < _hashers.Count; i++)
            {
                into[i] = Hasher.FormatDigest(_hashers[i].Hasher.Finish());
            }
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var (_, hasher) in _hashers)
                {
                    hasher.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
