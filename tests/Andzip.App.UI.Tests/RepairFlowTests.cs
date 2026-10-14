using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Menu;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Andzip.App.UiTests;

/// <summary>
/// 「工具 → 修复压缩包」这一条命令的<strong>顺序</strong>：先量，再问。
/// <para>改这条的理由不是审美：以前它一上来就把三种修复模式摆出来，好包也要用户去猜该选哪个，
/// 猜错（选了模式三）就是白等一次全量重压。现在骨架对得上就一句话收工，对不上才问，
/// 而且把"扫到了什么"摆在模式上方 —— 选模式的依据是坏在哪，不是三种模式各是什么。</para>
/// <para>三条都<strong>走菜单那一下</strong>，并且<strong>把弹出来的对话框点掉</strong>：
/// 命令是 <c>async void</c>，测试不点掉它就永远停在 <c>await ShowDialog</c> 上，
/// 那个续接落在 teardown 之后会把测试主机整个打崩（实测：三条全部点不掉时，
/// 运行到第一条就"测试主机进程崩溃"）。</para>
/// </summary>
[Collection("界面状态")]
public class RepairFlowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-repair-flow-").FullName;

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

    [AvaloniaFact]
    public void AWholeArchiveGetsOneSentenceNotAModeDialog()
    {
        var lang = TestLang.Chinese();
        var window = OpenWith(lang, BuildZip("box.zip", "a.txt"));

        ClickRepair(window);

        var report = AwaitWindow<MessageWindow>(window, "好包该得到一句结论");
        var shown = string.Join("|", VisualQuery.VisibleTexts(report));

        Assert.Contains(lang.Label("TEXT_REPAIR_HEALTHY"), shown, StringComparison.Ordinal);
        Assert.False(HasWindow<RepairWindow>(window),
            "结构没问题却把三种模式摆出来了 —— 用户接下来只能靠猜");

        ClickButton(report, lang.Label("TEXT_BTN_OK"));
    }

    [AvaloniaFact]
    public void ADamagedArchiveIsAskedWithTheFindingsOnScreen()
    {
        var lang = TestLang.Chinese();
        var archive = BuildZip("broken.zip", "a.txt", "b.txt");
        var window = OpenWith(lang, archive);

        // 打开之后再把中央目录那段抹掉：窗口手上那份清单还是好的，而磁盘上的包已经坏了。
        // 命令读的是文件本身，所以这一刀测的正是"修之前有没有真去量一遍"。
        var map = ZipScanner.Inspect(archive);
        var bytes = File.ReadAllBytes(archive);
        Array.Clear(bytes, (int)map.CentralDirectoryOffset, (int)map.CentralDirectoryLength);
        File.WriteAllBytes(archive, bytes);

        ClickRepair(window);

        var dialog = AwaitWindow<RepairWindow>(window, "坏包该问用户选哪种修复");
        var shown = string.Join("|", VisualQuery.VisibleTexts(dialog));

        Assert.Contains(lang.Label("TEXT_REPAIR_FINDINGS"), shown, StringComparison.Ordinal);
        Assert.Contains("中央目录", shown, StringComparison.Ordinal);

        // 点取消，不是开始修复：这条要问的是"问不问、问得清不清楚"，真修一遍是 ZipRepairerTests 的活。
        ClickButton(dialog, lang.Label("TEXT_BTN_CANCEL"));
    }

    [AvaloniaFact]
    public void AFormatThatHasNoZipSkeletonIsSaidSoInsteadOfAsked()
    {
        var lang = TestLang.Chinese();
        var archive = Path.Combine(_root, "box.7z");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(WriteSource("a.txt"))],
            new ArchiveCreationSettings(archive, CompressionLevelOption.Store, Format: ArchiveFormatOption.FromId("7Z")));

        var window = OpenWith(lang, archive);

        ClickRepair(window);

        var report = AwaitWindow<MessageWindow>(window, "非 ZIP 容器该直接说明");
        var shown = string.Join("|", VisualQuery.VisibleTexts(report));

        Assert.Contains(lang.Label("TEXT_REPAIR_ZIP_ONLY"), shown, StringComparison.Ordinal);
        Assert.False(HasWindow<RepairWindow>(window),
            "7z 没有 ZIP 那套骨架，摆出三种模式等于摆出三个注定失败的选项");

        ClickButton(report, lang.Label("TEXT_BTN_OK"));
    }

    // -------------------------------------------- 测试 → 修复：一条路走完，不让人自己接

    /// <summary>
    /// 测出坏成员之后，"要不要修"就在同一屏问完。
    /// <para>这一条走的是用户那条线：点「测试」→ 结果框上有「尝试修复…」和「关闭」两颗 →
    /// 点前者 → 结构扫描说"骨架是完整的"，而<strong>那句"数据有没有坏要用测试"不许再出现</strong>
    /// —— 用户正是从那一步来的，再劝一次就是自相矛盾（<c>cameFromTest</c> 那一条分支）。</para>
    /// </summary>
    [AvaloniaFact]
    public void ABrokenZipAsksToRepairOnTheResultScreen()
    {
        var lang = TestLang.Chinese();
        var archive = BuildZip("box.zip", "a.txt", "b.txt");
        // 成员名就是输入文件的文件名，而 WriteSource 落的是 src-b.txt —— 认成员要按包里那个名字来。
        CorruptMemberData(archive, "src-b.txt");

        var window = OpenWith(lang, archive);
        ClickTest(window);

        var result = AwaitWindow<MessageWindow>(window, "坏包该给出结果框");
        var shown = string.Join("|", VisualQuery.VisibleTexts(result));

        Assert.Contains("src-b.txt", shown, StringComparison.Ordinal);
        Assert.Contains(lang.Format("TEXT_TEST_BROKEN_HEADER", ("bad", "1"), ("total", "2")), shown,
            StringComparison.Ordinal);
        AssertButton(result, lang.Label("TEXT_BTN_TRY_REPAIR"));
        AssertButton(result, lang.Label("TEXT_BTN_CLOSE"));

        ClickButton(result, lang.Label("TEXT_BTN_TRY_REPAIR"));

        var handoff = AwaitWindow<MessageWindow>(window, "点了尝试修复该接着量结构");
        var said = string.Join("|", VisualQuery.VisibleTexts(handoff));
        Assert.Contains(lang.Label("TEXT_REPAIR_HEALTHY_AFTER_TEST"), said, StringComparison.Ordinal);
        Assert.DoesNotContain("要用「测试」", said, StringComparison.Ordinal);

        ClickButton(handoff, lang.Label("TEXT_BTN_OK"));
    }

    [AvaloniaFact]
    public void AHealthyArchiveGetsOneButtonAndNoRepairOffer()
    {
        var lang = TestLang.Chinese();
        var window = OpenWith(lang, BuildZip("good.zip", "a.txt", "b.txt"));

        ClickTest(window);

        var result = AwaitWindow<MessageWindow>(window, "好包该得到一句通过");
        var shown = string.Join("|", VisualQuery.VisibleTexts(result));

        Assert.Contains(lang.Label("TEXT_TEST_PASSED"), shown, StringComparison.Ordinal);
        Assert.DoesNotContain(lang.Label("TEXT_BTN_TRY_REPAIR"), shown, StringComparison.Ordinal);
        AssertButton(result, lang.Label("TEXT_BTN_OK"));

        ClickButton(result, lang.Label("TEXT_BTN_OK"));
    }

    /// <summary>
    /// 坏的是 7z 时<strong>不给</strong>那颗按钮：修复做的是 ZIP 那套骨架的字节级重建，
    /// 7z 没有对应结构。摆一颗点了只会说"修不了"的按钮，比不摆更糟。
    /// </summary>
    [AvaloniaFact]
    public void ADamagedSevenZipIsNotOfferedARepairThatCannotRun()
    {
        var lang = TestLang.Chinese();
        var archive = Path.Combine(_root, "box.7z");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(WriteSource("a.txt"))],
            new ArchiveCreationSettings(archive, CompressionLevelOption.Store, Format: ArchiveFormatOption.FromId("7Z")));

        // 7z 没有"按名字找局部头"这条路（它的布局完全不同），所以按整包比例翻一个字节：
        // 这一条要问的只是"坏包 + 非 ZIP 会不会给按钮"，谁坏了不重要。
        var bytes = File.ReadAllBytes(archive);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);

        var window = OpenWith(lang, archive);
        ClickTest(window);

        var result = AwaitWindow<MessageWindow>(window, "坏掉的 7z 也该有结论");
        var shown = string.Join("|", VisualQuery.VisibleTexts(result));

        Assert.DoesNotContain(lang.Label("TEXT_BTN_TRY_REPAIR"), shown, StringComparison.Ordinal);
        AssertButton(result, lang.Label("TEXT_BTN_OK"));

        ClickButton(result, lang.Label("TEXT_BTN_OK"));
    }

    /// <summary>
    /// 名单的截断规则：只列前 6 个，剩下的用一句"另有 N 个没查完"说清。
    /// <para>这一屏是让人决定"要不要修"的，不是让人抄文件名的；而"没查完"必须写出来 ——
    /// 一份看起来完整其实断掉的名单，比一份短名单危险得多。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheBrokenListIsCutShortAndSaysSo()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();

        var report = new ArchiveTestReport(
            Members: 30,
            Verified: 20,
            Failures: [.. Enumerable.Range(0, 8).Select(i => new TestFailure($"bad{i}.bin", "CRC mismatch"))]);

        var shown = window.DescribeBroken(report);

        Assert.Contains("bad0.bin", shown, StringComparison.Ordinal);
        Assert.Contains("bad5.bin", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("bad6.bin", shown, StringComparison.Ordinal);
        Assert.Contains(lang.Format("TEXT_TEST_AND_MORE", ("n", "2")), shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// 修完必须当场读一遍。<strong>"收回 2 个成员"是修复器自己的说法</strong> —— 它按局部头把中央目录
    /// 重建出来了，不等于那个包真能解压。这一条走完整条线（抹掉中央目录 → 模式一 → 开始修复 →
    /// 结果框），并断言结果里带着引擎读回来的那句复查结论。
    /// </summary>
    [AvaloniaFact]
    public void TheRepairResultCarriesWhatTheEngineFoundWhenItReadTheOutputBack()
    {
        var lang = TestLang.Chinese();
        var archive = BuildZip("wiped.zip", "a.txt", "b.txt");

        var map = ZipScanner.Inspect(archive);
        var bytes = File.ReadAllBytes(archive);
        Array.Clear(bytes, (int)map.CentralDirectoryOffset, (int)map.CentralDirectoryLength);
        File.WriteAllBytes(archive, bytes);

        var window = OpenWith(lang, archive);
        ClickRepair(window);

        var dialog = AwaitWindow<RepairWindow>(window, "中央目录被抹掉该问模式");
        // 记下对话框当时给的那个名字再点开始：SuggestOutputPath 每次调用都会挑"下一个没被占用的"，
        // 事后再问一遍拿到的是另一个名字，那条存在性断言就成了假红。
        var output = dialog.OutputPath;
        ClickButton(dialog, lang.Label("TEXT_REPAIR_START"));

        var result = AwaitWindow<MessageWindow>(window, "修完该给结论");
        var shown = string.Join("|", VisualQuery.VisibleTexts(result));

        Assert.Contains(
            lang.Format("TEXT_REPAIR_RECHECK_PASSED", ("total", "2")), shown, StringComparison.Ordinal);
        Assert.True(File.Exists(output), $"结论说复查通过了，可那份新包不在盘上：{output}");

        ClickButton(result, lang.Label("TEXT_BTN_OK"));
    }

    // ------------------------------------------------------------------ 走一遍用户那条路

    private static void ClickTest(MainWindow window)
    {
        var menu = Assert.Single(VisualQuery.Of<Menu>(window));
        var item = MenuWalk.All(menu).Single(i => Equals(i.Tag as string, MainMenuModel.CommandTestArchive));

        Clicker.Invoke(item);
    }

    /// <summary>这颗按钮必须<strong>在屏幕上看得见</strong>——"我们支持修复"写在代码里不算。</summary>
    private static void AssertButton(Window dialog, string label)
        => Assert.Contains(label, VisualQuery.VisibleTexts(dialog), StringComparer.Ordinal);

    /// <summary>
    /// 翻掉指名那个成员数据区里的一个字节（ZIP 局部头 + 名字长度 + 扩展字段之后就是数据）。
    /// <para>靠名字认而不是数第几个 <c>PK\x03\x04</c>：随机数据里撞出这四个字节的概率不为零。
    /// 与 <c>ArchiveTesterTests</c> 里那份是同一个道理，两个测试工程之间没有共享 helper 的项目，
    /// 所以这里宁可留一份短的。</para>
    /// </summary>
    private static void CorruptMemberData(string archive, string entryName)
    {
        var bytes = File.ReadAllBytes(archive);
        var name = System.Text.Encoding.UTF8.GetBytes(entryName);
        var at = -1;

        while ((at = IndexOfSignature(bytes, [0x50, 0x4B, 0x03, 0x04], at + 1)) >= 0)
        {
            var nameLength = BitConverter.ToUInt16(bytes, at + 26);
            var extraLength = BitConverter.ToUInt16(bytes, at + 28);

            if (nameLength != name.Length || !bytes.AsSpan(at + 30, nameLength).SequenceEqual(name))
            {
                continue;
            }

            bytes[at + 30 + nameLength + extraLength + 4] ^= 0xFF;
            File.WriteAllBytes(archive, bytes);
            return;
        }

        throw new InvalidOperationException($"包里找不到成员「{entryName}」的局部头。");
    }

    private static int IndexOfSignature(byte[] haystack, byte[] needle, int from)
    {
        for (var i = Math.Max(0, from); i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    private MainWindow OpenWith(LangCatalog lang, string archive)
    {
        var window = new MainWindow(lang, new AppSettings()).Shown();
        UiWait.Until(window.OpenArchive(archive), "打开压缩包");
        return window;
    }

    private static void ClickRepair(MainWindow window)
    {
        var menu = Assert.Single(VisualQuery.Of<Menu>(window));
        var item = MenuWalk.All(menu).Single(i => Equals(i.Tag as string, MainMenuModel.CommandRepairArchive));

        Clicker.Invoke(item);
    }

    /// <summary>
    /// 等某个对话框<strong>出现在屏幕上</strong>（不是"被 new 出来"）。
    /// <para>判据是主窗口的 <c>OwnedWindows</c>：这些对话框都是 <c>ShowDialog(this)</c> 开出来的，
    /// 挂在主窗口名下。headless 没有桌面生命周期对象，翻 <c>ApplicationLifetime</c> 只会拿到空的
    /// —— 第一版就是这么写的，三条全部超时。</para>
    /// </summary>
    private static T AwaitWindow<T>(Window owner, string what) where T : Window
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            UiWait.Pump(50);
            if (owner.OwnedWindows.OfType<T>().FirstOrDefault() is { } found)
            {
                return found;
            }
        }

        var open = string.Join(" / ", owner.OwnedWindows.Select(w => w.GetType().Name));
        throw new TimeoutException($"{what}：没等到 {typeof(T).Name}，主窗口名下开着的是「{open}」");
    }

    private static bool HasWindow<T>(Window owner) where T : Window => owner.OwnedWindows.OfType<T>().Any();

    /// <summary>按用户看到的文案点掉一颗按钮，并等那个对话框真的关掉 —— 不等就等于把崩溃留给 teardown。</summary>
    private static void ClickButton(Window dialog, string label)
    {
        var owner = dialog.Owner as Window;   // OwnedWindows 在 Window 上，Owner 的静态类型是 WindowBase
        var button = VisualQuery.Of<Button>(dialog).SingleOrDefault(b => Equals(VisualQuery.TextOf(b), label));
        Assert.NotNull(button);

        Clicker.Invoke(button!);

        var deadline = Environment.TickCount64 + 3_000;
        while (owner is not null && owner.OwnedWindows.Contains(dialog) && Environment.TickCount64 < deadline)
        {
            UiWait.Pump(20);
        }
    }

    // ------------------------------------------------------------------ 造材料

    private string BuildZip(string name, params string[] entries)
    {
        var path = Path.Combine(_root, name);
        ArchiveCreator.Create(
            [.. entries.Select(entry => ArchiveInputItem.FromPath(WriteSource(entry)))],
            new ArchiveCreationSettings(path, CompressionLevelOption.Store, Format: ArchiveFormatOption.FromId("ZIP")));
        return path;
    }

    private string WriteSource(string name)
    {
        var path = Path.Combine(_root, "src-" + name.Replace('/', '_'));
        File.WriteAllText(path, $"{name} 的内容，够长到能被压。\n重复一遍：{name}\n");
        return path;
    }
}
