using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Andzip.App;
using Andzip.App.Views;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Andzip.App.UiTests;

/// <summary>
/// 首页工具栏那九枚图标：几何来自 <c>assets/svg/*.svg</c>，界面读的就是那一份原文件。
/// <para>这一组要守的不是"画得好不好看"，而是<strong>设计稿与屏幕上是不是同一个东西</strong>：
/// 解析器少认一种写法、有人往 SVG 里加了个没支持的颜色或元素，表现都是"那个图标少一块"
/// 或"那一枚干脆没了" —— 而那正是用户在编辑器里改了半天、界面上毫无反应的一类问题。</para>
/// </summary>
[Collection("界面状态")]
public class ToolBarGlyphTests
{
    /// <summary>九枚图标的文件名（不含扩展名），顺序与工具栏一致。</summary>
    private static readonly string[] Names =
    [
        "open-archive", "extract", "new-archive", "add-file", "delete-file",
        "test-archive", "scan-file", "view", "language",
    ];

    /// <summary>原稿里那三个值 —— 每一个都必须有对应的主题令牌，见 <c>SvgGlyph.BrushFor</c>。</summary>
    private static readonly string[] MappedColors = ["#414B5A", "#A7B1BF", "#FFFFFF"];

    /// <summary>浅色调色板里 <c>Design.Icon.Glyph</c> 那一档，见 <c>Design.cs</c>。</summary>
    private static readonly Color GlyphInk = Color.FromRgb(0x5B, 0x64, 0x70);

    [AvaloniaFact]
    public void EveryPieceOfEveryDesignFileSurvivesTheParser()
    {
        foreach (var name in Names)
        {
            var source = XDocument.Parse(File.ReadAllText(Path.Combine(SvgDirectory(), name + ".svg")));
            var expected = source.Root!.Elements().Count(e => e.Name.LocalName is "path" or "rect" or "circle");

            var surface = (Panel)((Viewbox)SvgGlyph.Load(name, 40)).Child!;

            Assert.True(surface.Children.Count == expected,
                $"{name}.svg 里有 {expected} 片，界面上只画了 {surface.Children.Count} 片 —— "
                + "少的那一片在编辑器里看得见、在这里看不见，没人会怀疑是解析器漏了");
            Assert.True(surface.Width > 0 && surface.Height > 0, $"{name}.svg 没有视框尺寸");
        }
    }

    /// <summary>
    /// 九个文件用到的颜色必须全部有令牌。
    /// <para>渲染器遇到认不出的字面值会抛，但"抛在窗口构造里"意味着用户看到的是界面少一排按钮；
    /// 这条扫源码的用例把它挪到构建阶段，并且点名是哪一个文件哪一格。</para>
    /// </summary>
    [Fact]
    public void EveryColorInTheDesignFilesHasAToken()
    {
        var found = new List<string>();
        var unmapped = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SvgDirectory(), "*.svg"))
        {
            var literals = System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(file), "#[0-9A-Fa-f]{6}")
                .Select(m => m.Value);

            foreach (var literal in literals)
            {
                found.Add(literal);
                if (!MappedColors.Contains(literal, StringComparer.OrdinalIgnoreCase))
                {
                    unmapped.Add($"{Path.GetFileName(file)}: {literal}");
                }
            }
        }

        // 先钉"扫到了东西"：一条都没扫到说明正则或目录错了，那时下面那条恒真。
        Assert.True(found.Count >= 20, $"九个文件只扫出 {found.Count} 个字面色，扫描本身大概失效了");
        Assert.True(unmapped.Count == 0,
            "这些字面色没配主题令牌，深色模式下多半会看不见：\n    " + string.Join("\n    ", unmapped));
    }

    /// <summary>九枚都要真的画出东西来 —— 解析通过但一个像素都没落，等于图标不见了。</summary>
    [AvaloniaFact]
    public void AllNineGlyphsActuallyPaintSomethingOnScreen()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var icons = Names.Select(name => SvgGlyph.Load(name, 40)).ToList();
        foreach (var icon in icons)
        {
            row.Children.Add(icon);
        }

        var window = new Window
        {
            Content = row,
            Width = 600,
            Height = 120,
            Background = new SolidColorBrush(Colors.White),
        }.Shown();

        UiWait.Pump(120);
        var frame = window.CaptureRenderedFrame()!;
        var pixels = Read(frame);

        // 帧缓冲不一定是 1:1（这台机器上界面是 150% 缩放），所以按"这一枚在窗口里的位置 × 缩放"
        // 圈出它的取样框，而不是假设每格 48 像素 —— 假设错过一次，报的就是"某枚图标没画"。
        var scale = (double)frame.PixelSize.Width / window.Bounds.Width;

        for (var i = 0; i < icons.Count; i++)
        {
            var at = icons[i].TranslatePoint(new Point(), window)!.Value;
            var box = icons[i].Bounds;
            var ink = CountInk(
                pixels, frame.PixelSize.Width,
                (int)(at.X * scale), (int)(at.Y * scale),
                (int)((at.X + box.Width) * scale), (int)((at.Y + box.Height) * scale));

            Assert.True(ink > 30,
                $"{Names[i]} 那一格里只有 {ink} 个墨色像素 —— 图标没画出来（Viewbox 压成 0？几何解析空？）");
        }
    }

    [AvaloniaFact]
    public void TheToolBarShowsNineButtonsWithTheDesignLabels()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();

        var labels = ToolBarKeys;

        var texts = VisualQuery.VisibleTexts(window);
        foreach (var key in labels)
        {
            Assert.Contains(lang.Label(key), texts);
        }

        // 每一颗都得带着自己那枚图标（不是占位的空 Border）。
        var buttons = VisualQuery.Of<Button>(window)
            .Where(b => labels.Any(k => Equals(VisualQuery.TextOf(b), lang.Label(k))))
            .ToList();

        Assert.Equal(labels.Length, buttons.Count);
        Assert.All(buttons, b => Assert.NotNull(
            VisualQuery.Of<Viewbox>(b).FirstOrDefault(v => v.Child is Panel { Children.Count: > 0 })));
    }

    /// <summary>
    /// 九颗按钮一样宽（3.1.0：「发现每个大小不一样，所以将文字修改为：打开，解压，新建…」）。
    /// <para>宽窄不齐的直接原因是标签长短不齐 ——「打开压缩包」五个字把那一颗撑得比「查看」宽出
    /// 40 物理像素（真机量的），而图标那一格本来是一样大的。所以两件事都要钉住：
    /// <strong>中文这一排每个标签恰好两个字</strong>（多一个字这一条就红，改文案的人当场看见原因），
    /// 以及<strong>量出来的宽度确实相同</strong>（万一哪天按钮里多了别的东西，光看文案看不出来）。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheNineToolBarButtonsAreEquallyWide()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();
        UiWait.Pump(60);

        foreach (var key in ToolBarKeys)
        {
            var label = lang.Label(key);
            Assert.True(label.Length == 2,
                $"「{key}」的标签是「{label}」（{label.Length} 个字）—— 这一排要的都是两个字，"
                + "长短不齐，按钮就宽窄不齐");
        }

        var widths = VisualQuery.Of<Button>(window)
            .Where(b => ToolBarKeys.Any(k => Equals(VisualQuery.TextOf(b), lang.Label(k))))
            .Select(b => b.Bounds.Width)
            .ToList();

        Assert.Equal(ToolBarKeys.Length, widths.Count);
        Assert.True(widths.Max() - widths.Min() < 0.5,
            $"九颗按钮宽窄不齐：{string.Join(" / ", widths.Select(w => w.ToString("0.##")))}");
    }

    /// <summary>
    /// 九颗按钮把整条卡片<strong>分光</strong>（3.5.0，用户的「将首页的这9个按钮平均布局在整个软件宽度上」）。
    /// <para>以前那一行是一列 <see cref="StackPanel"/>：它只按每颗自己的内容宽度从左排，
    /// 卡片明明拉满整窗宽度，九颗却挤在左边、右端空出一大片。这一条量的就是那个"空"没了 ——
    /// 第一颗贴着左沿，而整排一直铺到右沿。<strong>每颗一样宽</strong>由
    /// <see cref="TheNineToolBarButtonsAreEquallyWide"/> 那条守着，这里只管"分光"。</para>
    /// <para>右沿允许<strong>不到一格</strong>的溢出：<see cref="UniformGrid"/> 的格子是
    /// 按整数向上取的（960 宽时每格 107、九格合计 963），多出来的那几像素落在卡片自己的内距里。
    /// 判据因此写成"盖到右沿、且溢出不超过一格"，而不是"逐像素对齐" ——
    /// 后者在真的实现上永远红，会把人逼去改一个没错的地方。</para>
    /// <para>把那一行换回 StackPanel，这一条当场找不到九格的 <see cref="UniformGrid"/>，立刻红。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheNineToolBarButtonsFillTheWholeCardWidth()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();
        UiWait.Pump(60);

        var row = VisualQuery.Of<UniformGrid>(window).Single(grid =>
            grid.Children.OfType<Button>().Count(
                b => ToolBarKeys.Any(k => Equals(VisualQuery.TextOf(b), lang.Label(k)))) >= 9);

        Assert.Equal(9, row.Columns);
        Assert.Equal(1, row.Rows);

        var buttons = row.Children.OfType<Button>().ToList();
        var width = row.Bounds.Width;
        Assert.True(width > 0, "工具栏那一行没量到宽度");

        var first = buttons[0].TranslatePoint(new Point(), row)!.Value;
        var last = buttons[^1];
        var lastAt = last.TranslatePoint(new Point(), row)!.Value;
        var covered = lastAt.X + last.Bounds.Width - first.X;
        var cell = last.Bounds.Width;

        Assert.True(Math.Abs(first.X) < 0.5,
            $"第一颗左边还空着 {first.X:0.##} —— 这一排没从左沿开始");
        Assert.True(covered >= width - 0.5,
            $"这一排只占了 {covered:0.##}，而卡片内沿有 {width:0.##} —— 右端空着一截");
        Assert.True(covered <= width + cell,
            $"这一排占了 {covered:0.##}，比卡片内沿 {width:0.##} 多出 {covered - width:0.##} —— 超过一格了");
    }

    /// <summary>首页那一排九颗按钮的标签键，顺序与 <c>MainToolBar.Create</c> 一致。</summary>
    private static readonly string[] ToolBarKeys =
    [
        "TEXT_TOOLBAR_OPEN", "TEXT_TOOLBAR_EXTRACT", "TEXT_TOOLBAR_NEW", "TEXT_TOOLBAR_ADD",
        "TEXT_TOOLBAR_DELETE", "TEXT_TOOLBAR_TEST", "TEXT_TOOLBAR_SCAN", "TEXT_TOOLBAR_VIEW",
        "TEXT_TOOLBAR_LANGUAGE",
    ];

    /// <summary>
    /// 「切换语言」必须真的能点，并且落在语言页上。
    /// <para>这颗以前写着当前语言、点不动。稿子把它改名成「切换语言」之后，
    /// <strong>一颗长得像命令却点不动的按钮比没有这颗更糟</strong> ——
    /// 所以这一条量的不是"有没有这个按钮"，而是"按下去有没有到那页去"。</para>
    /// </summary>
    [AvaloniaFact]
    public void SwitchLanguageButtonOpensSettingsOnTheLanguagePage()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();

        var button = VisualQuery.Of<Button>(window)
            .Single(b => Equals(VisualQuery.TextOf(b), lang.Label("TEXT_TOOLBAR_LANGUAGE")));

        Assert.True(button.IsEnabled, "「切换语言」点不动 —— 那这颗按钮就是在骗人");

        Clicker.Invoke(button);
        UiWait.Pump(300);

        var dialog = window.OwnedWindows.OfType<SettingsWindow>().SingleOrDefault();
        Assert.NotNull(dialog);
        try
        {
            Assert.Contains(lang.Label("TEXT_SETTINGS_LANGUAGE_TITLE"), VisualQuery.VisibleTexts(dialog!));
        }
        finally
        {
            dialog!.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ---------- 取样 ----------

    private static string SvgDirectory() => Path.Combine(RepositoryRoot(), "assets", "svg");

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Andzip.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根");
    }

    private static Color[] Read(WriteableBitmap frame)
    {
        var buffer = new byte[frame.PixelSize.Width * frame.PixelSize.Height * 4];
        using var locked = frame.Lock();
        Marshal.Copy(locked.Address, buffer, 0, buffer.Length);

        // headless 这块帧缓冲是 R,G,B,A（见 Pixels.At 那段注释）。
        return Enumerable.Range(0, frame.PixelSize.Width * frame.PixelSize.Height)
            .Select(i => Color.FromRgb(buffer[i * 4], buffer[i * 4 + 1], buffer[i * 4 + 2]))
            .ToArray();
    }

    private static int CountInk(Color[] pixels, int width, int x0, int y0, int x1, int y1)
    {
        var hits = 0;
        for (var y = y0; y < Math.Min(y1, pixels.Length / width); y++)
        {
            for (var x = x0; x < Math.Min(x1, width); x++)
            {
                var c = pixels[y * width + x];
                if (Math.Abs(c.R - GlyphInk.R) < 12 && Math.Abs(c.G - GlyphInk.G) < 12
                    && Math.Abs(c.B - GlyphInk.B) < 12)
                {
                    hits++;
                }
            }
        }

        return hits;
    }
}
