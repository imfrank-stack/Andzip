using Andzip.Core.Archive;
using Andzip.Core.Settings;

namespace Andzip.Core.Tests;

/// <summary>
/// 口令的第二层（跨重启）：键怎么算、索引怎么存、双层缓存怎么配合。
/// <para>这一组全部跑在 <see cref="FakeStore"/> 上 —— 真凭据存储那一侧的往返在
/// <c>Andzip.App.UI.Tests</c> 的 <c>CredentialVaultTests</c> 里（那才是真的写进 Windows）。
/// 分开的理由：这里要断言的是<strong>规则</strong>（什么时候读、什么时候写、什么时候不许读），
/// 而那些规则拿真存储测既慢又会在开发机上留下东西。</para>
/// </summary>
public sealed class PasswordVaultTests
{
    /// <summary>
    /// 一个把口令记在字典里的假存储，同时数着被读了多少次 ——
    /// "命中之后提升到内存，这一次运行不再问第二遍"那条只能靠计数断言。
    /// </summary>
    private sealed class FakeStore : IPersistentPasswordStore
    {
        /// <summary>与真存储同一个字节上限，好让"太长存不下"那条分支在这里就能测。</summary>
        public const int MaxBytes = 512;

        public Dictionary<string, string> Backing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Reads;

        public bool Supported { get; set; } = true;

        public bool IsSupported => Supported;

        public string? Rejects(string password)
            => System.Text.Encoding.UTF8.GetByteCount(password) > MaxBytes
                ? "口令超过单条凭据的字节上限，没有保存。"
                : null;

        public string? Save(string archivePath, string password)
        {
            if (Rejects(password) is { } refused)
            {
                return refused;
            }

            Backing[PasswordVaultKey.Normalize(archivePath)] = password;
            return null;
        }

        public bool TryGet(string archivePath, out string password)
        {
            Reads++;
            return Backing.TryGetValue(PasswordVaultKey.Normalize(archivePath), out password!);
        }

        public void Remove(string archivePath) => Backing.Remove(PasswordVaultKey.Normalize(archivePath));

        public IReadOnlyList<PasswordVaultEntry> List()
            => [.. Backing.Keys.Select(k => new PasswordVaultEntry(k, DateTimeOffset.UnixEpoch))];

        public void Clear() => Backing.Clear();
    }

    private static string TempArchive(string name)
        => Path.Combine(Path.GetTempPath(), "andzip-vault-" + Guid.NewGuid().ToString("N")[..8], name);

    // ------------------------------------------------------- 键与索引

    [Fact]
    public void TheSameArchiveAlwaysMapsToOneKeyAndAnotherArchiveNeverDoes()
    {
        var path = TempArchive("box.7z");

        var key = PasswordVaultKey.For(path);
        Assert.StartsWith(PasswordVaultKey.Prefix, key, StringComparison.Ordinal);
        Assert.Equal(key, PasswordVaultKey.For(path.ToLowerInvariant()));
        Assert.Equal(key, PasswordVaultKey.For(path + Path.DirectorySeparatorChar));

        Assert.NotEqual(key, PasswordVaultKey.For(TempArchive("other.7z")));
    }

    /// <summary>
    /// 目标名里不许出现路径 —— 那是"这个包是加密的"这条元数据本身。
    /// </summary>
    [Fact]
    public void TheTargetNameLeaksNothingAboutTheFile()
    {
        var path = TempArchive("税单2025-机密.zip");
        var key = PasswordVaultKey.For(path);

        Assert.DoesNotContain("税单", key, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(path), key, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", key, StringComparison.Ordinal);
        Assert.True(PasswordVaultKey.BelongsToUs(key));
        Assert.False(PasswordVaultKey.BelongsToUs("OtherApp/target"));
    }

    [Fact]
    public void TheIndexStoresEveryPathOnceAndDropsArchivesThatAreGone()
    {
        var settings = new AppSettings();
        var first = TempArchive("a.zip");
        var second = TempArchive("b.zip");

        PasswordVaultIndex.Add(settings, first);
        PasswordVaultIndex.Add(settings, first.ToUpperInvariant());
        PasswordVaultIndex.Add(settings, second);
        Assert.Equal(2, PasswordVaultIndex.Load(settings).Count);

        Assert.Equal(2, PasswordVaultIndex.Prune(settings));
        Assert.Empty(PasswordVaultIndex.Load(settings));

        PasswordVaultIndex.Add(settings, first);
        PasswordVaultIndex.Add(settings, second);
        PasswordVaultIndex.Remove(settings, first);
        Assert.Equal([second], PasswordVaultIndex.Load(settings));
    }

    /// <summary>路径里带分号也不能把一条记录劈成两条 —— 那正是分隔符选 <c>|</c> 的理由。</summary>
    [Fact]
    public void APathContainingASemicolonSurvivesTheRoundTrip()
    {
        var settings = new AppSettings();
        var tricky = TempArchive("a;b.zip");

        PasswordVaultIndex.Add(settings, tricky);

        Assert.Equal([tricky], PasswordVaultIndex.Load(settings));
    }

    // ------------------------------------------------------- 双层缓存

    [Fact]
    public void APasswordTypedOnceIsReadFromTheStoreOnlyUntilItLandsInMemory()
    {
        var store = new FakeStore();
        var cache = new PasswordCache { Persistent = store, PersistPasswords = true };
        var path = TempArchive("box.zip");

        Assert.Null(cache.Remember(path, "hunter2"));
        Assert.Equal("hunter2", store.Backing[PasswordVaultKey.Normalize(path)]);

        cache.Clear();
        Assert.True(cache.TryGet(path, out var first));
        Assert.Equal("hunter2", first);
        Assert.Equal(1, store.Reads);

        // 第二次命中内存，绝不再问一遍系统。
        Assert.True(cache.TryGet(path, out _));
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public void TurningTheMasterSwitchOffReadsNothingNotEvenTheStoredOnes()
    {
        var store = new FakeStore();
        var path = TempArchive("box.zip");
        store.Backing[PasswordVaultKey.Normalize(path)] = "hunter2";

        var cache = new PasswordCache { Persistent = store, PersistPasswords = true, Enabled = false };

        Assert.False(cache.TryGet(path, out _));
        Assert.Equal(0, store.Reads);
        // 关掉的时候连写都不该发生 —— "别记我的口令"不能只挡内存那一层。
        // 比"值没被改掉"而不是比"集合为空"：上面那一条是这条用例自己预置的。
        Assert.Null(cache.Remember(path, "hunter3"));
        Assert.Equal("hunter2", store.Backing[PasswordVaultKey.Normalize(path)]);
        Assert.Single(store.Backing);
    }

    [Fact]
    public void PersistenceIsOptInAndNotWritingDoesNotBreakTheSessionMemory()
    {
        var store = new FakeStore();
        var cache = new PasswordCache { Persistent = store, PersistPasswords = false };
        var path = TempArchive("box.zip");

        Assert.Null(cache.Remember(path, "hunter2"));

        Assert.True(cache.TryGet(path, out var password));
        Assert.Equal("hunter2", password);
        Assert.Empty(store.Backing);
    }

    /// <summary>
    /// 存不下的时候必须回一句话。<strong>内存那份确实记上了</strong>，
    /// 所以"这次运行里记得、重启之后不会"这件事只有说出口户才知道 —— 静默吞掉就是让他
    /// 以为以后也不必再输。
    /// </summary>
    [Fact]
    public void APasswordTooLongForOneCredentialSaysSoInsteadOfFailingQuietly()
    {
        var store = new FakeStore();
        var cache = new PasswordCache { Persistent = store, PersistPasswords = true };
        var path = TempArchive("box.zip");
        var tooLong = new string('密', 300); // UTF-8 下 900 字节，超过单条 512

        var reason = cache.Remember(path, tooLong);

        Assert.NotNull(reason);
        Assert.True(cache.TryGet(path, out var inMemory));
        Assert.Equal(tooLong, inMemory);
        Assert.Empty(store.Backing);
    }

    [Fact]
    public void ForgettingAnArchiveClearsBothTiersButClearingThisRunLeavesTheStoreAlone()
    {
        var store = new FakeStore();
        var cache = new PasswordCache { Persistent = store, PersistPasswords = true };
        var path = TempArchive("box.zip");
        cache.Remember(path, "hunter2");

        // 「清除本次运行的口令」：用户清的是内存，凭据里那条是他故意留下的，
        // 所以清完之后再读同一个包仍然读得到（从凭据回来）。
        cache.Clear();
        Assert.Single(store.Backing);
        Assert.True(cache.TryGet(path, out var stillThere));
        Assert.Equal("hunter2", stillThere);

        // 「忘掉这个包」：两层都得清，否则下次开机它又回来了。
        cache.Forget(path);
        cache.Clear();
        Assert.False(cache.TryGet(path, out _));
        Assert.Empty(store.Backing);
    }

    [Fact]
    public void AStoreThatIsNotThereAtAllBehavesLikeTheOldMemoryOnlyCache()
    {
        var cache = new PasswordCache { Persistent = null, PersistPasswords = true };
        var path = TempArchive("box.zip");

        Assert.Null(cache.Remember(path, "hunter2"));
        Assert.True(cache.TryGet(path, out _));

        // 没有可用的凭据存储时，整件事退化成 2.9.0 之前的样子：只记内存，一句话都不多说。
        var unsupported = new PasswordCache { Persistent = new FakeStore { Supported = false }, PersistPasswords = true };
        Assert.Null(unsupported.Remember(path, "hunter2"));
        Assert.True(unsupported.TryGet(path, out _));
        Assert.Equal(0, ((FakeStore)unsupported.Persistent!).Reads);
    }
}
