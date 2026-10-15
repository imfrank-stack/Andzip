using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>
/// 口令穷举的判据与执行。
/// <para>这一组里最要紧的是 <see cref="WrongPasswordOnDataOnlySevenZipIsNeverAccepted"/>：
/// 只加密数据的 7z 用错口令也能<strong>正常打开头部、正常列出清单</strong>，
/// 解出来的还是一段合法的乱码。判据如果写成"能打开就算对"，它会当场报一个假口令给用户。</para>
/// </summary>
public sealed class PasswordRecoveryTests : IDisposable
{
    private const string Secret = "hunter2";

    private readonly string _root = Directory.CreateTempSubdirectory("andzip-recover-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Source(string stem)
    {
        var source = Path.Combine(_root, stem + "-src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "small.txt"), "十六个字符的长度刚好");
        File.WriteAllBytes(Path.Combine(source, "big.bin"), new byte[200_000]);
        return source;
    }

    private string Create(string stem, string format, bool encryptHeaders, string password = Secret)
    {
        var archive = Path.Combine(_root, stem + "." + format.ToLowerInvariant());
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(Source(stem))],
            new ArchiveCreationSettings(
                archive,
                CompressionLevelOption.Store,
                Format: ArchiveFormatOption.FromId(format),
                Password: password,
                EncryptHeaders: encryptHeaders));
        return archive;
    }

    [Fact]
    public void APlainArchiveIsRefusedBecauseThereIsNothingToRecover()
    {
        var plain = Path.Combine(_root, "plain.zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(Source("plain"))],
            new ArchiveCreationSettings(plain, CompressionLevelOption.Store, Format: ArchiveFormatOption.FromId("ZIP")));

        var plan = PasswordRecovery.Inspect(plain);

        Assert.Equal(PasswordRecoveryTarget.NotEncrypted, plan.Target);
        Assert.False(plan.CanSearch);
        // 不合格的目标上，任何口令都该返回 false —— 包括它真的"能打开"这件事。
        Assert.False(PasswordRecovery.Accepts(plain, string.Empty, plan));
    }

    [Fact]
    public void AFileThatIsNotAnArchiveAtAllIsRefusedRatherThanEnumerated()
    {
        var text = Path.Combine(_root, "notes.txt");
        File.WriteAllText(text, "这只是一个文本文件，不是压缩包。");

        Assert.Equal(PasswordRecoveryTarget.Unopenable, PasswordRecovery.Inspect(text).Target);
        Assert.Equal(PasswordRecoveryTarget.Unopenable, PasswordRecovery.Inspect(Path.Combine(_root, "missing.zip")).Target);

        // 用例名字里那句"而不是被枚举"得真的有人守着：不合格的计划交给 Run 必须当场拒，
        // 而不是撞一亿次同一堵墙之后回一句"这个范围里没有" —— 那句"没有"是假的。
        var (space, _) = PasswordSpace.TryCreate("0123456789", 1, 2);
        Assert.Throws<ArgumentException>(() => PasswordRecovery.Run(
            text,
            space!,
            new PasswordRecoveryPlan(PasswordRecoveryTarget.Unopenable, PasswordProbeKind.OpensHeader, -1, 0)));
    }

    /// <summary>
    /// 大到数不出精确总数的范围<strong>一颗都不试</strong>。
    /// <para>这一条守的是界面上那颗「开始」为什么是灰的：连总数都数不出来的循环既给不出进度、
    /// 也给不出"还要多久"，而这一屏的全部承诺就是开跑之前把时长说实话。
    /// 开一根永远走不完的进度条不是"信息少一点"，是一个让人一直等下去的假象。</para>
    /// </summary>
    [Fact]
    public void ARangeTooBigToCountIsNotEnumeratedAtAll()
    {
        var archive = Create("huge", "ZIP", encryptHeaders: false);
        var plan = PasswordRecovery.Inspect(archive);
        Assert.True(plan.CanSearch, "这个包本身是合格的目标 —— 该拒的是范围，不是包");

        // 94 个可打印字符的 12 位：5e23，装不进 64 位。
        var printable = new string(Enumerable.Range('!', 94).Select(i => (char)i).ToArray());
        var (space, error) = PasswordSpace.TryCreate(printable, 12, 12);
        Assert.Null(error);
        Assert.True(space!.IsBeyondReach);

        var refused = Assert.Throws<ArgumentException>(() => PasswordRecovery.Run(archive, space, plan));
        Assert.Contains("精确", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEncryptedZipIsSearchableAndTheProbeIsTheSmallestEncryptedMember()
    {
        var archive = Create("zip", "ZIP", encryptHeaders: false);
        var plan = PasswordRecovery.Inspect(archive);

        Assert.Equal(PasswordRecoveryTarget.Searchable, plan.Target);
        Assert.Equal(PasswordProbeKind.ReadsEntry, plan.Probe);
        Assert.True(plan.ProbeEntryIndex >= 0);
        Assert.True(plan.ProbeEntryBytes > 0);

        // 探针挑的是最小的那一格：判据一分不损失，一次尝试却要读得最少。
        var encrypted = ArchiveReader.Read(archive).Entries
            .Where(e => e.IsEncrypted && !e.IsDirectory)
            .ToArray();
        Assert.NotEmpty(encrypted);
        Assert.Equal(encrypted.Min(e => e.Size), plan.ProbeEntryBytes);
    }

    [Fact]
    public void RightPasswordIsAcceptedAndWrongOnesAreNot()
    {
        var archive = Create("both", "ZIP", encryptHeaders: false);
        var plan = PasswordRecovery.Inspect(archive);

        Assert.True(PasswordRecovery.Accepts(archive, Secret, plan));
        Assert.False(PasswordRecovery.Accepts(archive, "hunter3", plan));
        Assert.False(PasswordRecovery.Accepts(archive, string.Empty, plan));
        Assert.False(PasswordRecovery.Accepts(archive, "hunter", plan), "短的前缀不算命中");
    }

    /// <summary>
    /// 这一条是整套判据的试金石：只加密数据的 7z，<strong>错口令也能打开、也能列清单</strong>，
    /// 解出来还是一段合法的乱码。只有整块读完让引擎对完 CRC 才会失败。
    /// 把 <c>Accepts</c> 里那次 <c>reader.Test(...)</c> 删掉，这条立刻红。
    /// </summary>
    [Fact]
    public void WrongPasswordOnDataOnlySevenZipIsNeverAccepted()
    {
        var archive = Create("data7z", "7Z", encryptHeaders: false);
        var plan = PasswordRecovery.Inspect(archive);

        Assert.Equal(PasswordRecoveryTarget.Searchable, plan.Target);
        Assert.Equal(PasswordProbeKind.ReadsEntry, plan.Probe);

        // 错口令下"打开 + 列清单"这两步都会成功 —— 这正是判据必须往下读一格的理由。
        var listed = ArchiveReader.Read(archive, "totally-wrong");
        Assert.NotEmpty(listed.Entries);

        Assert.True(PasswordRecovery.Accepts(archive, Secret, plan));
        Assert.False(PasswordRecovery.Accepts(archive, "totally-wrong", plan));
    }

    [Fact]
    public void HeaderEncryptedSevenZipIsSearchableByOpeningAlone()
    {
        var archive = Create("head7z", "7Z", encryptHeaders: true);
        var plan = PasswordRecovery.Inspect(archive);

        Assert.Equal(PasswordRecoveryTarget.Searchable, plan.Target);
        Assert.Equal(PasswordProbeKind.OpensHeader, plan.Probe);
        Assert.True(PasswordRecovery.Accepts(archive, Secret, plan));
        Assert.False(PasswordRecovery.Accepts(archive, "nope", plan));
    }

    [Fact]
    public void RunFindsThePasswordAndReportsHowManyAttemptsItTook()
    {
        var archive = Create("pin", "ZIP", encryptHeaders: false, password: "42");
        var plan = PasswordRecovery.Inspect(archive);
        var (space, error) = PasswordSpace.TryCreate("0123456789", 1, 2);
        Assert.Null(error);

        var result = PasswordRecovery.Run(archive, space!, plan);

        Assert.Equal(PasswordRecoveryOutcome.Found, result.Outcome);
        Assert.Equal("42", result.Password);
        // 一位数 10 个在前，"42" 是第 53 个候选 —— 这个数就是界面上"试了多少个"的凭据。
        Assert.Equal(53UL, result.Attempts);
    }

    [Fact]
    public void ExhaustedAndCancelledAreTwoDifferentSentences()
    {
        var archive = Create("absent", "ZIP", encryptHeaders: false, password: "qqqzzz");
        var plan = PasswordRecovery.Inspect(archive);
        var (space, _) = PasswordSpace.TryCreate("abc", 1, 2);

        var done = PasswordRecovery.Run(archive, space!, plan);
        Assert.Equal(PasswordRecoveryOutcome.Exhausted, done.Outcome);
        Assert.Null(done.Password);
        Assert.Equal(3 + 9UL, done.Attempts);
        Assert.True(done.Elapsed >= TimeSpan.Zero);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stopped = PasswordRecovery.Run(archive, space!, plan, cancellationToken: cts.Token);
        Assert.True(stopped.Outcome == PasswordRecoveryOutcome.Cancelled,
            "按了停止却说\"这个空间里没有\" —— 那是把用户的中途放弃报成一个结论。");
        Assert.Equal(0UL, stopped.Attempts);
    }

    [Fact]
    public void ProgressCarriesTheMeasuredRateAndAnEstimate()
    {
        var archive = Create("progress", "ZIP", encryptHeaders: false, password: "zz");
        var plan = PasswordRecovery.Inspect(archive);
        var (space, _) = PasswordSpace.TryCreate("abc", 1, 3);

        var reports = new List<PasswordRecoveryProgress>();
        PasswordRecovery.Run(archive, space!, plan, new CollectingProgress(reports));

        // 每 250ms 才报一次，小空间可能一次都不报；报了就必须是自洽的。
        foreach (var report in reports)
        {
            Assert.InRange(report.Fraction, 0d, 1d);
            Assert.True(report.Attempts <= report.Total);
            if (report.AttemptsPerSecond > 0)
            {
                Assert.NotNull(report.EstimatedRemaining);
            }
        }
    }

    /// <summary>Core 收到的进度回调必须是调用方自己造的 sink；测试里就是一个 List。</summary>
    private sealed class CollectingProgress(List<PasswordRecoveryProgress> sink) : IProgress<PasswordRecoveryProgress>
    {
        public void Report(PasswordRecoveryProgress value) => sink.Add(value);
    }
}
