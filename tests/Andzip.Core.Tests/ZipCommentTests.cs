using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 归档注释的读与写（ZIP 的 EOCD 尾部那一句）。「编辑 → 压缩文件注释」那一项的地基。
/// <para>
/// 验收刻意分成三层：<strong>自己读</strong>（<see cref="ZipComment.Read"/>）、
/// <strong>引擎读</strong>（<see cref="ArchiveReader.Read"/> 的 <c>Comment</c> —— 界面上真正走的那条路）、
/// <strong>别人读</strong>（<see cref="System.IO.Compression"/> 的 <c>ZipArchive.Comment</c>）。
/// 只验自己那一层的话，"两个实现约定一致、都与 ZIP 规范无关"这种错看不出来。
/// </para>
/// </summary>
public class ZipCommentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-comment-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(params string[] parts) => Path.Combine([_root, .. parts]);

    /// <summary>造一个正常 zip（.NET 的实现，独立于本引擎）。</summary>
    private string BuildZip(params (string Name, string Text)[] entries)
    {
        var path = PathOf($"box-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
            }
        }

        return path;
    }

    private static Dictionary<string, string> ReadWithDotNet(string zipPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result[entry.FullName] = Encoding.UTF8.GetString(buffer.ToArray());
        }

        return result;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>把一个原始的注释字节串塞进 EOCD（长度字段一起改）。</summary>
    private static void WriteRawComment(string zipPath, byte[] bytes)
    {
        var map = ZipScanner.Inspect(zipPath);
        Assert.True(map.EocdOffset >= 0);

        using var file = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        file.Position = map.EocdOffset + 20;
        file.Write([(byte)(bytes.Length & 0xFF), (byte)(bytes.Length >> 8)]);
        file.Position = map.EocdOffset + 22;
        file.Write(bytes);
        file.SetLength(map.EocdOffset + 22 + bytes.Length);
    }

    // ------------------------------------------------------------ 往返

    [Fact]
    public void ACommentRoundTripsThroughOurReaderAndAnotherImplementation()
    {
        var zip = BuildZip(("a.txt", "AAA"), ("docs/说明.md", "中文"));
        const string comment = "这一句是归档注释 — 中文、破折号与 ✓ 都在这。";

        ZipComment.Write(zip, comment);

        // 自己读
        Assert.Equal(comment, ZipComment.Read(zip));

        // 别人读：.NET 自己的 ZipArchive 也认这一句，而且成员一个不少
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Equal(comment, archive.Comment);
        }

        var members = ReadWithDotNet(zip);
        Assert.Equal("AAA", members["a.txt"]);
        Assert.Equal("中文", members["docs/说明.md"]);
    }

    /// <summary>
    /// 引擎自己那一份注释读出来不是我们写进去的那句话 —— <strong>这是量出来的事实，也是本类存在的理由</strong>。
    /// <para>7-Zip 的 <c>ZipHandler.cpp</c> 把注释字节按 <c>CP_ACP</c> 解，而 ZIP 规范没有给注释留编码位，
    /// 我们写的是 UTF-8：于是「中文注释」在引擎那里是别的六个字（实测是「涓枃娉ㄩ噴」，
    /// 但这里<strong>不把那串乱码当字面量钉死</strong> —— 它取决于本机代码页，换台机器就换一串）。
    /// 那条判据在 vendor 进来的第三方源码里，而本工程对它的约定是逐字符原样、不改一处，
    /// 所以注释这一格由 <see cref="ZipComment"/> 自己读字节。</para>
    /// <para>这条用例钉住的是<strong>"界面上的注释必须走 ZipComment、不许走清单里的 Comment"</strong>：
    /// 谁哪天把对话框的预填改成 <c>ArchiveListing.Comment</c>（"看起来更省事"），这条会红着告诉他为什么不行。</para>
    /// </summary>
    [Fact]
    public void TheEnginesOwnDecodeIsNotUtf8AwareSoTheCommentFeatureDoesNotUseIt()
    {
        var zip = BuildZip(("a.txt", "AAA"));
        const string comment = "中文注释";

        ZipComment.Write(zip, comment);

        Assert.Equal(comment, ZipComment.Read(zip));
        Assert.NotEqual(comment, ArchiveReader.Read(zip).Comment);
    }

    [Fact]
    public void TheCommentIsUtf8BytesAndTheRecordedLengthMatchesThem()
    {
        var zip = BuildZip(("a.txt", "AAA"));
        const string comment = "注释 ✓";
        var expected = Encoding.UTF8.GetBytes(comment);

        ZipComment.Write(zip, comment);

        var bytes = File.ReadAllBytes(zip);
        var eocdAt = bytes.Length - 22 - expected.Length;

        // 结束记录还在这一句注释前面，长度字段说的就是注释的字节数（不是字符数）
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(eocdAt)));
        Assert.Equal(expected.Length, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(eocdAt + 20)));
        Assert.Equal(expected, bytes[(eocdAt + 22)..]);
    }

    [Fact]
    public void ClearingTheCommentLeavesAnArchiveThatStillOpens()
    {
        var zip = BuildZip(("a.txt", "AAA"));
        ZipComment.Write(zip, "先写一句");
        Assert.Equal("先写一句", ZipComment.Read(zip));

        ZipComment.Write(zip, null);

        Assert.Null(ZipComment.Read(zip));
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Empty(archive.Comment);
        }

        // 文件尾就是结束记录本身，没有再拖着 22 + 0 之外的字节
        var map = ZipScanner.Inspect(zip);
        Assert.Equal(map.EocdOffset + 22, new FileInfo(zip).Length);
        Assert.Equal("AAA", ReadWithDotNet(zip)["a.txt"]);
    }

    [Fact]
    public void GrowingAndShrinkingTheCommentKeepsTheMembers()
    {
        var zip = BuildZip(("a.txt", "AAA"), ("b.txt", "BBB"));

        foreach (var comment in new[] { "短的", new string('长', 500), "x", string.Empty, "又一句 ✓" })
        {
            ZipComment.Write(zip, comment);

            var expected = string.IsNullOrWhiteSpace(comment) ? null : comment;
            Assert.Equal(expected, ZipComment.Read(zip));
            using (var archive = ZipFile.OpenRead(zip))
            {
                Assert.Equal(expected ?? string.Empty, archive.Comment);
            }

            var members = ReadWithDotNet(zip);
            Assert.Equal("AAA", members["a.txt"]);
            Assert.Equal("BBB", members["b.txt"]);
        }
    }

    /// <summary>
    /// 别的工具用系统代码页写的注释：我们解不出正确的中文（ZIP 没给注释留编码位），
    /// 但至少<strong>不丢字节、不抛异常、不出现问号</strong>。
    /// <para>「原文没改就不写回去」那条规矩在界面上（打开注释对话框直接点确定，文件不该被动过），
    /// 这里只钉读的那一半。写的那一半一写就会把别人那串字节换成 UTF-8 —— 那正是界面要避开的事。</para>
    /// </summary>
    [Fact]
    public void AForeignCodePageCommentIsNotMangledIntoQuestionMarks()
    {
        var zip = BuildZip(("a.txt", "AAA"));
        var gbk = new byte[] { 0xD6, 0xD0, 0xCE, 0xC4, 0xD7, 0xA2, 0xCA, 0xCD };   // "中文注释" 的 GBK 字节
        WriteRawComment(zip, gbk);

        var text = ZipComment.Read(zip);

        Assert.NotNull(text);
        Assert.Equal(gbk.Length, text!.Length);
        Assert.Equal(gbk, Encoding.Latin1.GetBytes(text));
        Assert.DoesNotContain('?', text);
    }

    // ------------------------------------------------------------ 拒绝

    [Fact]
    public void AnOverLongCommentIsRefusedAndTheFileIsUntouched()
    {
        var zip = BuildZip(("a.txt", "AAA"));
        ZipComment.Write(zip, "原本那一句");
        var before = Hash(zip);

        // 65535 字节是上限，这里多一个字节；"字"是 ASCII，所以字节数 == 字符数
        var tooLong = new string('x', ZipComment.MaxBytes + 1);
        var ex = Assert.Throws<ArchiveConstraintException>(() => ZipComment.Write(zip, tooLong));

        Assert.Equal(ArchiveConstraintReason.ArchiveCommentTooLong, ex.Reason);
        Assert.Equal(before, Hash(zip));
        Assert.Equal("原本那一句", ZipComment.Read(zip));
    }

    [Fact]
    public void TheLimitItselfIsAccepted()
    {
        var zip = BuildZip(("a.txt", "AAA"));
        var atLimit = new string('y', ZipComment.MaxBytes);

        ZipComment.Write(zip, atLimit);

        Assert.Equal(atLimit, ZipComment.Read(zip));
    }

    [Fact]
    public void ANonZipIsRefusedAndLeftUntouched()
    {
        // ① 7z：引擎造得出来，但它的归档注释不在结束记录里（连概念都没有）
        var sevenZip = PathOf("box.7z");
        var source = PathOf("a.txt");
        File.WriteAllText(source, "AAA");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(source)],
            new ArchiveCreationSettings(
                sevenZip,
                CompressionLevelOption.Default,
                Format: ArchiveFormatOption.FromId("7z"),
                IncludeRootFolder: false));

        // ② 根本不是压缩包的文件
        var plain = PathOf("plain.txt");
        File.WriteAllText(plain, "not an archive at all");

        foreach (var path in new[] { sevenZip, plain })
        {
            var before = Hash(path);
            var read = Assert.Throws<ArchiveConstraintException>(() => ZipComment.Read(path));
            var write = Assert.Throws<ArchiveConstraintException>(() => ZipComment.Write(path, "想说一句"));

            Assert.Equal(ArchiveConstraintReason.ArchiveCommentNotSupported, read.Reason);
            Assert.Equal(ArchiveConstraintReason.ArchiveCommentNotSupported, write.Reason);
            Assert.Equal(before, Hash(path));
        }
    }

    [Fact]
    public void AMissingArchiveIsReportedAsMissingRatherThanAsAnUnsupportedFormat()
    {
        Assert.Throws<FileNotFoundException>(() => ZipComment.Write(PathOf("nope.zip"), "一句话"));
    }

    // ------------------------------------------------------------ 与包内编辑共存

    /// <summary>
    /// 增删成员之后注释还在。这条量的是<strong>引擎的行为</strong>而不是我们的代码：
    /// 7-Zip 在改写既有包时会把输入包的 <c>ArcInfo.Comment</c> 原样带过去（ZipUpdate.cpp 那行
    /// <c>inArchive ? &amp;inArchive-&gt;ArcInfo.Comment : NULL</c>）。如果哪天它不带了，
    /// 这里立刻红 —— 那时候「编辑」里每一次增删都要变成"先记住注释、改完再写回去"。
    /// </summary>
    [Fact]
    public void AnExistingCommentSurvivesAddingAndDeletingMembers()
    {
        var zip = BuildZip(("a.txt", "AAA"), ("b.txt", "BBB"));
        ZipComment.Write(zip, "改成员之前就在的那一句");

        var added = PathOf("c.txt");
        File.WriteAllText(added, "CCC");
        var indices = ArchiveReader.Read(zip).Entries.ToDictionary(e => e.Path, e => (uint)e.Index);

        ArchiveUpdater.Update(new ArchiveUpdateSettings(
            zip,
            [indices["a.txt"], indices["b.txt"]],
            [new ArchiveAddedMember("c.txt", SourcePath: added)]));

        Assert.Equal("改成员之前就在的那一句", ZipComment.Read(zip));
        var members = ReadWithDotNet(zip);
        Assert.Equal("AAA", members["a.txt"]);
        Assert.Equal("CCC", members["c.txt"]);

        // 再把刚加的那一个删掉，注释还是那一句
        var afterAdd = ArchiveReader.Read(zip).Entries.ToDictionary(e => e.Path, e => (uint)e.Index);
        ArchiveUpdater.Update(new ArchiveUpdateSettings(zip, [afterAdd["a.txt"], afterAdd["b.txt"]], []));

        Assert.Equal("改成员之前就在的那一句", ZipComment.Read(zip));
        Assert.Equal("AAA", ReadWithDotNet(zip)["a.txt"]);
    }

    // ------------------------------------------------------------ 两种不普通的包

    /// <summary>手搭一个最小 ZIP64（局部头 + 中央目录 + ZIP64 三件套），尾部带一句注释。</summary>
    private string BuildZip64(string? comment)
    {
        var path = PathOf("zip64.zip");
        using var ms = new MemoryStream();

        void U16(ushort v) => ms.Write(BitConverter.GetBytes(v));
        void U32(uint v) => ms.Write(BitConverter.GetBytes(v));
        void U64(ulong v) => ms.Write(BitConverter.GetBytes(v));

        var name = Encoding.UTF8.GetBytes("huge.bin");
        U32(0x04034b50);
        U16(45); U16(0x800); U16(8); U16(0); U16(0); U32(0xDEADBEEF);
        U32(uint.MaxValue); U32(uint.MaxValue);
        U16((ushort)name.Length); U16(20);
        ms.Write(name);
        U16(0x0001); U16(16); U64(5_000_000_000UL); U64(4_000_000_000UL);
        ms.Write(new byte[8]);

        var cdAt = ms.Position;
        U32(0x02014b50);
        U16(45); U16(45); U16(0x800); U16(8); U16(0); U16(0); U32(0xDEADBEEF);
        U32(uint.MaxValue); U32(uint.MaxValue);
        U16((ushort)name.Length); U16(28); U16(0); U16(0); U16(0); U32(0); U32(0);
        ms.Write(name);
        U16(0x0001); U16(24); U64(5_000_000_000UL); U64(4_000_000_000UL); U64(0);

        var z64At = ms.Position;
        U32(0x06064b50); U64(44); U16(45); U16(45); U32(0); U32(0);
        U64(1); U64(1); U64((ulong)(ms.Position - cdAt)); U64((ulong)cdAt);
        U32(0x07064b50); U32(0); U64((ulong)z64At); U32(1);

        var commentBytes = comment is null ? [] : Encoding.UTF8.GetBytes(comment);
        U32(0x06054b50); U16(0); U16(0); U16(0xFFFF); U16(0xFFFF); U32(0xFFFFFFFF); U32(uint.MaxValue);
        U16((ushort)commentBytes.Length);
        ms.Write(commentBytes);

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    [Fact]
    public void AZip64ArchiveKeepsItsCommentAndItsZip64Records()
    {
        var zip = BuildZip64("zip64 上的第一句");
        Assert.Equal("zip64 上的第一句", ZipComment.Read(zip));
        Assert.True(ZipScanner.Inspect(zip).HasZip64);

        ZipComment.Write(zip, "换一句更长的 —— 中文注释在 ZIP64 上同样只是尾部那几个字节。");

        Assert.Equal("换一句更长的 —— 中文注释在 ZIP64 上同样只是尾部那几个字节。", ZipComment.Read(zip));
        var map = ZipScanner.Inspect(zip);
        Assert.True(map.HasZip64);
        Assert.False(map.Damaged);
        Assert.Equal(map.EocdOffset + 22 + map.CommentLength, new FileInfo(zip).Length);
    }

    /// <summary>自解压包：包本体前面有一段引导程序，EOCD 里的偏移都是相对包本体的。
    /// <para>成员用 <see cref="ZipScanner"/> 验，不用引擎也不用 .NET：.NET 的 <see cref="ZipFile"/>
    /// 不做前导补偿（直接报"EOCD 里的条目数与中央目录对不上"），而引擎只认<strong>真的</strong>自解压引导程序
    /// —— 这里塞的是随机字节，它连格式都认不出来（实测 <c>FormatNotSupported</c>）。
    /// 我们自己的扫描器两条都吃得下，这也正是注释这条路上真正用到的东西。</para>
    /// </summary>
    [Fact]
    public void AnSfxPrefixedZipIsEditedInPlaceWithoutLosingThePrefix()
    {
        var plain = BuildZip(("a.txt", "AAA"));
        var sfx = PathOf("sfx.exe");
        var prefix = new byte[4096];
        new Random(11).NextBytes(prefix);
        File.WriteAllBytes(sfx, [.. prefix, .. File.ReadAllBytes(plain)]);

        ZipComment.Write(sfx, "自解压包上的注释");

        var map = ZipScanner.Inspect(sfx);
        Assert.Equal(prefix.LongLength, map.PrefixLength);
        Assert.Contains("a.txt", map.CentralDirectory.Select(c => c.Name));
        Assert.Equal("自解压包上的注释", ZipComment.Read(sfx));
    }
}
