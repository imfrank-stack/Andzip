using Andzip.Core.Settings;

namespace Andzip.Core.Tests;

/// <summary>
/// 「文件 → 最近的文件」这份列表的规则：顺序、去重、上限、以及"文件没了就别挂着"。
/// 全部对着一个真实的 settings.ini 往返，不测内存里的中间状态 —— 这份东西的价值就在
/// 重启之后还在。
/// </summary>
public class RecentFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-recent-").FullName;

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

    private string ExistingFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private AppSettings Fresh() => AppSettings.Load(Path.Combine(_root, "settings.ini"));

    private static AppSettings Saved(AppSettings settings, string path)
    {
        settings.Save(path);
        return AppSettings.Load(path);
    }

    [Fact]
    public void NewestRecordComesFirst()
    {
        var settings = Fresh();

        RecentFiles.Record(settings, ExistingFile("a.zip"));
        RecentFiles.Record(settings, ExistingFile("b.zip"));

        Assert.Equal(
            [Path.Combine(_root, "b.zip"), Path.Combine(_root, "a.zip")],
            RecentFiles.Load(settings));
    }

    [Fact]
    public void RecordingAnArchiveTwiceKeepsASingleEntry()
    {
        var settings = Fresh();
        var a = ExistingFile("a.zip");

        RecentFiles.Record(settings, a);
        RecentFiles.Record(settings, ExistingFile("b.zip"));
        RecentFiles.Record(settings, a);

        var loaded = RecentFiles.Load(settings);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(Path.GetFullPath(a), loaded[0]);
    }

    [Fact]
    public void SamePathWithADifferentCaseIsOneEntry()
    {
        var settings = Fresh();

        RecentFiles.Record(settings, ExistingFile("a.zip"));
        RecentFiles.Record(settings, Path.Combine(_root, "A.ZIP"));

        Assert.Single(RecentFiles.Load(settings));
    }

    [Fact]
    public void TheListIsCappedAtTheLimit()
    {
        var settings = Fresh();

        for (var i = 0; i < RecentFiles.Limit + 5; i++)
        {
            RecentFiles.Record(settings, ExistingFile($"box{i}.zip"));
        }

        var loaded = RecentFiles.Load(settings);
        Assert.Equal(RecentFiles.Limit, loaded.Count);
        Assert.Equal(Path.Combine(_root, $"box{RecentFiles.Limit + 4}.zip"), loaded[0]);
    }

    [Fact]
    public void PathsSurviveTheSettingsFileRoundTrip()
    {
        var settingsPath = Path.Combine(_root, "roundtrip.ini");
        var settings = AppSettings.Load(settingsPath);

        // 带空格、带中文、带分号 —— 分号是重点：AppSettings 的列表助手拿它当分隔符，
        // 而它在 Windows 文件名里合法，所以这份列表故意用了另一个分隔符。
        var tricky = ExistingFile("a;b 中文.zip");
        RecentFiles.Record(settings, tricky);
        RecentFiles.Record(settings, ExistingFile("plain.zip"));

        var reloaded = Saved(settings, settingsPath);

        // 最后记的那条在最前面。
        Assert.Equal(
            [Path.Combine(_root, "plain.zip"), Path.GetFullPath(tricky)],
            RecentFiles.Load(reloaded));
    }

    [Fact]
    public void PruneDropsEntriesWhoseFileIsGone()
    {
        var settingsPath = Path.Combine(_root, "prune.ini");
        var settings = AppSettings.Load(settingsPath);

        var doomed = ExistingFile("doomed.zip");
        RecentFiles.Record(settings, doomed);
        RecentFiles.Record(settings, ExistingFile("alive.zip"));
        settings.Save(settingsPath);

        File.Delete(doomed);

        var reloaded = AppSettings.Load(settingsPath);
        Assert.Equal(1, RecentFiles.Prune(reloaded));

        var alive = Assert.Single(RecentFiles.Load(reloaded));
        Assert.EndsWith("alive.zip", alive, StringComparison.Ordinal);

        // 再剪一次不该有变化，也不该把列表写空。
        Assert.Equal(0, RecentFiles.Prune(reloaded));
        Assert.Single(RecentFiles.Load(reloaded));
    }

    [Fact]
    public void ClearEmptiesTheListAndItStaysEmptyAcrossAReload()
    {
        var settingsPath = Path.Combine(_root, "clear.ini");
        var settings = AppSettings.Load(settingsPath);

        RecentFiles.Record(settings, ExistingFile("a.zip"));
        RecentFiles.Clear(settings);

        Assert.Empty(RecentFiles.Load(settings));

        settings.Save(settingsPath);
        Assert.Empty(RecentFiles.Load(AppSettings.Load(settingsPath)));
    }

    [Fact]
    public void AnEmptyListWritesNoKey()
    {
        var settingsPath = Path.Combine(_root, "empty.ini");
        var settings = AppSettings.Load(settingsPath);

        RecentFiles.Clear(settings);
        settings.Save(settingsPath);

        // 留一行 `recent_files =` 空值没有意义，还会让"设过 vs 没设过"分不出来。
        Assert.DoesNotContain(
            "recent_files",
            File.ReadAllText(settingsPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingAnEmptyPathIsRejectedRatherThanSilentlyStored()
    {
        Assert.Throws<ArgumentException>(() => RecentFiles.Record(Fresh(), "  "));
    }
}
