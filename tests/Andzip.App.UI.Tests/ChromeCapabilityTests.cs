using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Andzip.App.UiTests;

/// <summary>
/// 界面规范（<c>docs/界面设计规范.md</c>）压在三个前提上，这里把它们钉成回归用例：
/// <list type="number">
///   <item><description>SimpleTheme 的控件模板真的吃 <c>CornerRadius</c> —— 否则整套圆角只是写在纸上。</description></item>
///   <item><description>headless 下指针移动会触发 <c>PointerEntered/Exited</c> —— 否则悬停态没法用测试证明存在。</description></item>
///   <item><description>headless 的合成按住<strong>能</strong>复现主题涂在模板面皮上的那块 <c>#686868</c>
///   —— 所以"按下这一刻"是可以断言的，不必只靠真机（1.8.8 实测；早先记的"复现不了"是错的，
///   错在拿一个已经接过 Chrome 的控件去试）。</description></item>
/// </list>
/// 三条都是<strong>量像素</strong>量出来的，不是"控件属性等于某个值"那种自我确认。
/// </summary>
[Collection("界面状态")]
public class ChromeCapabilityTests
{
    private static readonly Color Fill = Color.FromRgb(0x0F, 0x6C, 0xBD);
    private static readonly Color Page = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>SimpleTheme 的 Button 在 <c>:pressed</c> 上涂给模板面皮的那块灰。</summary>
    private static readonly Color ThemeGray = Color.FromRgb(0x68, 0x68, 0x68);

    /// <summary><c>Design.Surface.Pressed</c>：我们给"按住"这一档定的颜色。</summary>
    private static readonly Color OurPressed = Color.FromRgb(0xE9, 0xED, 0xF1);

    private static readonly Color OurHover = Color.FromRgb(0xF6, 0xF8, 0xFA);

    [AvaloniaFact]
    public void CornerRadiusActuallyRoundsTheControl()
    {
        var rounded = Button(corner: 8);
        var square = Button(corner: null);
        var window = new Window
        {
            Content = new StackPanel
            {
                Spacing = 24,
                Margin = new Thickness(24),
                Children = { rounded, square },
            },
            Width = 420,
            Height = 320,
            Background = Brushes.White,
        }.Shown();

        // 圆角生效 = 四个角回到页面底色；没生效 = 四个角还是填充色。
        Assert.True(CornersArePageColored(window, rounded), "CornerRadius 设了但四角仍是填充色：模板没吃这个属性。");
        Assert.False(CornersArePageColored(window, square), "对照组不成立：没设圆角的按钮四角也变白了，说明测量方式有问题。");
    }

    [AvaloniaFact]
    public void PointerEnterAndExitFireUnderHeadlessInput()
    {
        var button = Button(corner: 6);
        var window = new Window
        {
            Content = new StackPanel { Margin = new Thickness(24), Children = { button } },
            Width = 420,
            Height = 320,
            Background = Brushes.White,
        }.Shown();

        var entered = 0;
        var exited = 0;
        button.PointerEntered += (_, _) => entered++;
        button.PointerExited += (_, _) => exited++;

        var over = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseMove(over);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(over + new Point(0, 260));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, entered);
        Assert.Equal(1, exited);
    }

    /// <summary>
    /// 按住这一下：裸按钮露出主题那块灰，接了 Chrome 的工具栏按钮露出我们那一档。
    /// <para>两条一起断言才有意义 —— 只有前一条成立，后一条才算"挡住了"什么。
    /// 用户 1.8.8 的截图就是这个：「好多地方鼠标点击瞬间会（从浅色药丸）变成那一块深灰，很割裂」。
    /// 量的是工具栏「打开」，因为它走 <c>Chrome.Lift</c>（药丸在控件底下，控件自己必须一直透明）；
    /// <c>Ghost</c>/<c>Accent</c>/<c>Picker</c> 走 <c>TrackStates</c>，同一枚面皮同一个办法。</para>
    /// <para>取样点刻意落在按钮右端的空白处：那里不会有图标与文字挡着，量到的就是底色本身。</para>
    /// </summary>
    [AvaloniaFact]
    public void HoldingAButtonShowsOurShadeWhileABareOneStillShowsTheThemeGray()
    {
        var lang = TestLang.Chinese();
        var window = new Andzip.App.MainWindow(lang, new Andzip.Core.Settings.AppSettings()).Shown();
        Pixels.At(window, new Point(4, 4));   // 命中与模板都要先有一帧渲染过

        var open = VisualQuery.Of<Button>(window).First(b => (VisualQuery.TextOf(b) ?? "").Contains("打开"));
        var onToolbar = At(window, open, open.Bounds.Width - 10, open.Bounds.Height / 2);

        window.MouseMove(onToolbar);
        UiWait.Pump(80);
        var hover = Pixels.At(window, onToolbar);
        window.MouseDown(onToolbar, MouseButton.Left);
        UiWait.Pump(80);
        var held = Pixels.At(window, onToolbar);
        window.MouseUp(onToolbar, MouseButton.Left);
        UiWait.Pump(40);

        Assert.True(Same(hover, OurHover), $"工具栏按钮悬停应当是我们那档 {Name(OurHover)}，量到 {Name(hover)}");
        Assert.True(Same(held, OurPressed),
            $"按住那一下露出来的还是主题的 {Name(ThemeGray)} —— 面皮没钉住（Chrome.PinFace），量到 {Name(held)}");

        // 对照：同一套输入打到没接 Chrome 的裸按钮上，必须现出那块灰。
        var bare = new Button { Content = "x", Width = 120, Height = 40 };
        var other = new Window
        {
            Content = new StackPanel { Margin = new Thickness(24), Children = { bare } },
            Width = 300,
            Height = 200,
            Background = Brushes.White,
        }.Shown();
        Pixels.At(other, new Point(4, 4));

        var onBare = At(other, bare, bare.Bounds.Width - 10, bare.Bounds.Height / 2);
        other.MouseDown(onBare, MouseButton.Left);
        UiWait.Pump(80);
        var bareHeld = Pixels.At(other, onBare);
        other.MouseUp(onBare, MouseButton.Left);

        Assert.True(Same(bareHeld, ThemeGray),
            $"对照组失效：裸按钮按住都没变 {Name(ThemeGray)}（量到 {Name(bareHeld)}），"
            + "说明这套主题的伪类没在 headless 里现形，上面那条断言就成了白过的");
    }

    private static Point At(Window window, Visual target, double x, double y)
        => target.TranslatePoint(new Point(x, y), window)!.Value;

    private static string Name(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    private static Button Button(double? corner) => new()
    {
        Content = "x",
        Background = new SolidColorBrush(Fill),
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        CornerRadius = corner is null ? default : new CornerRadius(corner.Value),
        Padding = new Thickness(28, 14, 28, 14),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static bool CornersArePageColored(Window window, Control control)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        using (frame!)
        {
            var scale = (double)frame.PixelSize.Width / window.Bounds.Width;
            var origin = control.TranslatePoint(new Point(0, 0), window)!.Value;
            var left = (int)(origin.X * scale);
            var top = (int)(origin.Y * scale);
            var right = (int)((origin.X + control.Bounds.Width) * scale) - 1;
            var bottom = (int)((origin.Y + control.Bounds.Height) * scale) - 1;

            return new[]
            {
                Pixel(frame, left, top),
                Pixel(frame, right, top),
                Pixel(frame, left, bottom),
                Pixel(frame, right, bottom),
            }.All(c => Same(c, Page));
        }
    }

    private static Color Pixel(WriteableBitmap frame, int x, int y)
    {
        x = Math.Clamp(x, 0, frame.PixelSize.Width - 1);
        y = Math.Clamp(y, 0, frame.PixelSize.Height - 1);

        using var locked = frame.Lock();
        var stride = locked.RowBytes;
        var buffer = new byte[stride * frame.PixelSize.Height];
        System.Runtime.InteropServices.Marshal.Copy(locked.Address, buffer, 0, buffer.Length);

        var at = y * stride + x * 4;
        return Color.FromRgb(buffer[at + 2], buffer[at + 1], buffer[at]);
    }

    /// <summary>
    /// 这个 headless 后端的帧缓冲通道序与 Bgra32 不一致（实测读出来是 R,G,B 顺序），
    /// 逐通道比会假阴性；只问"是不是这个颜色"，把三通道排序后比就够了。
    /// </summary>
    private static bool Same(Color a, Color b)
    {
        static int[] Sort(Color c) => [.. new[] { (int)c.R, c.G, c.B }.OrderBy(v => v)];
        var x = Sort(a);
        var y = Sort(b);
        return x[0] == y[0] && x[1] == y[1] && x[2] == y[2];
    }
}
