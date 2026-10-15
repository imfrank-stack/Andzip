using Andzip.App.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Andzip.App.UiTests;

/// <summary>
/// 自绘标题栏的把关。这里最值钱的一条是"最小化那条线真的是一条线" ——
/// 纯水平线的几何外接框<strong>高度是 0</strong>，用 <c>Stretch.Uniform</c> 等比拉伸时
/// 缩放因子算不出来，实测那条线会缩成一个点：属性全对、屏幕上没有。
/// </summary>
[Collection("界面状态")]
public class TitleBarTests
{
    [AvaloniaFact]
    public void MinimizeIsALineNotADot()
    {
        var (window, bar) = Host();
        var glyph = GlyphOf(bar, "最小化");
        var center = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), window)!.Value;

        // 同一条水平线上、中心左右各 4px：还是线的话两点都该是墨色，缩成点就至少有一边是白的。
        var left = Pixels.At(window, new Point(center.X - 4, center.Y));
        var right = Pixels.At(window, new Point(center.X + 4, center.Y));

        Assert.True(IsInk(left) && IsInk(right),
            $"最小化不是一条线（左右两点 {Pixels.Name(left)} / {Pixels.Name(right)}，都是面板白）—— "
            + "十有八九是 Stretch 把零高度的几何缩成了一个点");
    }

    [AvaloniaFact]
    public void TheCloseButtonActuallyClosesTheWindow()
    {
        var (window, bar) = Host();
        var close = ButtonOf(bar, "关闭");

        Clicker.Invoke(close);
        UiWait.Pump(50);

        Assert.False(window.IsVisible, "点了关闭但窗口还在");
    }

    [AvaloniaFact]
    public void EveryWindowButtonHasAnAccessibleName()
    {
        var (_, bar) = Host();

        // 读屏里三个按钮不能都念成"按钮"—— 名字来自语言包，缺键就会露出裸键名。
        var names = VisualQuery.Of<Button>(bar).Select(Avalonia.Automation.AutomationProperties.GetName);

        Assert.Equal(3, names.Count());
        Assert.DoesNotContain(names, n => n.StartsWith("TEXT_", StringComparison.Ordinal));
        Assert.DoesNotContain(names, string.IsNullOrWhiteSpace);
    }

    [AvaloniaFact]
    public void TheCaptionFollowsTheWindowTitle()
    {
        var (window, bar) = Host();

        Assert.Contains("探针窗口", VisualQuery.Of<TextBlock>(bar).Select(t => t.Text));

        window.Title = "换了标题";
        UiWait.Pump(50);

        // 绑的是 Window.Title：开包 / 关包 / 另存为都改它，抄一份就会看到标题栏还停在旧包名。
        Assert.Contains("换了标题", VisualQuery.Of<TextBlock>(bar).Select(t => t.Text));
    }

    [AvaloniaFact]
    public void DialogsGetOnlyACloseButton()
    {
        var lang = TestLang.Chinese();
        var dialog = new PasswordWindow(lang).Shown();

        var buttons = VisualQuery.Of<Button>(VisualQuery.Of<Andzip.App.Views.TitleBar>(dialog).Single()).ToList();

        Assert.Single(buttons);
        Assert.Equal("关闭", Avalonia.Automation.AutomationProperties.GetName(buttons[0]));
    }

    /// <summary>
    /// 按住标题栏那三颗：看到的必须还是那枚圆角芯片，不能变回主题那块方的 <c>#686868</c>。
    /// <para>用户两张截图说的是同一件事：悬停是淡红圆角芯片，按下去是一整块 46×40 的方黑框
    /// —— 芯片是圆的、主题那块灰是方的，它从芯片四周各探出来一点，读起来就是"圆角变方正"。</para>
    /// <para>1.8.8 给这三颗接过 <c>PinFace</c>，但那条"按下"的像素用例量的是<strong>工具栏</strong>：
    /// 标题栏这一颗从来没人量过，坏了也没人知道。<strong>接线存在 ≠ 接线生效</strong>，这条补的是生效。</para>
    /// </summary>
    [AvaloniaFact]
    public void PressingACaptionButtonKeepsTheRoundedChip()
    {
        var (window, bar) = Host();
        var close = ButtonOf(bar, "关闭");
        var at = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        Pixels.At(window, at);

        window.MouseMove(at);
        UiWait.Pump(60);
        window.MouseDown(at, MouseButton.Left);
        UiWait.Pump(120);

        var gray = 0;
        var chip = 0;
        for (var dy = -19; dy <= 19; dy++)
        {
            for (var dx = -22; dx <= 22; dx++)
            {
                var c = Pixels.At(window, at + new Point(dx, dy));
                if (Pixels.Same(c, Color.FromRgb(0x68, 0x68, 0x68)))
                {
                    gray++;
                }

                if (Pixels.Same(c, Color.FromRgb(0xFD, 0xEC, 0xEA)))
                {
                    chip++;
                }
            }
        }

        // 松手点挪到芯片外面：不然这一按真把窗口关了，用例只能跑一次。
        var away = at + new Point(0, 160);
        window.MouseMove(away);
        window.MouseUp(away, MouseButton.Left);
        UiWait.Pump(60);

        Assert.True(gray == 0,
            $"按住关闭钮，那一格里有 {gray} 个主题的 #686868 —— 圆角芯片被那块方的灰盖住了（Chrome.PinFace 没生效）");
        Assert.True(chip > 200,
            $"按住时那一格只量到 {chip} 个淡红芯片像素 —— 灰是没有，但芯片也没画出来，等于看着像\"没反应\"");
    }

    /// <summary>
    /// 顶栏收成一行（2.4.0）：菜单搬进标题栏那一行，原来第一行那句标题文字删掉，工具栏整体上移一行。
    /// <para>用户的原话是「将我们的首页（图2），上面的文字去掉（第一行），第二行整体上移」。
    /// 版本号<strong>不能顺手删掉</strong> —— 1.9.1 定的规矩是每张截图自报身份；
    /// 3.1.0 起它住在面板右下角（见 <see cref="TheVersionSitsInTheBottomRightCorner"/>）。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheMainWindowWearsItsMenuOnTheCaptionRow()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new Andzip.Core.Settings.AppSettings()).Shown();
        UiWait.Pump(60);

        var bar = VisualQuery.Of<TitleBar>(window).Single();
        Assert.NotEmpty(VisualQuery.Of<Menu>(bar));

        // 工具栏紧跟在标题栏那一行下面，中间没有第三行 —— 这就是"整体上移"。
        var open = VisualQuery.Of<Button>(window)
            .Single(b => Equals(VisualQuery.TextOf(b), lang.Label("TEXT_TOOLBAR_OPEN")));
        var barBottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), window)!.Value.Y;
        var toolbarTop = open.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(toolbarTop <= barBottom + 2,
            $"标题栏底在 y={barBottom:0}，工具栏顶却在 y={toolbarTop:0} —— 中间还压着一行");

        var shown = VisualQuery.VisibleTexts(window);
        Assert.DoesNotContain(window.Title, shown, StringComparer.Ordinal);

        // 版本号还在，只是换了地方：既在屏幕上看得见，也还在窗口自己的 Title 里
        var version = Version();
        Assert.Contains(version, shown, StringComparer.Ordinal);
        Assert.Contains(version, window.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// 版本号住在面板右下角，<strong>不在标题栏那一行</strong>（3.1.0 用户要求：「将版本号放在右下角的位置」）。
    /// <para>只断言"这一小截字还看得见"守不住这次改动 —— 挪回标题栏它照样绿。所以量的是位置：
    /// 右沿与下沿都要贴到面板那一角，而且不能还是标题栏的后代。</para>
    /// <para><strong>另一条是这一版自己踩出来的</strong>：第一版把它做成浮在面板上的覆盖层，
    /// 真机截图上它正压在底部栏那枚「图标视图」按钮身上，而覆盖层不参与布局，
    /// headless 里谁也不与谁重叠、全绿。所以这里把"与同一行里的按钮不许重叠"写成断言，
    /// 并且两个视图各量一遍（右下角那一排在首页与压缩包视图里都有住户）。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheVersionSitsInTheBottomRightCorner()
    {
        var window = new MainWindow(TestLang.Chinese(), new Andzip.Core.Settings.AppSettings()).Shown();
        UiWait.Pump(60);
        AssertBadgeInCorner(window, Badge(window), "首页");

        var directory = System.IO.Directory.CreateTempSubdirectory("andzip-badge-").FullName;
        try
        {
            var zip = System.IO.Path.Combine(directory, "box.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                archive.CreateEntry("a.txt");
            }

            UiWait.Until(window.OpenArchive(zip), "打开压缩包");
            AssertBadgeInCorner(window, Badge(window), "压缩包视图");
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static TextBlock Badge(Window window)
        => VisualQuery.Of<TextBlock>(window).Single(t => t.Text == Version());

    private static void AssertBadgeInCorner(Window window, TextBlock badge, string where)
    {
        var at = badge.TranslatePoint(new Point(), window)!.Value;
        var rightEdge = at.X + badge.Bounds.Width;
        var bottomEdge = at.Y + badge.Bounds.Height;

        Assert.True(window.Bounds.Width - rightEdge is > 0 and < 60,
            $"{where}：版本号右沿在 x={rightEdge:0}，窗口宽 {window.Bounds.Width:0} —— 没贴到右边");
        Assert.True(window.Bounds.Height - bottomEdge is > 0 and < 60,
            $"{where}：版本号下沿在 y={bottomEdge:0}，窗口高 {window.Bounds.Height:0} —— 没贴到底部");
        Assert.True(at.Y > window.Bounds.Height / 2,
            $"{where}：版本号在 y={at.Y:0}，那是上半屏（窗口高 {window.Bounds.Height:0}）");
        Assert.Empty(VisualQuery.Of<TextBlock>(VisualQuery.Of<TitleBar>(window).Single())
            .Where(t => t.Text == Version()));

        foreach (var button in VisualQuery.Of<Button>(window))
        {
            var b = button.TranslatePoint(new Point(), window)!.Value;
            var sameRow = b.Y < bottomEdge && b.Y + button.Bounds.Height > at.Y;
            var sameCols = b.X < rightEdge && b.X + button.Bounds.Width > at.X;
            Assert.False(sameRow && sameCols,
                $"{where}：版本号与一颗按钮重叠（按钮左上 {b.X:0},{b.Y:0}，尺寸 "
                + $"{button.Bounds.Width:0}x{button.Bounds.Height:0}；版本号左上 {at.X:0},{at.Y:0}）"
                + " —— 覆盖层不参与布局，这种重叠在 headless 之外才看得见");
        }
    }

    /// <summary>面板右下角那一小截版本号，与 <c>MainWindow</c> 拼 Title 时用的是同一个算法。</summary>
    private static string Version()
    {
        var v = typeof(MainWindow).Assembly.GetName().Version!;
        return $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    private static (Window Window, TitleBar Bar) Host()
    {
        var lang = TestLang.Chinese();
        var content = new Border { Width = 320, Height = 200 };
        var window = new Window { Width = 460, Height = 320 }.Shown();

        // Chromeless 要在窗口有句柄之后再装，与真实对话框的顺序一致。
        window.Chromeless(lang, content, resizable: true);
        UiWait.Pump(50);
        window.Title = "探针窗口";
        UiWait.Pump(50);

        return (window, VisualQuery.Of<TitleBar>(window).Single());
    }

    private static Path GlyphOf(TitleBar bar, string name) => ButtonOf(bar, name).Content as Path
        ?? throw new InvalidOperationException($"「{name}」按钮里不是线稿图形。");

    private static Button ButtonOf(TitleBar bar, string name)
        => VisualQuery.Of<Button>(bar).SingleOrDefault(b =>
               Avalonia.Automation.AutomationProperties.GetName(b) == name)
           ?? throw new InvalidOperationException($"标题栏里找不到「{name}」按钮。");

    /// <summary>
    /// 这条线只有 1.1 描边，抗锯齿之后实测是 <c>#ACB1B7</c> 而不是令牌那支 <c>#5B6470</c> ——
    /// 所以判"有没有墨"要比"比面板白暗多少"，不能按实心色卡（卡了就会假红）。
    /// </summary>
    private static bool IsInk(Color c) => Pixels.Luminance(c) < 0.85;
}
