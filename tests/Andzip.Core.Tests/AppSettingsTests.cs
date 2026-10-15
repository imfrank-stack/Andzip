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

    /// <summary>
    /// 一个实例保存时，不许抹掉另一个实例刚写进去的键。
    /// <para>这台机器上同时开着几个 Andzip 是常态 —— 每次双击都起一个新进程，而每个进程手里
    /// 是它<strong>启动那一刻</strong>读到的整份快照。用户报的「双击直接解压怎么设都没用」
    /// 就是这条：在 B 窗口里选了「直接解压」并落盘，回到已经开着的 A 窗口打开一个包，
    /// A 记一笔最近文件、把它那份<strong>没见过这个键</strong>的快照整个写回去 —— 键消失了，
    /// 下一次双击又变回打开主窗口。</para>
    /// <para>所以 <c>Save</c> 必须是"只写我改过的那几键"，不是"把我记得的全部重写一遍"。</para>
    /// </summary>
    [Fact]
    public void SaveFromAnOlderSnapshotKeepsKeysAnotherInstanceWrote()
    {
        var opened = AppSettings.Load(_path);
        opened.SetString(SettingKeys.RecentFiles, "a.zip");
        opened.Save(_path);

        // 另一个实例（设置窗口）写入双击这一项；上面那份快照没见过它
        var dialog = AppSettings.Load(_path);
        dialog.SetString(SettingKeys.DoubleClickAction, "extract");
        dialog.Save(_path);

        // 老实例只是记了一笔最近文件 —— 这一次写盘不该把上面那条抹掉
        opened.SetString(SettingKeys.RecentFiles, "a.zip;b.zip");
        opened.Save(_path);

        var onDisk = AppSettings.Load(_path);
        Assert.Equal("extract", onDisk.GetString(SettingKeys.DoubleClickAction));
        Assert.Equal("a.zip;b.zip", onDisk.GetString(SettingKeys.RecentFiles));
    }

    /// <summary>
    /// 反过来也要成立：显式删掉的键，合并写回时不能又从磁盘上长回来。
    /// 「设置 → 安全」关掉保存口令时要删 <c>password_vault_paths</c> 那一行，
    /// 只删内存不删磁盘等于开关关了、清单还在。
    /// </summary>
    [Fact]
    public void SaveAppliesAnExplicitRemovalToWhatIsOnDisk()
    {
        var first = AppSettings.Load(_path);
        first.SetString(SettingKeys.RecentFiles, "a.zip");
        first.SetString("password_vault_paths", @"C:\a.zip|C:\b.zip");
        first.Save(_path);

        var second = AppSettings.Load(_path);
        second.SetString("password_vault_paths", null);
        second.Save(_path);

        var onDisk = AppSettings.Load(_path);
        Assert.False(onDisk.Has("password_vault_paths"));
        Assert.Equal("a.zip", onDisk.GetString(SettingKeys.RecentFiles));
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

    // ---------- 两份实例并存：各写各的键，谁也不许抹掉谁 ----------

    /// <summary>
    /// 主窗口与设置窗口（以及另一个双击起来的进程）各持一份实例。
    /// <para>3.1.1 之前写盘是"整份快照盖过去"，于是老快照会把别人刚写的键抹掉，
    /// 用户看到的就是「自动展开树里的文件夹」（0.13.0）与「双击直接解压」（3.1.1）
    /// 这两次"设了没用"。现在 <c>Save</c> 只写自己改过的键，这一组用例钉的就是这两半：
    /// 没碰过的键必须活着，碰过的键按最后落盘的算。</para>
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

    /// <summary>
    /// 同一个键被两个实例改过时：后落盘的那一份赢 —— 这是没有锁的键值文件的边界。
    /// <para>3.1.1 起 <c>Save</c> 只写自己改过的键，"谁没碰过的键都不该消失"由
    /// <see cref="SaveFromAnOlderSnapshotKeepsKeysAnotherInstanceWrote"/> 钉住；这一条钉的是另一半：
    /// 真的改了同一个键，仍然按最后写的算。想让自己写的值不被别人盖掉，办法还是先
    /// <c>Reload</c> 再改（0.13.0 那条老规矩，它从"必须"降级成"想赢就得重读"）。</para>
    /// </summary>
    [Fact]
    public void TwoInstancesWritingTheSameKey_LastWriterWins()
    {
        var window = AppSettings.Load(_path);
        window.SetString("language", "SimpChinese");
        window.Save(_path);

        var dialog = AppSettings.Load(_path);
        dialog.SetString("language", "English");
        dialog.Save(_path);

        // 窗口手里那份还是 SimpChinese，但它没改过这个键 —— 再写一次不该动磁盘。
        window.Save(_path);
        Assert.Equal("English", AppSettings.Load(_path).GetString("language"));

        // 它真的改了这个键，那就是后写的赢。
        window.SetString("language", "SimpChinese");
        window.Save(_path);
        Assert.Equal("SimpChinese", AppSettings.Load(_path).GetString("language"));

        // 先重读再改，两边都不丢：别人写的键保住了，窗口自己要记的那一项也记上了。
        window.Reload(_path);
        window.SetString("recent_files", @"C:\a.zip");
        window.Save(_path);

        var final = AppSettings.Load(_path);
        Assert.Equal("SimpChinese", final.GetString("language"));
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
