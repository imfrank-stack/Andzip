using System.Buffers.Binary;
using System.Text;

namespace Andzip.Core.Archive;

/// <summary>中央目录（CD）里的一条：这是包"声称"自己有什么。</summary>
/// <param name="LocalHeaderOffset">它指向的局部头位置，<strong>相对压缩包本体</strong>；
/// 在自解压包里要加上 <see cref="ZipMap.PrefixLength"/> 才是文件里的位置。声称而已，
/// 是否真在那儿由 <see cref="ZipMap.LocalAt"/> 对照 —— 对不上就是坏包的头号症状。</param>
public sealed record ZipCentralEntry(
    string Name,
    int Method,
    uint Crc32,
    long CompressedSize,
    long UncompressedSize,
    long LocalHeaderOffset,
    int Flags,
    bool NeedsZip64);

/// <summary>磁盘上认出来的一个局部文件头（LFH）：这是包"实际"有什么。</summary>
/// <param name="DataOffset">载荷起点 = 头起点 + 30 + 名字长 + 扩展长。扩展长只有在该头之后
/// 确实跟着载荷时才可信，所以这里是"按头里写的长度"算出来的最佳估计。</param>
/// <param name="CompressedSize">头里声明的压缩后大小。带数据描述符（flags bit 3）时是 0，
/// 真值在中央目录或尾随的描述符里 —— 因此不能无条件信，也不能无条件当"完整"。</param>
public sealed record ZipLocalHeader(
    string Name,
    int Method,
    uint Crc32,
    long CompressedSize,
    long UncompressedSize,
    long HeaderOffset,
    long DataOffset,
    int Flags,
    bool NeedsZip64,
    /// <summary>名字与扩展区的原始长度。修复时要<strong>原字节</strong>搬名字：GBK 文件名解码再
    /// 编码回去不等于原来的字节，用字符串重写名字会把中文成员写成问号。</summary>
    int NameLength = 0,
    int ExtraLength = 0)
{
    /// <summary>载荷是否整段都在文件里。假的那些只能抢救前半截，或不收。</summary>
    public bool IsDataComplete(long fileLength)
        => CompressedSize > 0 && DataOffset + CompressedSize <= fileLength;

    /// <summary>是否用了数据描述符：声明大小为 0 但 CRC 也是 0，长度要靠别人告诉。</summary>
    public bool HasDataDescriptor => (Flags & 0x08) != 0;
}

/// <summary>
/// 一份 ZIP 骨架的读取结果。<strong>不</strong>是"这个包能不能用"的结论 —— 那由三种修复模式
/// 各自的目标决定；这里只回答"磁盘上到底有什么"。
/// </summary>
/// <param name="PrefixLength">压缩包本体在文件里的起点：自解压包（SFX）前面那段引导程序的字节数，
/// 普通包为 0。中央目录里的相对位置都要加上它才是文件位置。</param>
public sealed record ZipMap(
    long FileLength,
    long PrefixLength,
    long EocdOffset,
    bool HasZip64,
    IReadOnlyList<ZipCentralEntry> CentralDirectory,
    IReadOnlyList<ZipLocalHeader> LocalHeaders,
    IReadOnlyList<string> Notes,
    /// <summary>中央目录在文件里的起点；-1 表示不知道（读不到中央目录时也是这个值，
    /// 所以"是不是真空"要用 <see cref="CentralDirectoryLength"/> 一起看）。</summary>
    long CentralDirectoryOffset = -1,
    /// <summary>中央目录声称占多少字节。模式一重写中央目录时，从这里开始到 EOCD 整段都要换掉。</summary>
    long CentralDirectoryLength = 0)
{
    /// <summary>中央目录声称 N 条、实际读出 M 条，差值就是被截掉/被覆盖的那部分。</summary>
    public int MissingCentralEntries { get; init; }

    /// <summary>
    /// 结构上有没有损伤。<strong>只看骨架，不看数据</strong>：
    /// 判据是"这些数对不上"（找不到结束记录 / 声称的成员数比读出来的多 / 一条成员都认不出来），
    /// 不是"说明里有字" —— <see cref="Notes"/> 里有一类是情报不是病（自解压前导、EOCD 之后的追加字节），
    /// 拿它当损伤会把好包说成坏包。
    /// <para>反过来也不成立：结构完好而某个成员 CRC 错的包，这里仍是 <c>false</c> ——
    /// 那要真把数据解一遍才知道，是「测试」那个按钮的活，不是扫描的。</para>
    /// </summary>
    public bool Damaged => EocdOffset < 0
        || MissingCentralEntries > 0
        || (CentralDirectory.Count == 0 && LocalHeaders.Count == 0);

    /// <summary>按局部头的文件位置查（已经算上 SFX 前导）。</summary>
    public ZipLocalHeader? LocalAt(long headerOffset)
        => LocalHeaders.FirstOrDefault(l => l.HeaderOffset == headerOffset);

    /// <summary>中央目录声称的某条成员对应的局部头位置（文件坐标系）。</summary>
    public long AbsoluteLocalOffset(ZipCentralEntry entry) => PrefixLength + entry.LocalHeaderOffset;
}

/// <summary>
/// ZIP 骨架的直接读取：结束记录（EOCD）、ZIP64 记录、中央目录、局部文件头游走，全部自己解析字节，
/// 不借引擎。理由很直接 —— 修复功能要处理的正是引擎读不出来的包，请它帮忙看等于先认输。
/// <para>
/// 只读结构，<strong>不</strong>解载荷：模式一、模式二原样搬运压缩数据用不着解压；模式三要解压
/// 那是另一件事（走 <see cref="ArchiveExtractor"/>），混进来只会让这层既难测又难信。
/// </para>
/// </summary>
public static class ZipScanner
{
    private const uint LocalSignature = 0x04034b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint EocdSignature = 0x06054b50;
    private const uint Eocd64Signature = 0x06064b50;
    private const uint Eocd64LocatorSignature = 0x07064b50;
    private const ushort Zip64ExtraId = 0x0001;

    private const int EocdSize = 22;
    private const int CentralFixedSize = 46;
    private const int LocalFixedSize = 30;

    /// <summary>倒找 EOCD 的窗口：注释最长 65535，再加记录本身。</summary>
    private const int EocdSearchWindow = EocdSize + 0xFFFF;

    /// <summary>游走时一次读多少字节。整包读进内存对 4 GB 的包不成立，所以分块 + 签名跨块重叠 3 字节。</summary>
    private const int ScanChunk = 1 << 20;

    /// <summary>合法 ZIP 用 16 位存名字长度，但真包极少超过这个数；拿它当假签名的第一道闸。</summary>
    private const int MaxSaneNameLength = 1024;

    public static ZipMap Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Archive to inspect was not found.", fullPath);
        }

        using var stream = File.OpenRead(fullPath);
        return Inspect(stream);
    }

    public static ZipMap Inspect(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanSeek)
        {
            throw new ArgumentException("The ZIP scanner needs a seekable stream.", nameof(input));
        }

        var file = new ZipBytes(input);
        var notes = new List<string>();

        if (file.Length < EocdSize)
        {
            // 连一个 EOCD 都放不下。仍然值得游走局部头 —— 模式三能捞一个成员是一个成员，
            // 所以这里不能提前放弃，只报告"至少截断了这么多"。
            var salvaged = WalkLocalHeaders(file, 0, file.Length, notes);
            notes.Add("文件比一个 EOCD 记录还短，至少被截断了。");
            return Build(file.Length, 0, -1, false, [], salvaged, notes, 0);
        }

        var eocdOffset = FindEndOfCentralDirectory(file, notes);
        var claimedCount = 0L;
        long cdOffset = -1;
        long cdSize = 0;
        var hasZip64 = false;
        var central = new List<ZipCentralEntry>();

        if (eocdOffset < 0)
        {
            notes.Add("找不到结束记录（EOCD）：中央目录要么被截掉，要么整个被覆盖。");
        }
        else
        {
            var eocd = file.Read(eocdOffset, EocdSize);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(eocd.AsSpan(20));
            var trailing = file.Length - (eocdOffset + EocdSize + commentLength);
            if (trailing != 0)
            {
                // 尾部多出字节 = 包被追加或拼接过，分卷续接要面对的正是在这里。
                notes.Add($"EOCD 之后还剩 {trailing} 字节。");
            }

            claimedCount = BinaryPrimitives.ReadUInt16LittleEndian(eocd.AsSpan(10));
            cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd.AsSpan(16));
            cdSize = BinaryPrimitives.ReadUInt32LittleEndian(eocd.AsSpan(12));

            var needs64 = cdOffset == uint.MaxValue || claimedCount == ushort.MaxValue || cdSize == uint.MaxValue;
            if (needs64)
            {
                hasZip64 = true;
                if (!TryReadZip64(file, eocdOffset, out cdOffset, out cdSize, out claimedCount))
                {
                    notes.Add("EOCD 指向 ZIP64 记录，但那条记录读不出来。");
                }
            }
        }

        // SFX 前导：中央目录里的偏移是"相对包本体"的，包本体却不在文件开头。
        // 判据不用猜 —— 把候选前导加上去看那个位置是不是真的中央目录签名，验不中就当 0。
        var prefix = ResolvePrefixLength(file, eocdOffset, cdOffset, claimedCount, hasZip64);
        if (prefix > 0)
        {
            notes.Add($"自解压前导：前 {prefix} 个字节是引导程序，按 ZIP 偏移换算时已加上。");
        }

        if (eocdOffset >= 0 && claimedCount > 0)
        {
            central = ReadCentralDirectory(file, prefix + cdOffset, claimedCount, eocdOffset, notes);
        }

        // 游走局部头：包"实际"有什么。范围止于中央目录（或 EOCD），否则会把中央目录里
        // 那些"看起来像头"的字节（成员名重复出现）认成成员。
        var walkStop = central.Count > 0 ? prefix + cdOffset : eocdOffset > 0 ? eocdOffset : file.Length;
        var locals = WalkLocalHeaders(file, prefix, walkStop, notes);

        if (locals.Count == 0 && central.Count == 0)
        {
            notes.Add("既没有可用的中央目录，也没找到任何局部文件头：这个包里没有能认定的成员。");
        }

        return Build(file.Length, prefix, eocdOffset, hasZip64, central, locals, notes,
            Math.Max(0, (int)claimedCount - central.Count),
            cdOffset >= 0 ? prefix + cdOffset : -1,
            cdSize);
    }

    private static ZipMap Build(
        long length, long prefix, long eocdOffset, bool hasZip64,
        List<ZipCentralEntry> central, List<ZipLocalHeader> locals, List<string> notes, int missing,
        long centralDirectoryOffset = -1, long centralDirectoryLength = 0)
        => new(length, prefix, eocdOffset, hasZip64, central, locals, notes,
            centralDirectoryOffset, centralDirectoryLength)
        {
            MissingCentralEntries = missing,
        };

    // ------------------------------------------------------------------ EOCD

    /// <summary>倒着找 EOCD。注释里可以出现任意字节（包括一个假签名），所以每命中一次都要
    /// 验证"签名 + 22 + 注释长 == 文件尾"才算数。</summary>
    private static long FindEndOfCentralDirectory(ZipBytes file, List<string> notes)
    {
        var window = (int)Math.Min(file.Length, EocdSearchWindow + EocdSize);
        var tail = file.Read(file.Length - window, window);

        for (var i = window - EocdSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EocdSignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            if (file.Length - window + i + EocdSize + commentLength == file.Length)
            {
                return file.Length - window + i;
            }
        }

        _ = notes;    // 让调用方统一收口这些提示，这里不加，避免重复报告
        return -1;
    }

    /// <summary>ZIP64 的定位器固定在 EOCD 之前 20 字节，记录本身可在任何位置。</summary>
    private static bool TryReadZip64(
        ZipBytes file, long eocdOffset, out long cdOffset, out long cdSize, out long entryCount)
    {
        cdOffset = cdSize = entryCount = 0;

        var locatorAt = eocdOffset - 20;
        if (locatorAt < 0)
        {
            return false;
        }

        var locator = file.Read(locatorAt, 20);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(0)) != Eocd64LocatorSignature)
        {
            return false;
        }

        var recordAt = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
        if (recordAt < 0 || recordAt + 56 > file.Length)
        {
            return false;
        }

        var record = file.Read(recordAt, 56);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0)) != Eocd64Signature)
        {
            return false;
        }

        // ZIP64 EOCD：56 字节基本记录里，条目数在 32，中央目录大小在 40，起点在 48。
        entryCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
        cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
        cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
        return true;
    }

    // ------------------------------------------------------------ 中央目录

    private static List<ZipCentralEntry> ReadCentralDirectory(
        ZipBytes file, long start, long claimedCount, long eocdOffset, List<string> notes)
    {
        var entries = new List<ZipCentralEntry>();
        if (start < 0 || start >= file.Length || claimedCount <= 0)
        {
            return entries;
        }

        var position = start;
        for (var i = 0L; i < claimedCount; i++)
        {
            if (position + CentralFixedSize > file.Length)
            {
                notes.Add($"中央目录在第 {i} 条之后越过了文件尾（声称 {claimedCount} 条）。");
                break;
            }

            var head = file.Read(position, CentralFixedSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0)) != CentralSignature)
            {
                notes.Add($"中央目录第 {i} 条声称在位置 {position}，那儿却不是中央目录头。");
                break;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16));
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(20));
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(24));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(32));
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(42));

            var total = CentralFixedSize + nameLength + extraLength + commentLength;
            if (position + total > file.Length)
            {
                // 名字本身就落在被截掉的那一段里，读出来是零填充 —— 这时候报位置比报一个假名字诚实。
                notes.Add($"中央目录第 {i} 条在位置 {position} 处被截断。");
                break;
            }

            var name = ReadName(file, position, nameLength, flags);
            var extra = file.Read(position + CentralFixedSize + nameLength, extraLength);

            // ZIP64 扩展里字段的顺序不是固定的三个都在：只有原本写成 0xFFFFFFFF 的那几个才会出现，
            // 顺序按 未压缩 → 压缩 → 相对位置。所以必须先把"哪些是 0xFFFFFFFF"算清楚再读。
            var needsZip64 = uncompressed == uint.MaxValue || compressed == uint.MaxValue || localOffset == uint.MaxValue;
            if (needsZip64)
            {
                if (TryReadZip64Extra(extra, uncompressed == uint.MaxValue, compressed == uint.MaxValue,
                        localOffset == uint.MaxValue, out var z64Uncompressed, out var z64Compressed, out var z64Offset))
                {
                    uncompressed = z64Uncompressed;
                    compressed = z64Compressed;
                    localOffset = z64Offset;
                }
                else
                {
                    notes.Add($"成员 {name} 声称是 ZIP64，但那条扩展缺失或长度不对，大小只能当未知。");
                    uncompressed = 0;
                    compressed = 0;
                }
            }

            entries.Add(new ZipCentralEntry(name, method, crc, compressed, uncompressed, localOffset, flags, needsZip64));
            position += total;

            if (position > eocdOffset)
            {
                notes.Add($"中央目录读过了 EOCD 的位置（{eocdOffset}），后面的字节不可信。");
                break;
            }
        }

        return entries;
    }

    private static string ReadName(ZipBytes file, long position, ushort nameLength, int flags)
        => DecodeName(file.Read(position + CentralFixedSize, nameLength), flags);

    /// <summary>ZIP64 扩展（0x0001）。按"哪几个 32 位字段是被占位符顶掉的"决定读取顺序 ——
    /// 顺序错一位就会把大小当偏移用，那种错会伪装成成功，所以宁可返回 false。</summary>
    private static bool TryReadZip64Extra(
        ReadOnlySpan<byte> extra,
        bool wantsUncompressed, bool wantsCompressed, bool wantsOffset,
        out long uncompressed, out long compressed, out long localOffset)
    {
        uncompressed = compressed = localOffset = 0;

        var i = 0;
        while (i + 4 <= extra.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(i));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(i + 2));
            var dataAt = i + 4;
            if (dataAt + size > extra.Length)
            {
                return false;   // 扩展长度越界：整条判坏
            }

            if (id == Zip64ExtraId)
            {
                var needed = (wantsUncompressed ? 8 : 0) + (wantsCompressed ? 8 : 0) + (wantsOffset ? 8 : 0);
                if (size < needed || needed == 0)
                {
                    return false;
                }

                var at = dataAt;
                if (wantsUncompressed)
                {
                    uncompressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra.Slice(at));
                    at += 8;
                }

                if (wantsCompressed)
                {
                    compressed = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra.Slice(at));
                    at += 8;
                }

                if (wantsOffset)
                {
                    localOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra.Slice(at));
                }

                return true;
            }

            i = dataAt + size;
        }

        return false;
    }

    // ------------------------------------------------------------ SFX 前导

    /// <summary>
    /// 包本体在文件里的起点。判据是可验证的：中央目录偏移加上候选前导之后，那个位置必须
    /// 真的写着中央目录签名。验不中就用 0 —— 猜一个前导比承认"不知道"更危险，因为后面所有
    /// 搬运都会照着错的位置复制。
    /// </summary>
    private static long ResolvePrefixLength(
        ZipBytes file, long eocdOffset, long cdOffset, long claimedCount, bool hasZip64)
    {
        if (eocdOffset < 0 || claimedCount <= 0)
        {
            return 0;
        }

        if (HasCentralSignature(file, cdOffset))
        {
            return 0;
        }

        // 引导程序里第一个真局部头的位置就是包本体起点。游走用同一套判据，不会把 PE 文件里
        // 凑出来的假签名当回事（名字必须像名字）。
        var candidate = FirstLocalHeaderOffset(file, 0, eocdOffset);
        if (candidate > 0 && HasCentralSignature(file, candidate + cdOffset))
        {
            return candidate;
        }

        return 0;
    }

    private static bool HasCentralSignature(ZipBytes file, long at)
        => at >= 0 && at + 4 <= file.Length
        && BinaryPrimitives.ReadUInt32LittleEndian(file.Read(at, 4)) == CentralSignature;

    private static long FirstLocalHeaderOffset(ZipBytes file, long from, long stop)
    {
        var headers = WalkLocalHeaders(file, from, stop, []);
        return headers.Count > 0 ? headers[0].HeaderOffset : -1;
    }

    // ------------------------------------------------------------ 局部头游走

    private static List<ZipLocalHeader> WalkLocalHeaders(ZipBytes file, long from, long stop, List<string> notes)
    {
        var headers = new List<ZipLocalHeader>();
        var end = Math.Min(stop, file.Length);
        if (end - from < LocalFixedSize)
        {
            return headers;
        }

        var position = from;
        while (position + LocalFixedSize <= end)
        {
            var signature = file.ReadUInt32(position);
            if (signature != LocalSignature)
            {
                position++;
                continue;
            }

            var flags = (int)file.ReadUInt16(position + 6);
            var method = (int)file.ReadUInt16(position + 8);
            var crc = file.ReadUInt32(position + 14);
            long compressed = file.ReadUInt32(position + 18);
            long uncompressed = file.ReadUInt32(position + 22);
            var nameLength = (int)file.ReadUInt16(position + 26);
            var extraLength = (int)file.ReadUInt16(position + 28);

            if (nameLength is <= 0 or > MaxSaneNameLength
                || position + LocalFixedSize + nameLength > end)
            {
                position++;
                continue;
            }

            var nameBytes = file.Read(position + LocalFixedSize, nameLength);
            if (!LooksLikeName(nameBytes))
            {
                position++;
                continue;
            }

            var dataOffset = position + LocalFixedSize + nameLength + extraLength;
            var needsZip64 = uncompressed == uint.MaxValue || compressed == uint.MaxValue;
            if (needsZip64)
            {
                var extra = extraLength > 0 ? file.Read(position + LocalFixedSize + nameLength, extraLength) : [];
                if (TryReadZip64Extra(extra, uncompressed == uint.MaxValue, compressed == uint.MaxValue, false,
                        out var z64Uncompressed, out var z64Compressed, out _))
                {
                    uncompressed = z64Uncompressed;
                    compressed = z64Compressed;
                }
                else
                {
                    // 大小不明：仍然收下来（模式三要按"下一个签名"边界猜），但记一笔。
                    notes.Add($"成员 {DecodeName(nameBytes, flags)} 的 ZIP64 大小读不出来，长度按未知处理。");
                    uncompressed = 0;
                    compressed = 0;
                }
            }

            headers.Add(new ZipLocalHeader(
                DecodeName(nameBytes, flags), method, crc, compressed, uncompressed,
                position, dataOffset, flags, needsZip64, nameLength, extraLength));

            // 头与头之间不该有空隙：按声明大小跳，才是"下一个成员"。大小未知（数据描述符或
            // ZIP64 读失败）时退回逐字节，宁可慢也别把数据里的假签名收进来。
            var next = compressed > 0 && dataOffset + compressed <= end
                ? dataOffset + compressed
                : dataOffset;
            position = Math.Max(next, position + 1);
        }

        return headers;
    }

    /// <summary>名字得像名字。压缩数据里凑出 <c>PK\x03\x04</c> 并不稀奇，凑出一个没有控制字符、
    /// 长度合理、还带路径的样子很稀奇 —— 这一道闸挡掉的假签名比其余加起来都多。</summary>
    private static bool LooksLikeName(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return false;
        }

        foreach (var c in bytes)
        {
            // 允许 UTF-8 的高字节：中文文件名是常态，不是异常。
            if (c < 0x20 && c != '\t')
            {
                return false;
            }

            if (c == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>flags bit 11 决定名字是 UTF-8 还是当年的代码页。非 UTF-8 时用系统码页解，
    /// 中文包里那批 GBK 名字只有这样才不会变成一排问号。</summary>
    private static string DecodeName(ReadOnlySpan<byte> bytes, int flags)
    {
        if ((flags & 0x800) != 0)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        try
        {
            return Encoding.GetEncoding(0).GetString(bytes);   // 0 = 系统当前代码页
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>带一个小缓存的随机读：游走要逐字节试探，每次一个 syscall 会慢到不可用，
    /// 整包读进内存又撑不住大文件，所以取 1 MiB 窗口。</summary>
    private sealed class ZipBytes
    {
        private readonly Stream _stream;
        private readonly byte[] _chunk = new byte[ScanChunk];
        private long _chunkOffset = -1;
        private int _chunkLength;

        public ZipBytes(Stream stream)
        {
            _stream = stream;
            Length = stream.Length;
        }

        public long Length { get; }

        public byte[] Read(long offset, int count)
        {
            var buffer = new byte[count];
            if (count == 0 || offset < 0)
            {
                return buffer;
            }

            // 命中窗口就零拷贝；否则直接 seek 读（这些调用都是几十字节，不划算再走窗口）。
            if (offset >= _chunkOffset && offset + count <= _chunkOffset + _chunkLength)
            {
                Array.Copy(_chunk, (int)(offset - _chunkOffset), buffer, 0, count);
                return buffer;
            }

            _stream.Position = offset;
            var read = 0;
            while (read < count)
            {
                var got = _stream.Read(buffer.AsSpan(read, count - read));
                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            return buffer;
        }

        public uint ReadUInt32(long offset)
        {
            if (offset < 0 || offset + 4 > Length)
            {
                return 0;
            }

            if (!InChunk(offset, 4))
            {
                FillChunk(offset);
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(_chunk.AsSpan((int)(offset - _chunkOffset)));
        }

        public ushort ReadUInt16(long offset)
        {
            if (offset < 0 || offset + 2 > Length)
            {
                return 0;
            }

            if (!InChunk(offset, 2))
            {
                FillChunk(offset);
            }

            return BinaryPrimitives.ReadUInt16LittleEndian(_chunk.AsSpan((int)(offset - _chunkOffset)));
        }

        private bool InChunk(long offset, int count)
            => _chunkOffset >= 0 && offset >= _chunkOffset && offset + count <= _chunkOffset + _chunkLength;

        private void FillChunk(long offset)
        {
            // 窗口起点往前挪一点点没有意义，但结尾要保证一个头结构（30 字节 + 名字）不被切断，
            // 否则每个跨块的都要走慢路径。
            var want = (int)Math.Min(_chunk.Length, Length - offset);
            _stream.Position = offset;
            var read = 0;
            while (read < want)
            {
                var got = _stream.Read(_chunk.AsSpan(read, want - read));
                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            _chunkOffset = offset;
            _chunkLength = read;
        }
    }
}
