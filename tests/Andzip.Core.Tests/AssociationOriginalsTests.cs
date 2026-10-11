using Andzip.Core.Settings;

namespace Andzip.Core.Tests;

/// <summary>
/// 「接管前的默认程序」映射的编解码。这份数据是撤销关联时唯一能还原默认程序的依据，
/// 所以它坏不得：脏数据必须被跳过而不是被当成一条「把默认值设成空」的指令。
/// </summary>
public class AssociationOriginalsTests
{
    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        var originals = new[]
        {
            new AssociationOriginal("zip", "CompressedFolder"),
            new AssociationOriginal("7z", "7-Zip.7z"),
            new AssociationOriginal("tar", string.Empty),
        };

        var decoded = AssociationOriginals.Decode(AssociationOriginals.Encode(originals));

        Assert.Equal(3, decoded.Count);
        Assert.Equal("CompressedFolder", decoded.Single(i => i.Extension == "zip").ProgId);
        Assert.Equal("7-Zip.7z", decoded.Single(i => i.Extension == "7z").ProgId);
        // 空 ProgID 表示「当时根本没有默认值」，撤销时要删值而不是写回空串 —— 这个区分必须留住。
        Assert.Equal(string.Empty, decoded.Single(i => i.Extension == "tar").ProgId);
    }

    [Fact]
    public void Encode_DeduplicatesByExtensionKeepingTheFirst()
    {
        // 同一扩展名被两个格式条目共用时（rar / r00 之类），第一次接管前看到的才是真原值。
        var encoded = AssociationOriginals.Encode(
        [
            new AssociationOriginal("rar", "WinRAR.rar"),
            new AssociationOriginal("rar", "Andzip.rar"),
        ]);

        Assert.Equal("rar=WinRAR.rar", encoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decode_EmptyInput_YieldsNothing(string? stored)
        => Assert.Empty(AssociationOriginals.Decode(stored));

    [Fact]
    public void Decode_SkipsRecordsWithoutAKey()
    {
        var decoded = AssociationOriginals.Decode("=novalue;zip=Ok;garbage;7z=AlsoOk");

        Assert.Equal(["zip", "7z"], decoded.Select(i => i.Extension).ToArray());
    }

    [Fact]
    public void Decode_NormalisesDotsAndCase()
        => Assert.Equal("zip", Assert.Single(AssociationOriginals.Decode(".ZIP=CompressedFolder")).Extension);

    [Fact]
    public void Decode_KeepsEqualsSignsInsideTheProgId()
    {
        // 只按第一个 = 切分：ProgID 里带 = 也不该被截断。
        var single = Assert.Single(AssociationOriginals.Decode("zip=Some.ProgID=v2"));

        Assert.Equal("Some.ProgID=v2", single.ProgId);
    }

    [Fact]
    public void Encode_BlankExtensionsAreDropped()
        => Assert.Equal("zip=A", AssociationOriginals.Encode([new AssociationOriginal("  ", "A"), new AssociationOriginal("zip", "A")]));
}
