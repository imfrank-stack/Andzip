using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.Core.Archive;
using Andzip.Core.Settings;
using Xunit;

namespace Andzip.App.UiTests;

/// <summary>
/// 真的写进 Windows 凭据管理器、再读回来。
/// <para>这一组不能拿假存储代替：<c>CREDENTIALW</c> 的字段顺序、blob 的字节数、
/// <c>CredEnumerate</c> 的前缀过滤、512 字节上限，全都是"只有真 API 会说谎不了"的东西 ——
/// 记错一个字段偏移，编译与单测都不会响，只有第一次真写的时候才会发现存进去的是错位的数据。</para>
/// <para>每个用例用自己的一次性路径（临时目录 + 一个 GUID），所以既不会撞上这台机器上
/// 真实存在过的条目，也不依赖上一次运行留下什么。<see cref="Dispose"/> 负责把写进去的删干净 ——
/// 测试在开发机上跑一次就在凭据管理器里多一条，那是把用户的系统当日志盘。</para>
/// <para><strong>这里不调 <c>Clear()</c></strong>：它按前缀删掉本程序存的<strong>全部</strong>凭据，
/// 而那在真存储上是一次真实的破坏性操作（万一这台机器上真有用户存过的口令）。
/// "关掉开关要一个不剩"那条语义由 <c>PasswordVaultTests</c> 在假存储上钉住。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialVaultTests : IDisposable
{
    private const string Secret = "hunter2-别写进设置文件";

    private readonly string _root = Directory.CreateTempSubdirectory("andzip-vault-").FullName;
    private readonly string _settingsPath;
    private readonly List<string> _created = [];

    public CredentialVaultTests()
    {
        _settingsPath = Path.Combine(_root, "settings.ini");
    }

    public void Dispose()
    {
        var vault = new CredentialVault(_settingsPath);
        foreach (var archive in _created)
        {
            vault.Remove(archive);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Archive(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "not really an archive, only a key");
        _created.Add(path);
        return path;
    }

    [Fact]
    public void APasswordStoredByOneInstanceIsReadBackByAFreshOne()
    {
        var archive = Archive("box.zip");

        Assert.Null(new CredentialVault(_settingsPath).Save(archive, Secret));

        // 新实例 = 新进程。跨重启这件事的全部承诺就在这一次读上。
        var reopened = new CredentialVault(_settingsPath);
        Assert.True(reopened.TryGet(archive, out var password));
        Assert.Equal(Secret, password);

        var listed = reopened.List();
        Assert.Equal([archive], listed.Select(e => e.ArchivePath).ToArray());
        Assert.True(listed[0].SavedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    /// <summary>
    /// 口令绝不进设置文件 —— 而路径<strong>要</strong>进（那一屏得显示是哪个包）。
    /// 这两句一起断言，才不会有一条是空断言。
    /// </summary>
    [Fact]
    public void TheSettingsFileHoldsThePathAndNeverThePassword()
    {
        var archive = Archive("tax.zip");
        Assert.Null(new CredentialVault(_settingsPath).Save(archive, Secret));

        var raw = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
        Assert.Contains("password_vault_paths", raw, StringComparison.Ordinal);
        Assert.Contains(archive, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void DeletingOneLeavesTheOthersAlone()
    {
        var keep = Archive("keep.zip");
        var gone = Archive("gone.zip");
        var vault = new CredentialVault(_settingsPath);
        vault.Save(keep, Secret);
        vault.Save(gone, "another-one");

        vault.Remove(gone);

        Assert.False(vault.TryGet(gone, out _));
        Assert.True(vault.TryGet(keep, out var still));
        Assert.Equal(Secret, still);
        Assert.Equal([keep], vault.List().Select(e => e.ArchivePath).ToArray());
    }

    /// <summary>分号在 Windows 文件名里合法，所以它不能把一条索引劈成两条。</summary>
    [Fact]
    public void AnArchiveNamedWithASemicolonIsOneEntryNotTwo()
    {
        var archive = Archive("a;b.zip");

        Assert.Null(new CredentialVault(_settingsPath).Save(archive, Secret));

        var vault = new CredentialVault(_settingsPath);
        Assert.Single(vault.List());
        Assert.True(vault.TryGet(archive, out var password));
        Assert.Equal(Secret, password);
    }

    /// <summary>
    /// <c>Clear()</c> 与「全部删除」挑"哪些是本程序存的"，靠的全是这一步枚举。
    /// <para>它坏掉的形状不是报错，是<strong>返回空</strong> —— 于是那颗按钮什么都没删，
    /// 界面却照样说"已经清空"，而这一条语义在假存储上永远测不出来（假存储的枚举是自己写的，
    /// 想返回什么返回什么）。所以这里单独钉一次，且不真调用 <c>Clear()</c>。</para>
    /// </summary>
    [Fact]
    public void TheEnumerationActuallyFindsWhatThisProgramStored()
    {
        var archive = Archive("seen-by-enumeration.zip");
        Assert.Null(new CredentialVault(_settingsPath).Save(archive, Secret));

        var target = PasswordVaultKey.For(archive);
        Assert.Contains(target, CredentialVault.EnumerateTargets());
    }

    [Fact]
    public void APasswordTooLongForOneCredentialIsRefusedAndNothingIsStored()
    {
        var archive = Archive("long.zip");
        var vault = new CredentialVault(_settingsPath);
        var tooLong = new string('密', 300); // UTF-8 900 字节 > 单条 512

        Assert.NotNull(vault.Rejects(tooLong));
        Assert.NotNull(vault.Save(archive, tooLong));

        Assert.False(vault.TryGet(archive, out _));
        Assert.Empty(vault.List());
        Assert.Null(vault.Rejects("short-enough"));
    }

    /// <summary>
    /// 同一个包换个写法（大小写不同）必须是同一条 —— 否则"删掉一条"删不干净，
    /// 而界面上看着已经清空了。
    /// </summary>
    [Fact]
    public void TheSameArchiveWrittenDifferentlyIsTheSameCredential()
    {
        var archive = Archive("Case.zip");
        var vault = new CredentialVault(_settingsPath);
        vault.Save(archive, Secret);

        Assert.True(vault.TryGet(archive.ToUpperInvariant(), out var password));
        Assert.Equal(Secret, password);

        vault.Remove(archive.ToLowerInvariant());
        Assert.False(vault.TryGet(archive, out _));
    }
}
