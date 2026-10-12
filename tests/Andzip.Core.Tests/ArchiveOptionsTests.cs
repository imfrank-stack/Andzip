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

    // ---------- 创建对话框能列出来的格式 ----------

    [Fact]
    public void Creatable_FormatsAreAllCreatableAndKeepTheReferenceOrder()
    {
        // 用户的话是"列出来就一定要实现"。这张表就是对话框的下拉框，所以这里断言的不是数量，
        // 而是"没有任何一项是点下去只会报暂未实现的"。
        var creatable = ArchiveFormatOption.Creatable;

        Assert.NotEmpty(creatable);
        Assert.All(creatable, format => Assert.True(format.CanCreate, $"{format.Id} 报称可创建，引擎却写不出来"));

        // 一次同时钉住"成员"与"顺序"：这张表少了项、或者换了顺序，都是界面上的可见变化。
        Assert.Equal(
            ArchiveFormatOption.All.Where(format => format.CanCreate).Select(format => format.Id),
            creatable.Select(format => format.Id));
    }

    [Fact]
    public void Creatable_ExcludesTheReadOnlyEntriesTheReferenceSoftwareLists()
    {
        // ZIPX / EXE / LZH / ISO / ZPAQ 是照参考软件摆进 All 的只读候选，选中之下只能开始一句
        // 「该功能暂未实现」。它们仍然留在 All 里（扩展名归类那些用途要），
        // 但不能再出现在创建对话框上。
        foreach (var id in new[] { "ZIPX", "EXE", "LZH", "ISO", "ZPAQ" })
        {
            Assert.Equal(id, ArchiveFormatOption.FromId(id).Id);
            Assert.False(ArchiveFormatOption.FromId(id).CanCreate);
            Assert.DoesNotContain(ArchiveFormatOption.FromId(id), ArchiveFormatOption.Creatable);
        }
    }

    [Fact]
    public void IsReadablePath_RecognisesArchiveExtensionsWithoutOpeningTheFile()
    {
        Assert.True(ArchiveFormatOption.IsReadablePath(@"C:\x\box.zip"));
        Assert.True(ArchiveFormatOption.IsReadablePath(@"C:\x\BOX.ZIP"));
        Assert.False(ArchiveFormatOption.IsReadablePath(@"C:\x\note.txt"));
        Assert.False(ArchiveFormatOption.IsReadablePath(@"C:\x\noextension"));
    }

    [Fact]
    public void IsReadablePath_NeverCallsAFolderAnArchive()
    {
        var directory = Directory.CreateTempSubdirectory("az-drop-");

        try
        {
            Assert.False(ArchiveFormatOption.IsReadablePath(directory.FullName));

            // 名字长得像包也得是文件才算 —— 真拖一个叫 photos.zip 的文件夹进窗口，
            // 那是"要压的东西"，不是"要看的东西"。
            Directory.CreateDirectory(Path.Combine(directory.FullName, "photos.zip"));
            Assert.False(ArchiveFormatOption.IsReadablePath(Path.Combine(directory.FullName, "photos.zip")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}