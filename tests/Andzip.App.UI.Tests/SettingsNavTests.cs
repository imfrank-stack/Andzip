using Andzip.App;
using Andzip.App.Views;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Andzip.App.UiTests;

/// <summary>
/// 设置窗口左侧那一列导航。用户两条反馈都指向"按下这一下"（开发记录第七节第 44 条）：
/// <list type="number">
/// <item>按下时整块变成 <c>#686868</c> 的深灰，压在浅底上像按坏了 —— 那是 SimpleTheme 的
/// Button 模板自己画的 <c>:pressed</c>，应用层给 <c>Background</c> 写的值压不过它。
/// 现在药丸画在 Content 里，按下那一档由 <c>Surface.Pressed</c> 说了算。</item>
/// <item>条目之间那 4px 是点不动的死区。现在行与行紧贴，命中测试打在分界上必须落进某一行。</item>
/// </list>
/// </summary>
[Collection("界面状态")]
public class SettingsNavTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-nav-").FullName;

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
    public void PressingANavRowShowsOurShadeNotTheThemeGray()
    {
        var lang = TestLang.Chinese();
        var window = new SettingsWindow(lang, new AppSettings(), Path.Combine(_root, "settings.ini")).Shown();
        var row = NavRows(window, lang)[4];   // 「压缩」—— 用户截图里被按黑的那一行

        // 打在行的右半边：那里一定是药丸，不会撞上左边的文字。
        var at = OnWindow(row, row.Bounds.Width - 24, row.Bounds.Height / 2);
        var pill = (Border)row.Content!;

        Assert.True(pill.Bounds.Height >= row.Bounds.Height - 1,
            $"药丸只占 {pill.Bounds.Height}，行高却有 {row.Bounds.Height} —— 没铺满的话，"
            + "模板那层深灰会从上下缝里透出来，等于没修");

        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        UiWait.Pump(80);

        var pressed = Pixels.At(window, at);
        // 再量一枚**角上**的像素：药丸是圆的、盖得住行中心，盖不住自己的四个角，
        // 而那层面皮是方的 —— 1.8.7 那句"已核对"就是从这里漏过去的
        // （用户 1.8.9 的截图量的正是角上那几粒 #686868，中心却是好的）。
        var corner = Pixels.At(window, OnWindow(row, 1, 1));
        window.MouseUp(at, MouseButton.Left);

        Assert.False(Pixels.Same(corner, Color.FromRgb(0x68, 0x68, 0x68)),
            $"药丸四角漏出主题的 {Pixels.Name(corner)}：药丸是圆的、面皮是方的，"
            + "没钉住面皮的话按下去就是四个角各一块黑（中心量不到这件事）");

        Assert.False(Pixels.Same(pressed, Color.FromRgb(0x68, 0x68, 0x68)),
            "按下又变回 SimpleTheme 那块 #686868 深灰了 —— 用户说的\"不协调\"就是这个");
        Assert.True(Pixels.Same(pressed, Pixels.Of(Design.Surface.Pressed)),
            $"按下量到 {Pixels.Name(pressed)}，期望 {Pixels.Name(Pixels.Of(Design.Surface.Pressed))}");
    }

    [AvaloniaFact]
    public void ThereIsNoUnclickableStripBetweenNavRows()
    {
        var lang = TestLang.Chinese();
        var window = new SettingsWindow(lang, new AppSettings(), Path.Combine(_root, "settings.ini")).Shown();
        var nav = NavRows(window, lang);

        Assert.True(nav.Count >= 9, $"九页导航应当都在，实际 {nav.Count} 行");

        for (var i = 0; i + 1 < nav.Count; i++)
        {
            // 分界那一行：上一行的下沿就是下一行的上沿。以前那里是 StackPanel 的 Spacing，
            // 命中测试只能拿到导航条本身 —— 用户的手指划过去就是"没反应"。
            var seam = OnWindow(nav[i], nav[i].Bounds.Width / 2, nav[i].Bounds.Height);
            var probe = OnWindow(nav[i], nav[i].Bounds.Width / 2, nav[i].Bounds.Height / 2);

            // headless 里命中测试要先有一帧渲染过：没量过像素就直接 GetVisualsAt 会一律返回空表
            // （实测：行中心也命中 0 个）。量一下再问，问的才是产品。
            Pixels.At(window, probe);

            var hits = window.GetVisualsAt(seam).ToList();

            Assert.True(hits.Any(visual => IsNavRow(visual, nav)),
                $"第 {i + 1} 与第 {i + 2} 行之间（y={seam.Y:0}）点不到任何一行导航，命中："
                + string.Join(" → ", hits.Select(h => h.GetType().Name))
                + $"；行 {i} 在 {nav[i].Bounds}，行 {i + 1} 在 {nav[i + 1].Bounds}");
        }
    }

    private static IReadOnlyList<Button> NavRows(Window window, LangCatalog lang)
    {
        var keys = new[]
        {
            "TEXT_SETTINGS_GENERAL", "TEXT_SETTINGS_FILE_ASSOCIATION", "TEXT_SETTINGS_CONTEXT_MENU",
            "TEXT_SETTINGS_EXTRACT", "TEXT_SETTINGS_COMPRESS", "TEXT_SETTINGS_VIEW",
            "TEXT_SETTINGS_SECURITY", "TEXT_SETTINGS_ADVANCED", "TEXT_SETTINGS_LANGUAGE",
        };

        var labels = keys.Select(key => lang.Label(key)).ToHashSet(StringComparer.Ordinal);
        return VisualQuery.Of<Button>(window).Where(b => labels.Contains(VisualQuery.TextOf(b) ?? string.Empty)).ToList();
    }

    private static bool IsNavRow(Visual visual, IReadOnlyList<Button> nav)
    {
        for (Visual? at = visual; at is not null; at = at.GetVisualParent())
        {
            if (nav.Any(row => ReferenceEquals(row, at)))
            {
                return true;
            }
        }

        return false;
    }

    private static Point OnWindow(Visual from, double x, double y)
        => from.TranslatePoint(new Point(x, y), (Visual?)from.GetVisualRoot()) ?? new Point(x, y);
}
