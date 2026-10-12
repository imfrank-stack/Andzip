using System.Buffers.Binary;
using System.Globalization;
using Andzip.Native;
using NativeArchiveWriter = Andzip.Native.ArchiveWriter;

namespace Andzip.Core.Archive;

/// <summary>三种修复策略。它们是三件不同的事，不是一条路的不同深浅 —— 所以让用户自己选。</summary>
public enum ZipRepairMode
{
    /// <summary>
    /// 只重建中心目录（中央目录 + EOCD）。成员载荷一个字节都不改，按局部文件头原样搬走，
    /// 只改写头里的长度字段。
    /// <para>
    /// 不适用：局部头里不写长度的流式包（真值只在数据描述符里）。结构修复不解压，
    /// 所以无从得知它有多长 —— 这些成员会被跳过并说明，那种包该用 <see cref="SalvageAndRepack"/>。
    /// </para>
    /// </summary>
    RebuildCentralDirectory,

    /// <summary>
    /// 分卷 / 被截断包的续接：把 <c>名字.z01 … .zNN + 名字.zip</c> 当作一条拼接流读，
    /// 并允许最后一个成员只剩半截 —— 按实际剩下的长度写进新的中心目录，让前面那些成员仍然打得开。
    /// 输出<strong>不再分卷</strong>：分卷的偏移记账在残缺输入上本就不可信，赌它比不赌更糟。
    /// </summary>
    JoinTruncatedParts,

    /// <summary>
    /// 能修多少修多少：先做结构修复，然后逐个成员<strong>真解压</strong>；解得出来的重新打包，
    /// 解不出来的（CRC 错、数据被覆盖）跳过并列名。代价是重新压一遍 —— 耗时，而且新包字节与原包
    /// 不再相同；好处是中心目录与 CRC 都是重算出来的真值。
    /// </summary>
    SalvageAndRepack,
}

/// <summary>一次修复的产出。跳过什么必须和收回什么一样清楚。</summary>
public sealed record ZipRepairResult(
    string OutputPath,
    IReadOnlyList<string> RecoveredEntries,
    IReadOnlyList<string> SkippedEntries,
    IReadOnlyList<string> Notes)
{
    public bool ProducedAnything => RecoveredEntries.Count > 0;
}

/// <summary>
/// ZIP 修复（菜单「工具 → 修复压缩包」）。三种模式共用一层字节级搬运，全部建立在
/// <see cref="ZipScanner"/> 的游走上 —— 也就是说，建立在"引擎读不出来的时候我们还能知道什么"上。
/// <para>
/// 三条铁律：<strong>绝不覆盖原包</strong>（输出一定是另一个文件）；<strong>不确定就跳过并说明</strong>
/// （宁可少一个成员，也不写一个看起来成功的错偏移）；<strong>每条跳过都带原因</strong>。
/// </para>
/// </summary>
public static class ZipRepairer
{
    private const uint CentralSignature = 0x02014b50;
    private const uint Eocd64Signature = 0x06064b50;
    private const uint Eocd64LocatorSignature = 0x07064b50;
    private const uint EocdSignature = 0x06054b50;

    private const int CentralFixedSize = 46;
    private const int EocdSize = 22;
    private const int Eocd64Size = 56;
    private const int Eocd64LocatorSize = 20;

    /// <summary>模式三逐成员解压时，超过这个大小的先落临时文件再入包，
    /// 免得把一个 2 GB 的成员整个吸进内存。</summary>
    private const long InMemoryLimit = 256L << 20;

    /// <summary>建议的输出路径：同目录、加「.修复」、重名自动加序号。返回值永远不等于输入。</summary>
    public static string SuggestOutputPath(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var full = Path.GetFullPath(archivePath);
        var directory = Path.GetDirectoryName(full) ?? throw new InvalidOperationException("路径没有父目录：" + full);
        var stem = Path.GetFileNameWithoutExtension(full);

        // 复用右键压缩那套唯一化：那里已经处理过"文件和目录都算占用这个名字"。
        return ShellTasks.UniqueArchivePath(Path.Combine(directory, stem + ".修复.zip"));
    }

    /// <summary>分卷拼接顺序：<c>base.z01 … base.zNN</c>，最后是 <c>base.zip</c>（中心目录就在最后）。
    /// 没有分卷时返回只含自身的一元表。</summary>
    public static IReadOnlyList<string> FindVolumeParts(string archivePath)
    {
        var full = Path.GetFullPath(archivePath);
        var directory = Path.GetDirectoryName(full) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(full);

        var parts = new List<string>();
        for (var n = 1; n <= 300; n++)
        {
            var part = Path.Combine(directory, $"{stem}.z{n:D2}");
            if (!File.Exists(part))
            {
                break;
            }

            parts.Add(part);
        }

        if (parts.Count == 0)
        {
            return [full];
        }

        // 有分卷却缺主包：中心目录就在主包里，拼了前面也读不到目录。这时候报错比硬拼诚实。
        if (!string.Equals(Path.GetExtension(full), ".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            throw new FileNotFoundException("找到分卷却没找到主包：分卷的中心目录在最后那个 .zip 里。", full);
        }

        parts.Add(full);
        return parts;
    }

    public static ZipRepairResult Repair(
        string archivePath,
        ZipRepairMode mode,
        string? outputPath = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var input = Path.GetFullPath(archivePath);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Archive to repair was not found.", input);
        }

        var output = string.IsNullOrWhiteSpace(outputPath) ? SuggestOutputPath(input) : Path.GetFullPath(outputPath);
        if (string.Equals(output, input, StringComparison.OrdinalIgnoreCase))
        {
            // 修复到一半失败再覆盖原包，用户就同时失去了原包和一个残包。这一条不让走。
            throw new ArgumentException("输出路径不能就是原包：修复中途失败会连原始数据一起丢掉。", nameof(outputPath));
        }

        return mode switch
        {
            ZipRepairMode.RebuildCentralDirectory => Rebuild(input, [input], output, acceptTruncatedTail: false, progress, cancellationToken),
            ZipRepairMode.JoinTruncatedParts => Rebuild(input, FindVolumeParts(input), output, acceptTruncatedTail: true, progress, cancellationToken),
            ZipRepairMode.SalvageAndRepack => Salvage(input, output, progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的修复模式。"),
        };
    }

    // ------------------------------------------------ 模式一 / 模式二：结构重建

    private static ZipRepairResult Rebuild(
        string input,
        IReadOnlyList<string> parts,
        string output,
        bool acceptTruncatedTail,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var recovered = new List<string>();
        var skipped = new List<string>();

        // 分卷先拼成一条临时流：这样游走、偏移和写出只有一条路径可测。
        var staged = parts.Count > 1 ? StageParts(parts, notes) : null;
        var sourcePath = staged ?? input;

        try
        {
            using var source = File.OpenRead(sourcePath);
            var map = ZipScanner.Inspect(source);

            if (map.LocalHeaders.Count == 0)
            {
                notes.Add("一个局部文件头都没找到：这份数据里认不出任何成员，不生成新包。");
                return new ZipRepairResult(output, recovered, skipped, notes);
            }

            if (map.PrefixLength > 0)
            {
                notes.Add($"原包是自解压格式：前 {map.PrefixLength} 字节是引导程序，不会带进新包 —— 新包是普通 zip，双击不再自动运行。");
            }

            // 搬运的总边界：中心目录起点（读得出时）→ EOCD 起点 → 文件尾。
            var boundary = map.CentralDirectoryOffset > 0 && map.CentralDirectoryOffset < map.FileLength
                ? map.CentralDirectoryOffset
                : map.EocdOffset > 0 ? map.EocdOffset : map.FileLength;

            var plan = PlanMembers(source, map, boundary, acceptTruncatedTail, notes, skipped);

            if (plan.Count == 0)
            {
                notes.Add("所有成员都被跳过了，不生成新包。");
                return new ZipRepairResult(output, recovered, skipped, notes);
            }

            using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);

            // 先规划再写：中心目录要一次写对，就得知道每条落在哪儿。边写边猜会写出
            // 一个对不上的中心目录 —— 那正是我们要修的那种病。
            var offsets = new long[plan.Count];
            for (var i = 0; i < plan.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                offsets[i] = target.Position;
                CopyMemberHeaderAndData(source, target, plan[i]);

                recovered.Add(plan[i].Name);
                progress?.Report(new ArchiveProgress(
                    ArchiveOperation.Extract, target.Position, source.Length, plan[i].Name, i, plan.Count));
            }

            WriteCentralDirectory(target, plan, offsets);
            recovered.Sort(StringComparer.Ordinal);

            if (skipped.Count > 0)
            {
                notes.Add($"跳过了 {skipped.Count} 个成员，新包里有 {recovered.Count} 个。");
            }

            return new ZipRepairResult(output, recovered, skipped, notes);
        }
        finally
        {
            if (staged is not null)
            {
                TryDelete(staged);
            }
        }
    }

    /// <summary>一个待搬运成员：源区间 + 写中心目录要用的那些值。头里的原始字段在这里读一次存好，
    /// 后面的写出不再回源。</summary>
    private sealed class PlannedMember
    {
        public required string Name { get; init; }
        public required byte[] RawName { get; init; }
        public required long SourceHeaderOffset { get; init; }
        public required long SourceDataOffset { get; init; }
        public required long SourceDataEnd { get; init; }
        public required int Flags { get; init; }
        public required int Method { get; init; }
        public required uint Crc32 { get; init; }
        public required ushort DosTime { get; init; }
        public required ushort DosDate { get; init; }
        public required long UncompressedSize { get; init; }

        public long CompressedSize => SourceDataEnd - SourceDataOffset;
        public bool IsDirectory => Name.EndsWith('/') || (ExternalAttributes & 0x10) != 0;

        /// <summary>局部头没有外部属性字段，目录靠名字以 '/' 结尾判定；这里留个位置给
        /// 以后从中心目录补来的属性。</summary>
        public uint ExternalAttributes { get; init; }
    }

    private static List<PlannedMember> PlanMembers(
        Stream source,
        ZipMap map,
        long boundary,
        bool acceptTruncatedTail,
        List<string> notes,
        List<string> skipped)
    {
        var plan = new List<PlannedMember>();
        var locals = map.LocalHeaders.OrderBy(h => h.HeaderOffset).ToList();

        for (var i = 0; i < locals.Count; i++)
        {
            var header = locals[i];

            // 下一个成员的起点是"这段末尾最远能到哪儿"的唯一硬证据：声明长度越过它，
            // 要么声明是假的，要么中间被覆盖过。裁到边界是这两种情况下唯一安全的做法。
            var nextStart = i + 1 < locals.Count ? locals[i + 1].HeaderOffset : boundary;
            var isDirectory = header.Name.EndsWith('/');

            if (header.CompressedSize == 0 && !isDirectory && header.UncompressedSize != 0)
            {
                // 长度只写在数据描述符里 —— 不先解压就无从得知。猜一个边界会是假成功。
                skipped.Add(header.Name);
                notes.Add($"{header.Name}：局部头里没写长度（流式写入），结构修复不解压所以无法确定，请改用「能修多少修多少」。");
                continue;
            }

            var declaredEnd = header.DataOffset + header.CompressedSize;
            var end = Math.Min(declaredEnd, Math.Min(nextStart, map.FileLength));

            if (declaredEnd > map.FileLength)
            {
                if (!acceptTruncatedTail)
                {
                    skipped.Add(header.Name);
                    notes.Add($"{header.Name}：声明 {header.CompressedSize} 字节，实际只剩 {Math.Max(0, map.FileLength - header.DataOffset)} 字节。模式一不搬残缺成员；要收残缺的用「分卷/截断续接」。");
                    continue;
                }

                notes.Add($"{header.Name}：数据被截断，按实际剩下的 {end - header.DataOffset} 字节写入。解压这一个成员会报长度或 CRC 错，其它成员不受影响。");
            }
            else if (declaredEnd > nextStart)
            {
                notes.Add($"{header.Name}：声明长度越过了下一个成员的起点，已裁剪到边界。");
            }

            if (end < header.DataOffset)
            {
                skipped.Add(header.Name);
                notes.Add($"{header.Name}：算出来的数据区间是空的（起点 {header.DataOffset}，终点 {end}）。");
                continue;
            }

            if (header.CompressedSize > uint.MaxValue || end - header.DataOffset > uint.MaxValue)
            {
                skipped.Add(header.Name);
                notes.Add($"{header.Name}：超过 4 GB，需要重写 ZIP64 扩展，本模式不做。");
                continue;
            }

            // 时间戳、名字原字节都必须从源里读：中心目录要写它们，而"重新生成"的时间
            // 会让每个成员看起来都是刚刚修的 —— 那是伪造元数据。
            var headerLength = (int)(header.DataOffset - header.HeaderOffset);
            if (headerLength < 30 || headerLength > 0xFFFF + 0xFFFF)
            {
                skipped.Add(header.Name);
                notes.Add($"{header.Name}：局部头长 {headerLength} 字节，不合理。");
                continue;
            }

            var headerBytes = ReadAt(source, header.HeaderOffset, headerLength);
            plan.Add(new PlannedMember
            {
                Name = header.Name,
                RawName = headerBytes.AsSpan(30, header.NameLength).ToArray(),
                SourceHeaderOffset = header.HeaderOffset,
                SourceDataOffset = header.DataOffset,
                SourceDataEnd = end,
                Flags = header.Flags,
                Method = header.Method,
                Crc32 = header.Crc32,
                DosTime = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(10)),
                DosDate = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.AsSpan(12)),
                UncompressedSize = Math.Min(header.UncompressedSize, uint.MaxValue),
            });
        }

        return plan;
    }

    /// <summary>搬一个成员：头区原样复制，只把两个长度字段改成新算出来的值；载荷逐字节照搬。</summary>
    private static void CopyMemberHeaderAndData(Stream source, Stream target, PlannedMember member)
    {
        var headerLength = (int)(member.SourceDataOffset - member.SourceHeaderOffset);
        var headerBytes = ReadAt(source, member.SourceHeaderOffset, headerLength);

        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(18), (uint)member.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(22), (uint)member.UncompressedSize);

        target.Write(headerBytes);
        CopyRange(source, member.SourceDataOffset, member.CompressedSize, target);
    }

    private static void WriteCentralDirectory(Stream target, List<PlannedMember> plan, long[] offsets)
    {
        var cdStart = target.Position;

        // 46 字节的头缓冲在循环外分配一次：每个字段都会重写，复用是安全的，
        // 而放进循环就是每个成员往栈上压 46 字节（CA2014，大目录包会真把栈吃穿）。
        Span<byte> head = stackalloc byte[CentralFixedSize];

        for (var i = 0; i < plan.Count; i++)
        {
            var member = plan[i];

            BinaryPrimitives.WriteUInt32LittleEndian(head, CentralSignature);
            head[4] = 20;   // version made by：MS-DOS
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(6), 20);   // version needed
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(8), (ushort)member.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(10), (ushort)member.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(12), member.DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(14), member.DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(16), member.Crc32);
            BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(20), (uint)member.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(24), (uint)member.UncompressedSize);
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(28), (ushort)member.RawName.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(30), 0);    // extra
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(32), 0);    // comment
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(34), 0);    // disk start
            BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(36), 0);    // internal attrs
            BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(38), member.IsDirectory ? 0x10u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(42), (uint)offsets[i]);

            target.Write(head);

            // 名字按原字节写：老包里的中文名可能是 GBK，解码再编码回去不等于原字节，
            // 用字符串重写会把成员名写成一排问号 —— 那等于修坏了另一样东西。
            target.Write(member.RawName);
        }

        WriteEndRecords(target, plan.Count, target.Position - cdStart, cdStart);
    }

    private static void WriteEndRecords(Stream target, int count, long cdLength, long cdStart)
    {
        var needsZip64 = count > ushort.MaxValue || cdLength > uint.MaxValue || cdStart > uint.MaxValue;

        if (needsZip64)
        {
            var z64At = target.Position;
            Span<byte> z64 = stackalloc byte[Eocd64Size];
            BinaryPrimitives.WriteUInt32LittleEndian(z64, Eocd64Signature);
            BinaryPrimitives.WriteUInt64LittleEndian(z64.Slice(4), (ulong)(Eocd64Size - 12));
            z64[12] = 45;
            z64[14] = 45;
            BinaryPrimitives.WriteUInt64LittleEndian(z64.Slice(24), (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(z64.Slice(32), (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(z64.Slice(40), (ulong)cdLength);
            BinaryPrimitives.WriteUInt64LittleEndian(z64.Slice(48), (ulong)cdStart);
            target.Write(z64);

            Span<byte> locator = stackalloc byte[Eocd64LocatorSize];
            BinaryPrimitives.WriteUInt32LittleEndian(locator, Eocd64LocatorSignature);
            BinaryPrimitives.WriteUInt64LittleEndian(locator.Slice(8), (ulong)z64At);
            BinaryPrimitives.WriteUInt32LittleEndian(locator.Slice(16), 1);
            target.Write(locator);
        }

        Span<byte> eocd = stackalloc byte[EocdSize];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, EocdSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(8), needsZip64 ? ushort.MaxValue : (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(10), needsZip64 ? ushort.MaxValue : (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(12), needsZip64 ? uint.MaxValue : (uint)cdLength);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(16), needsZip64 ? uint.MaxValue : (uint)cdStart);
        target.Write(eocd);
    }

    // ------------------------------------------------ 模式三：逐成员抢救

    private static ZipRepairResult Salvage(
        string input,
        string output,
        IProgress<ArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var recovered = new List<string>();
        var skipped = new List<string>();

        // 先补结构，引擎才可能按成员逐个读 —— 引擎连目录都认不出时谈不上"逐成员"。
        var stagedRebuild = TempPath(".zip");
        var scratch = Path.Combine(Path.GetTempPath(), $"andzip-repair-{Guid.NewGuid():N}");

        try
        {
            var structural = Rebuild(input, FindVolumeParts(input), stagedRebuild,
                acceptTruncatedTail: true, progress: null, cancellationToken);

            notes.AddRange(structural.Notes);
            if (!structural.ProducedAnything)
            {
                notes.Add("结构修复没能恢复任何成员，模式三到此为止，不生成新包。");
                return new ZipRepairResult(output, recovered, skipped, notes);
            }

            using var reader = ArchiveReader.OpenNative(stagedRebuild, password: null);
            var entries = reader.Entries;

            using var writer = NativeArchiveWriter.Create(output, new CreateOptions
            {
                Format = "zip",
                Method = "Deflate",
                Level = 5,
            });

            Directory.CreateDirectory(scratch);

            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[i];

                progress?.Report(new ArchiveProgress(
                    ArchiveOperation.Extract, 0, 0, entry.Name, i, entries.Count));

                if (entry.IsDirectory)
                {
                    writer.AddDirectory(entry.Name);
                    recovered.Add(entry.Name);
                    continue;
                }

                // 解不出来就是坏在这里 —— 跳过并报名，绝不写一个"看起来在、其实坏"的成员。
                var tempFile = (string?)null;
                try
                {
                    if (entry.Size is { } size && size > InMemoryLimit)
                    {
                        tempFile = Path.Combine(scratch, i.ToString(CultureInfo.InvariantCulture));
                        using (var file = File.Create(tempFile))
                        {
                            reader.ReadEntry((ulong)i, file);
                        }

                        writer.AddFile(tempFile, entry.Name);
                    }
                    else
                    {
                        writer.AddData(reader.ReadEntry((ulong)i), entry.Name);
                    }
                }
                catch (Exception ex)
                {
                    // 外壳的 AndzipException 必须在 Core 里就地翻译，否则 native 错误码会漏到 UI 上；
                    // 翻译完之后仍然只"吞"数据类失败，取消一类要继续往外抛。
                    var reported = ex is AndzipException native
                        ? NativeEngine.Translate(entry.Name, native, badFormatMeansUnsupported: false)
                        : ex;

                    if (reported is not (InvalidDataException or IOException or ArchiveFailureException))
                    {
                        throw reported;
                    }

                    skipped.Add(entry.Name);
                    notes.Add($"{entry.Name}：解不出来（{reported.Message}），已跳过。");
                    continue;
                }
                finally
                {
                    if (tempFile is not null)
                    {
                        TryDelete(tempFile);
                    }
                }

                recovered.Add(entry.Name);
            }

            foreach (var warning in writer.Warnings)
            {
                notes.Add(warning);
            }

            writer.Finish();
        }
        finally
        {
            TryDelete(stagedRebuild);
            TryDeleteDirectory(scratch);
        }

        return new ZipRepairResult(output, recovered, skipped, notes);
    }

    // ------------------------------------------------------------------ 小工具

    private static string StageParts(IReadOnlyList<string> parts, List<string> notes)
    {
        var staged = TempPath(".bin");
        notes.Add($"分卷先拼成一条流（{parts.Count} 个文件）再修复；新包不分卷。");

        using var output = File.Create(staged);
        foreach (var part in parts)
        {
            using var input = File.OpenRead(part);
            input.CopyTo(output);
        }

        return staged;
    }

    private static string TempPath(string extension)
        => Path.Combine(Path.GetTempPath(), $"andzip-repair-{Guid.NewGuid():N}{extension}");

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer.AsSpan(0, count));
        return buffer;
    }

    private static void CopyRange(Stream source, long offset, long count, Stream target)
    {
        source.Position = offset;
        var buffer = new byte[1 << 17];
        while (count > 0)
        {
            var got = source.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, count)));
            if (got == 0)
            {
                break;   // 源到尾：剩下的长度按实际算，别写出一个假区间
            }

            target.Write(buffer.AsSpan(0, got));
            count -= got;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件删不掉不影响修复结果，宁可留着也别报一个假失败。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
