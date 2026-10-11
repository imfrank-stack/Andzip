using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 会话内的口令记忆与临时目录解析。
/// <para>
/// 这两条都是"看起来很小、错了很难看"的东西：口令记错了对象会把 A 包的口令拿去开 B 包；
/// 临时目录解析错了会让一个本来能打开的分卷包打不开。
/// </para>
/// </summary>
public class PasswordCacheTests
{
    [Fact]
    public void RememberedPasswordIsReturnedForTheSameArchive()
    {
        var cache = new PasswordCache();
        var archive = Path.Combine(Path.GetTempPath(), "az-pw", "secret.7z");

        cache.Remember(archive, "hunter2");

        Assert.True(cache.TryGet(archive, out var password));
        Assert.Equal("hunter2", password);
    }

    [Fact]
    public void LookupIgnoresPathCasing()
    {
        // Windows 文件系统不区分大小写。区分了会对同一个包问两遍口令 —— 用户视角就是"记了个寂寞"。
        var cache = new PasswordCache();
        var archive = Path.Combine(Path.GetTempPath(), "az-pw", "secret.7z");

        cache.Remember(archive, "hunter2");

        Assert.True(cache.TryGet(archive.ToUpperInvariant(), out var password));
        Assert.Equal("hunter2", password);
    }

    [Fact]
    public void EmptyPasswordIsNotRemembered()
    {
        // 空口令不是"这个包的口令是空"，而是"用户没填"。记下来会让下次直接跳过询问然后失败。
        var cache = new PasswordCache();
        var archive = Path.Combine(Path.GetTempPath(), "az-pw", "empty.7z");

        cache.Remember(archive, string.Empty);

        Assert.False(cache.TryGet(archive, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void DisabledCacheRemembersNothingAndMatchesNothing()
    {
        var cache = new PasswordCache { Enabled = false };
        var archive = Path.Combine(Path.GetTempPath(), "az-pw", "off.7z");

        cache.Remember(archive, "hunter2");

        Assert.False(cache.TryGet(archive, out _));
    }

    [Fact]
    public void TurningTheFeatureOffHidesWhatWasAlreadyRemembered()
    {
        // 设置页关掉这一项时会同时 Clear()，但即便调用方忘了清，Enabled=false 也必须立刻不再命中。
        var cache = new PasswordCache();
        var archive = Path.Combine(Path.GetTempPath(), "az-pw", "was.7z");
        cache.Remember(archive, "hunter2");

        cache.Enabled = false;

        Assert.False(cache.TryGet(archive, out _));
    }

    [Fact]
    public void ForgetAndClearShrinkTheCount()
    {
        var cache = new PasswordCache();
        var directory = Path.GetTempPath();
        cache.Remember(Path.Combine(directory, "a.7z"), "1");
        cache.Remember(Path.Combine(directory, "b.7z"), "2");

        cache.Forget(Path.Combine(directory, "A.7Z"));
        Assert.Equal(1, cache.Count);

        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ResolveTempDirectoryFallsBackToSystemTempWhenUnset()
    {
        ArchiveEngine.TempDirectory = null;

        Assert.Equal(Path.GetTempPath(), ArchiveEngine.ResolveTempDirectory());
    }

    [Fact]
    public void ResolveTempDirectoryUsesAnExistingDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("az-temp-").FullName;
        ArchiveEngine.TempDirectory = directory;

        try
        {
            Assert.Equal(directory, ArchiveEngine.ResolveTempDirectory());
        }
        finally
        {
            ArchiveEngine.TempDirectory = null;
        }
    }

    [Fact]
    public void ResolveTempDirectoryRefusesToFailTheArchive()
    {
        // 一个不可能存在的路径（段名里带非法字符）不能让"打开分卷包"整个失败：
        // 退回系统临时目录，用户顶多不知道文件落在哪儿，而不是包压根打不开。
        ArchiveEngine.TempDirectory = Path.Combine(Path.GetTempPath(), "az-nope<>|", "deeper");

        try
        {
            Assert.Equal(Path.GetTempPath(), ArchiveEngine.ResolveTempDirectory());
        }
        finally
        {
            ArchiveEngine.TempDirectory = null;
        }
    }
}
