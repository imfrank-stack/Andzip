using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Andzip.App.UiTests;

/// <summary>
/// 开关这件事的<strong>先探后写</strong>：设置页九个页面的布尔项要从勾选框换成开关，
/// 而开关是 <see cref="ToggleButton"/> 画的 —— 上一轮已经知道 SimpleTheme 的 Button 模板吃
/// <c>CornerRadius</c>，但 ToggleButton 是另一份模板，不能想当然。
/// <para>三条断言各管一头：轨道真的是胶囊（角回到画布色）、滑块真的换边、空格键真的能切
/// （换成自绘控件最容易丢的就是这一条 —— 丢了就只有鼠标能用）。</para>
/// </summary>
[Collection("界面状态")]
public class ToggleSwitchTests
{
    [AvaloniaFact]
    public void TrackIsAPillAndTheKnobMovesWithTheState()
    {
        var (window, toggle) = Host();
        var corner = At(window, toggle, 1, 1);

        Assert.True(Pixels.Same(corner, Page),
            $"轨道四角不是画布色而是 {Pixels.Name(corner)} —— ToggleButton 的模板没吃 CornerRadius，"
            + "开关会长成一个方方块");

        var left = KnobSide(window, toggle);
        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        var right = KnobSide(window, toggle);

        Assert.Equal(Knob.Left, left);
        Assert.Equal(Knob.Right, right);

        // 开着的那一截必须是品牌蓝。这一条不是锦上添花：把轨道色设成控件自己的 Background
        // 看着完全正确，但 SimpleTheme 的 :checked setter 会把它盖成模板灰 ——
        // 于是"开着"和"关着"只差滑块位置，颜色上根本分不出来。
        var track = At(window, toggle, 8, toggle.Bounds.Height / 2);
        Assert.True(track.B > track.R + 30 && track.B > track.G + 20,
            $"开着的轨道不是品牌蓝而是 {Pixels.Name(track)} —— 轨道色被模板的 :checked 抢走了");
    }

    [AvaloniaFact]
    public void SpaceKeyTogglesTheSwitch()
    {
        var (window, toggle) = Host();

        toggle.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(toggle.IsChecked == true, "空格没能打开开关：键盘用户用不了这一项");

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(toggle.IsChecked == false, "空格没能关上开关");
    }

    [AvaloniaFact]
    public void ARealClickTogglesItAndFiresTheStateChangedHandler()
    {
        var (window, toggle) = Host();
        var fired = 0;
        toggle.IsCheckedChanged += (_, _) => fired++;

        var at = toggle.TranslatePoint(new Point(toggle.Bounds.Width / 2, toggle.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(toggle.IsChecked == true, "真指针点下去没切换");
        Assert.True(fired > 0, "切换了但事件没发出去 —— 设置页的「立即应用」按钮不会亮");
    }

    private static (Window Window, ToggleButton Toggle) Host()
    {
        var toggle = new ToggleButton().ToggleSwitch();
        var window = new Window
        {
            Content = new StackPanel
            {
                Margin = new Thickness(40),
                Children = { toggle },
            },
            Width = 320,
            Height = 240,
            Background = new SolidColorBrush(Page),
        }.Shown();

        Dispatcher.UIThread.RunJobs();
        return (window, toggle);
    }

    private static readonly Color Page = Color.FromRgb(0xEE, 0xF1, 0xF5);

    private enum Knob
    {
        Left,
        Right,
    }

    /// <summary>
    /// 控件的 <c>Bounds</c> 是<strong>相对父容器</strong>的，直接拿去量帧缓冲会量到别处 ——
    /// 这条一开始就是这么假绿的：角上取到的是窗口背景，跟圆不圆角没关系。
    /// </summary>
    private static Color At(Window window, Control control, double x, double y)
        => Pixels.At(window, control.TranslatePoint(new Point(x, y), window)!.Value);

    /// <summary>滑块在哪一边：轨道竖向中线上，左右各取一个靠边的点，白的那侧就是滑块。</summary>
    private static Knob KnobSide(Window window, ToggleButton toggle)
    {
        var midY = toggle.Bounds.Height / 2;
        var left = At(window, toggle, 7, midY);
        var right = At(window, toggle, toggle.Bounds.Width - 7, midY);

        return IsLight(left) ? Knob.Left : Knob.Right;

        static bool IsLight(Color c) => c.R > 0xE0 && c.G > 0xE0 && c.B > 0xE0;
    }
}
