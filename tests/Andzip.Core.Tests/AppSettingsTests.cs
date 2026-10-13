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

    // ---------- Reload：两份实例并存时的唯一安全网 ----------

    /// <summary>
    /// 主窗口与设置窗口各持一份实例，而写盘是"整份快照盖过去"。
    /// 不重读的话有两件事会同时发生：界面按旧值重新应用（看着像设置没生效），
    /// 以及主窗口下一次写盘把用户刚改的那一项抹掉（连重启都找不回来）。
    /// 0.13.0 之前「自动展开树里的文件夹」就是这么坏的。
    /// </summary>
    [Fact]
    public void Reload_PicksUpWhatAnotherInstanceWrote()
    {
        var window = AppSettings.Load(_path);
        window.SetBool("view_auto_expand_tree", true);
        window.Save(_path);

        var dialog = AppSettings.Load(_path);
        dialog.SetBool("view_auto_expand_tree", false);
        dialog.Save(_path);

        Assert.True(window.GetBool("view_auto_expand_tree", fallback: true), "前提：窗口手里那份还是旧值");

        window.Reload(_path);

        Assert.False(window.GetBool("view_auto_expand_tree", fallback: true));
    }

    [Fact]
    public void StaleSnapshot_SaveErasesWhatAnotherInstanceWrote()
    {
        // 反向钉住后果（而不是修复）：不重读就直接写盘，别人刚写的值会整条消失。
        // 这条用例读起来像在给 bug 背书，它的用处是说明为什么 Reload 不能省。
        var window = AppSettings.Load(_path);
        window.SetString("language", "SimpChinese");
        window.Save(_path);

        var dialog = AppSettings.Load(_path);
        dialog.SetString("language", "English");
        dialog.Save(_path);

        window.Save(_path);

        var afterClobber = AppSettings.Load(_path).GetString("language");
        Assert.True(afterClobber == "SimpChinese", $"旧快照写盘应当把 English 抹掉，实际留下的是「{afterClobber}」");

        // 反过来，先重读再写就两全：这一项保住了，窗口自己要记的那一项也记上了。
        var other = AppSettings.Load(_path);
        other.SetString("language", "English");
        other.Save(_path);

        window.Reload(_path);
        window.SetString("recent_files", @"C:\a.zip");
        window.Save(_path);

        var final = AppSettings.Load(_path);
        Assert.Equal("English", final.GetString("language"));
        Assert.Equal(@"C:\a.zip", final.GetString("recent_files"));
    }

    [Fact]
    public void Reload_AlsoDropsKeysThatWentAwayOnDisk()
    {
        var window = AppSettings.Load(_path);
        window.SetString("title_bar_full_path", "1");
        window.Save(_path);

        File.WriteAllText(_path, "; everything reset by hand\n");

        window.Reload(_path);

        Assert.False(window.Has("title_bar_full_path"));
    }

    [Fact]
    public void Reload_ReplacesInPlaceSoHoldersSeeTheNewValues()
    {
        // 必须是就地替换：换新实例的话，已经把这份引用交出去的地方（主窗口、设置窗口）
        // 手里拿的还是旧的，重读就白做了。
        var window = AppSettings.Load(_path);
        var handedOut = window;

        File.WriteAllText(_path, "recent_files = C:\\a.zip\n");
        window.Reload(_path);

        Assert.Same(window, handedOut);
        Assert.Equal("C:\\a.zip", handedOut.GetString("recent_files"));
    }
}
