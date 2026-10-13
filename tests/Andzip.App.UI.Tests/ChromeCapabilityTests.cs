using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Andzip.App.UiTests;

/// <summary>
/// 界面规范（<c>docs/界面设计规范.md</c>）压在两个前提上，这里把它们钉成回归用例：
/// <list type="number">
///   <item><description>SimpleTheme 的控件模板真的吃 <c>CornerRadius</c> —— 否则整套圆角只是写在纸上。</description></item>
///   <item><description>headless 下指针移动会触发 <c>PointerEntered/Exited</c> —— 否则悬停态没法用测试证明存在。</description></item>
/// </list>
/// 两条都是<strong>量像素</strong>量出来的，不是"控件属性等于某个值"那种自我确认。
/// </summary>
public class ChromeCapabilityTests
{
    private static readonly Color Fill = Color.FromRgb(0x0F, 0x6C, 0xBD);
    private static readonly Color Page = Color.FromRgb(0xFF, 0xFF, 0xFF);

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
