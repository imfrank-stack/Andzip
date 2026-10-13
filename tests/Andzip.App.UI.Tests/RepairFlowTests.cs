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

    // ------------------------------------------------------------------ 走一遍用户那条路

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
        var button = VisualQuery.Of<Button>(dialog).SingleOrDefault(b => Equals(b.Content?.ToString(), label));
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
