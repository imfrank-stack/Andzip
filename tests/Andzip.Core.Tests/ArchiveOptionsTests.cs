using System.IO.Compression;
using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

public class ArchiveOptionsTests
{
    // ---------- CompressionLevelOption（FR-2.4） ----------

    [Fact]
    public void CompressionLevels_AreFiveTiersInSpeedOrder()
    {
        Assert.Equal(5, CompressionLevelOption.All.Count);
        Assert.Equal([0, 1, 2, 3, 4], CompressionLevelOption.All.Select(l => l.Id).ToArray());
        // 越靠前越快 —— 排序本身就是"前三档拼速度"这条规则的体现。
        Assert.Equal([0, 1, 3, 5, 9], CompressionLevelOption.All.Select(l => l.EngineLevel).ToArray());
        Assert.Equal(CompressionLevelOption.Normal, CompressionLevelOption.Default);
    }

    [Fact]
    public void CompressionLevels_MapToEngineLevels()
    {
        // 引擎的级别是 0..9（-mx=），不再是 System.IO.Compression 的四档枚举。
        Assert.Equal(0, CompressionLevelOption.Store.EngineLevel);
        Assert.Equal(1, CompressionLevelOption.UltraFast.EngineLevel);
        Assert.Equal(3, CompressionLevelOption.Fast.EngineLevel);
        Assert.Equal(5, CompressionLevelOption.Normal.EngineLevel);
        Assert.Equal(9, CompressionLevelOption.Max.EngineLevel);
    }

    [Fact]
    public void CompressionLevels_PositionIsNotTheSameThingAsId()
    {
        // 下拉框用的是位置，设置文件里存的是 Id。今天两者相等，将来插入一档就会分叉 ——
        // 钉住这个区别，免得有人图省事把 Id 当 SelectedIndex 用。
        Assert.Equal(2, CompressionLevelOption.PositionOf(CompressionLevelOption.Fast));
        Assert.Equal(-1, CompressionLevelOption.PositionOf(new CompressionLevelOption(99, "X", 5)));
    }

    [Fact]
    public void CompressionLevels_UseReferenceTextKeys()
    {
        Assert.Equal("TEXT_COMPRESSIONMETHOD_STORE", CompressionLevelOption.Store.TextKey);
        Assert.Equal("TEXT_COMPRESSIONMETHOD_ULTRAFAST", CompressionLevelOption.UltraFast.TextKey);
        Assert.Equal("TEXT_COMPRESSIONMETHOD_FAST", CompressionLevelOption.Fast.TextKey);
        Assert.Equal("TEXT_COMPRESSIONMETHOD_NORMAL", CompressionLevelOption.Normal.TextKey);
        Assert.Equal("TEXT_COMPRESSIONMETHOD_MAX", CompressionLevelOption.Max.TextKey);
    }

    [Theory]
    [InlineData(9)]      // 四档时代没人写过 9，但手改过的文件什么都有
    [InlineData(-1)]
    [InlineData(4711)]
    public void CompressionLevel_FromId_UnknownFallsBackInsteadOfThrowing(int id)
    {
        // 绿色版可能读到更新版本写下的档位号。认不出来要按最保守的默认继续工作，
        // 而不是在启动路径上抛一个用户看不懂的异常。
        Assert.Equal(CompressionLevelOption.Normal, CompressionLevelOption.FromId(id));
    }

    // ---------- ArchiveFormatOption（FR-2.2 / 附录 D.1） ----------

    [Fact]
    public void Formats_MatchReferenceOrder()
    {
        Assert.Equal(
            ["ZIP", "ZIPX", "EXE", "TAR", "TGZ", "LZH", "ISO", "7Z", "GZ", "BZ2", "XZ", "ZPAQ"],
            ArchiveFormatOption.All.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void Formats_CreatableSet_MatchesImplementedEngines()
    {
        Assert.True(ArchiveFormatOption.Default.CanCreate);
        Assert.Equal("ZIP", ArchiveFormatOption.Default.Id);

        // 换引擎后新解锁的三项：这正是本次重构的目的，所以逐条钉住而不是只比总数。
        foreach (var id in new[] { "7Z", "BZ2", "XZ" })
        {
            Assert.True(ArchiveFormatOption.FromId(id).CanCreate, $"{id} 应该可以创建");
        }

        // 只读项保持不可创建（引擎能读但写不出来，或本工具不打算写）。
        foreach (var id in new[] { "ZIPX", "EXE", "LZH", "ISO", "ZPAQ" })
        {
            Assert.False(ArchiveFormatOption.FromId(id).CanCreate, $"{id} 不应该可以创建");
        }

        // 可创建集合只能是表里的这些，且必须全部被引擎确认。
        Assert.Subset(
            new HashSet<string>(ArchiveFormatOption.All.Select(f => f.Id)),
            new HashSet<string>(ArchiveFormatOption.All.Where(f => f.CanCreate).Select(f => f.Id)));
    }

    [Fact]
    public void Formats_FromId_IsCaseInsensitive()
        => Assert.Equal("7Z", ArchiveFormatOption.FromId("7z").Id);

    [Fact]
    public void Formats_FromId_UnknownThrows()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveFormatOption.FromId("RAR"));

    // ---------- ArchiveInputItem（FR-2.1） ----------

    [Fact]
    public void InputItem_FromFile_ReportsSize()
    {
        var dir = Directory.CreateTempSubdirectory("andzip-item-");
        try
        {
            var path = Path.Combine(dir.FullName, "a.txt");
            File.WriteAllText(path, "12345");

            var item = ArchiveInputItem.FromPath(path);

            Assert.False(item.IsDirectory);
            Assert.Equal("a.txt", item.Name);
            Assert.Equal(5, item.Size);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void InputItem_FromDirectory_HasNoSize()
    {
        var dir = Directory.CreateTempSubdirectory("andzip-item-");
        try
        {
            var item = ArchiveInputItem.FromPath(dir.FullName);

            Assert.True(item.IsDirectory);
            Assert.Null(item.Size);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void InputItem_MissingPath_Throws()
    {
        var missing = Path.Combine(Path.GetTempPath(), "andzip-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<FileNotFoundException>(() => ArchiveInputItem.FromPath(missing));
    }
}