using Andzip.Core.Settings;

namespace Andzip.Core.Tests;

/// <summary>
/// 设置存储：一个扁平 key=value 文件。这些用例钉的是「没设过」与「设成默认值」的区分、
/// 以及删除键的行为 —— 设置页要用这两个性质来决定预勾选与「立即应用」是否可用。
/// </summary>
public class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Directory.CreateTempSubdirectory("andzip-settings-").FullName, "settings.ini");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Load_MissingFile_YieldsDefaults()
    {
        var settings = AppSettings.Load(_path);

        Assert.False(settings.Has("anything"));
        Assert.Equal("fallback", settings.GetString("anything", "fallback"));
        Assert.True(settings.GetBool("anything", fallback: true));
        Assert.Empty(settings.GetStringList("anything"));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsScalarsAndLists()
    {
        var settings = AppSettings.Load(_path);
        settings.SetString("language", "SimpChinese");
        settings.SetBool("show_start_screen", true);
        settings.SetStringList("file_associations", ["zip", "7z", "tar"]);
        settings.Save(_path);

        var reloaded = AppSettings.Load(_path);
        Assert.Equal("SimpChinese", reloaded.GetString("language"));
        Assert.True(reloaded.GetBool("show_start_screen"));
        Assert.Equal(["zip", "7z", "tar"], reloaded.GetStringList("file_associations").ToArray());
    }

    [Fact]
    public void SetString_Null_RemovesTheKey()
    {
        var settings = AppSettings.Load(_path);
        settings.SetString("gone", "value");
        Assert.True(settings.Has("gone"));

        settings.SetString("gone", null);
        Assert.False(settings.Has("gone"));
    }

    [Fact]
    public void EmptyList_IsStoredAsRemovedSoDefaultsCanTakeOver()
    {
        // 「没设过」要能触发默认勾选，所以空列表必须等价于删除键，而不是存一个空串。
        var settings = AppSettings.Load(_path);
        settings.SetStringList("file_associations", ["zip"]);
        settings.SetStringList("file_associations", []);

        Assert.False(settings.Has("file_associations"));
    }

    [Fact]
    public void Load_IgnoresCommentsAndMalformedLines()
    {
        File.WriteAllText(_path, """
            ; comment
            # another comment

            key = value
            =novalue
            nokey
            spaced=trimmed
            """);

        var settings = AppSettings.Load(_path);
        Assert.Equal("value", settings.GetString("key"));
        Assert.Equal("trimmed", settings.GetString("spaced"));
        Assert.False(settings.Has("novalue"));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("0", false)]
    [InlineData("nonsense", false)]
    public void GetBool_AcceptsTheTwoSpellingsItWrites(string stored, bool expected)
    {
        var settings = AppSettings.Load(_path);
        settings.SetString("flag", stored);

        Assert.Equal(expected, settings.GetBool("flag"));
    }

    [Fact]
    public void Keys_AreCaseInsensitive()
    {
        var settings = AppSettings.Load(_path);
        settings.SetString("File_Associations", "zip");

        Assert.Equal("zip", settings.GetString("file_associations"));
        Assert.True(settings.Has("FILE_ASSOCIATIONS"));
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var nested = Path.Combine(Path.GetDirectoryName(_path)!, "deeper", "settings.ini");

        var settings = AppSettings.Load(nested);
        settings.SetString("k", "v");
        settings.Save(nested);

        Assert.True(File.Exists(nested));
        Assert.Equal("v", AppSettings.Load(nested).GetString("k"));
    }

    [Fact]
    public void DefaultFilePath_PointsUnderPerUserApplicationData()
    {
        var path = AppSettings.DefaultFilePath;

        Assert.EndsWith("settings.ini", path);
        Assert.Contains("Andzip", path);
        // 设置跟着用户走：绿色版常被放进只读位置，程序目录不可写。
        Assert.NotEqual(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(path));
    }
}
