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
