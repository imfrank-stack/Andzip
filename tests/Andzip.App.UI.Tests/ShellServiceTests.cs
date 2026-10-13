using System.Runtime.Versioning;
using Andzip.App.Services;
using Andzip.App.Views;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Andzip.App.UiTests;

/// <summary>
/// 删除与定位这两条走的是 Win32，不是我们自己的代码：结构体错位、参数拼错，编译器一个字都不会报，
/// 只在用户点下去那一刻静默失效 —— 或者更糟，把"还能反悔"的那一条悄悄变成不可反悔。
/// 所以这里真的调用一次，然后看文件系统的实际状态。
/// </summary>
[Collection("界面状态")]
public class ShellServiceTests
{
    private const string ProbeFileName = "andzip-recycle-probe.txt";

    /// <summary>
    /// 「删除压缩文件」的承诺是"还能反悔"，而那全押在 <c>FOF_ALLOWUNDO</c> 有没有真的生效上。
    /// 只断言"文件不见了"是不够的 —— 直接 <c>File.Delete</c> 也能让它不见。
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void RecycleBinDeleteLeavesTheFileRecoverable()
    {
        var root = Directory.CreateTempSubdirectory("andzip-recycle-");
        var probe = Path.Combine(root.FullName, ProbeFileName);
        File.WriteAllText(probe, "这是测试探针，不是用户的文件。");

        try
        {
            RecycleBin.Delete(probe);

            Assert.False(File.Exists(probe), "文件还在原地：删除根本没发生。");

            var entries = RecycledEntriesFor(probe);
            Assert.True(entries.Count > 0, "文件消失了，可回收站里没有它 —— 那是直接删掉，不是移到回收站。");

            foreach (var (data, metadata) in entries)
            {
                // 配对清理：只删数据文件会在用户的回收站里留下一条点不开的记录。
                File.Delete(data);
                File.Delete(metadata);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// 在系统盘的 <c>$Recycle.Bin</c> 里找某个原始路径对应的回收站条目，返回（数据文件，元数据文件）。
    /// <para>回收站把数据文件改名成 <c>$R</c> + 一个短名（长文件名留不住，这里的短名看着像随机串），
    /// 真正的原名与完整路径记在配对的 <c>$I</c> 里 —— 所以认"它确实进了回收站"只能去读 <c>$I</c>，
    /// 按 <c>$R</c> 的文件名去找会一无所获。</para>
    /// <para>每个账户一个子目录，别的账户读不动就跳过；探针是刚删的那个文件，只可能在当前账户下。</para>
    /// </summary>
    private static IReadOnlyList<(string Data, string Metadata)> RecycledEntriesFor(string originalPath)
    {
        var found = new List<(string, string)>();
        var recycleRoot = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "$Recycle.Bin");
        var needle = Path.GetFullPath(originalPath);

        foreach (var account in Directory.EnumerateDirectories(recycleRoot))
        {
            List<string> metadataFiles;
            try
            {
                metadataFiles = [.. Directory.GetFiles(account, "$I*", SearchOption.AllDirectories)];
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var metadata in metadataFiles)
            {
                // $I 的头是二进制（版本 / 大小 / 删除时间 / 路径长度），后面跟着 UTF-16 的原路径。
                // 不解析格式，直接把整段按 UTF-16 摊开找那条路径 —— 要问的只是"有没有它"。
                var text = System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(metadata));
                if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(metadata);
                    var data = Path.Combine(Path.GetDirectoryName(metadata)!, "$R" + name[2..]);
                    if (File.Exists(data))
                    {
                        found.Add((data, metadata));
                    }
                }
            }
        }

        return found;
    }

    /// <summary>路径不存在时返回 false，而不是开个空窗口再报告成功。</summary>
    [Fact]
    public void RevealRefusesAPathThatIsNotThere()
    {
        // 真定位会弹出资源管理器窗口，那不该在测试里发生，所以这里只走"东西不存在"这一条分支。
        var missing = Path.Combine(Path.GetTempPath(), "andzip-这个包不存在.zip");
        Assert.False(ArchiveReveal.Reveal(missing));
    }

    // 删除前的确认框：哪个按钮算"是"，是这条路径上唯一的安全网。

    [AvaloniaFact]
    public void ConfirmDialogAnswersOnlyTheButtonThatWasPressed()
    {
        var lang = TestLang.Chinese();
        var owner = new MainWindow(lang, new AppSettings()).Shown();

        try
        {
            Assert.True(PressInConfirmDialog(lang, owner, "TEXT_BTN_DELETE"), "点「删除」应该回 true");
            Assert.False(PressInConfirmDialog(lang, owner, "TEXT_BTN_CANCEL"), "点「取消」必须回 false");
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>弹一个两钮确认框，按下其中一个，返回对话框带回去的结果。</summary>
    private static bool PressInConfirmDialog(LangCatalog lang, Window owner, string buttonKey)
    {
        var dialog = new MessageWindow(
            lang,
            lang.Label("MENUSTR_DELETE_ARCHIVE"),
            lang.Get("TEXT_CONFIRM_DELETE_ARCHIVE"),
            "TEXT_BTN_DELETE",
            "TEXT_BTN_CANCEL");

        var answer = dialog.ShowDialog<bool>(owner);

        // 对话框是异步呈现的，先让 Dispatcher 跑一会儿，控件树才建得起来。
        UiWait.Pump(100);

        var wanted = lang.Label(buttonKey);
        var button = VisualQuery.Of<Button>(dialog).Single(b => b.Content?.ToString() == wanted);
        Clicker.Invoke(button);

        return UiWait.Until(answer, $"确认框按「{wanted}」");
    }
}
