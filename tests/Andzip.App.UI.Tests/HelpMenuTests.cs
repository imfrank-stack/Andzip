using System;
using System.Linq;
using Andzip.App;
using Andzip.App.Views;
using Andzip.Core;
using Andzip.Core.Archive;
using Andzip.Core.Menu;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Andzip.App.UiTests;

/// <summary>
/// 「帮助」那一排与关于窗口。
/// <para>要钉住的是两类事：菜单上印出来的承诺（哪几条是活的、哪一条已经删掉）与
/// 关于那一屏报的数是不是<strong>现场读的</strong>。版本号与内核状态如果写成字面量，
/// 下一版它就会指着用户说假话 —— 而这句话恰恰是用户来核对"我装的是哪一版"时唯一能看的。</para>
/// </summary>
[Collection("界面状态")]
public class HelpMenuTests
{
    private static void Close(Window window)
    {
        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// 界面上真的看得见这三条活的、论坛那条仍然诚实地区着，而「转到帮助页面」整条不在了。
    /// <para>读的是<strong>真菜单</strong>不是模型：模型改对了而 <c>MainMenuBar</c> 把它过滤掉，
    /// 用户看到的还是旧的那一排。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheHelpMenuShowsWhatIsActuallyWired()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();

        try
        {
            var items = MenuWalk.All(Assert.Single(VisualQuery.Of<Menu>(window))).ToList();

            string Visible(string key) => lang.Get(key).Replace("&", string.Empty, StringComparison.Ordinal);

            foreach (var key in new[] { "MENUSTR_ABOUT", "MENUSTR_OPEN_FOLDER", "MENUSTR_HOMEPAGE" })
            {
                var item = Assert.Single(items, i => Equals(i.Header?.ToString(), Visible(key)));
                Assert.True(item.IsEnabled, $"「{Visible(key)}」在模型里已启用，界面上却是灰的");
            }

            // 「转到用户论坛」还没有地址。没有地址就还没有这一条 —— 拿官网凑一个是最容易犯、
            // 也最难被发现的假：点了确实有浏览器弹出来，只是去的不是它说的那个地方。
            var forum = Assert.Single(items, i => Equals(i.Header?.ToString(), Visible("MENUSTR_FORUM")));
            Assert.False(forum.IsEnabled, "还没有论坛地址，这条必须灰着");

            Assert.DoesNotContain(items, i => Equals(i.Header?.ToString(), Visible("MENUSTR_HELPPAGE")));
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// F1 真的开得出关于窗口，而且同一时刻只有一扇。
    /// <para>走的是 <c>KeyBindings</c> 里那一条命令本身 —— 菜单上印着 F1 而按下去开不出窗口，
    /// 是这一档最容易留下又最难看出来的空承诺。</para>
    /// </summary>
    [AvaloniaFact]
    public void F1OpensTheAboutBoxAndNeverOpensASecondOne()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();

        try
        {
            var binding = Assert.Single(window.KeyBindings.OfType<KeyBinding>(),
                b => Equals(b.Gesture, new KeyGesture(Key.F1)));

            binding.Command!.Execute(null);
            UiWait.Pump(60);
            Assert.Single(window.OwnedWindows.OfType<AboutWindow>());

            binding.Command.Execute(null);
            UiWait.Pump(60);
            // 注意别写成 Assert.Single(集合, "一句话") —— 那个重载把第二参数当"期望等于它的项"，
            // 于是"集合里有一扇"也会报"一个都不匹配"。
            var open = window.OwnedWindows.OfType<AboutWindow>().Count();
            Assert.True(open == 1,
                $"再按一次 F1 应当把那扇唤到前面，而不是叠出第二扇一模一样的关于（现在 {open} 扇）");
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 关于那一屏报的版本与内核状态是<strong>现场读的</strong>，不是抄来的字面量。
    /// <para>引擎没加载时标题栏会写 "compression engine unavailable" —— 关于窗口必须说同一句，
    /// 而不是永远报"7-Zip 内核已就绪"那种好话。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheAboutBoxRepeatsWhatTheAppReallyKnows()
    {
        var facts = AboutWindow.Facts(TestLang.Chinese()).ToDictionary(f => f.LabelKey, f => f.Value);

        var expected = typeof(AboutWindow).Assembly.GetName().Version!;
        Assert.Equal($"v{expected.Major}.{expected.Minor}.{expected.Build}", facts["TEXT_ABOUT_VERSION"]);

        Assert.Equal(ArchiveEngine.Describe(), facts["TEXT_ABOUT_ENGINE"]);

        // 许可证这一句不是装饰：这个 exe 里合并了 7-Zip 与几个编码器。
        Assert.Contains("LGPL", facts["TEXT_ABOUT_LICENSE"], StringComparison.Ordinal);
        Assert.Contains("unRAR", facts["TEXT_ABOUT_LICENSE"], StringComparison.Ordinal);
    }

    /// <summary>
    /// "安装目录"那一行指的是<strong>这个正在运行的程序自己的目录</strong>。
    /// <para>一台机器上可以同时存在"给自己"与"给所有用户"两份，绿色版更是根本没有安装这一步；
    /// 写死任何一个路径都会有一半天是错的。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheAboutBoxPointsAtTheFolderItIsReallyRunningFrom()
    {
        var directory = AboutWindow.AppDirectory();

        Assert.NotNull(directory);
        Assert.True(System.IO.Directory.Exists(directory!), "关于窗口报了一个不存在的目录");
        Assert.Equal(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Environment.ProcessPath!)), directory);
    }

    /// <summary>
    /// 官网地址只有一个来源，而且就是项目真正住的那个地址。
    /// <para>这一条断言看着像在挑字面量，其实挑的是"有没有第二份"：菜单、关于窗口、文档
    /// 都读 <see cref="AndzipLinks.Homepage" />，改错了地方会当场红。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheHomepageAddressIsTheProjectsOwnAndAppearsOnScreen()
    {
        Assert.Equal("https://github.com/imfrank-stack/Andzip", AndzipLinks.Homepage);

        var lang = TestLang.Chinese();
        var window = new AboutWindow(lang).Shown();
        try
        {
            // 「关于」那一屏上印着这个地址：用户不看菜单也能知道去哪儿说问题。
            Assert.Contains(VisualQuery.VisibleTexts(window),
                text => text.Contains(AndzipLinks.Homepage, StringComparison.Ordinal));
        }
        finally
        {
            Close(window);
        }
    }
}
