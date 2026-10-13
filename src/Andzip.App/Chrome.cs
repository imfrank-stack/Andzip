using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Andzip.App;

/// <summary>
/// 交互状态（悬停 / 按下 / 焦点）。
/// <para>为什么不用应用层样式 <c>Style(x =&gt; x.OfType&lt;Button&gt;().Class(":pointerover"))</c>：
/// 实测压不过 SimpleTheme 内部 <c>ControlTheme</c> 的 setter —— 注入悬停底色后中心像素一个字节都没变
/// （结论钉在 <c>ChromeCapabilityTests</c>）。所以状态由这里接 <see cref="Control.PointerEntered"/>
/// 等事件来驱动，好处是这套东西<strong>可以在 headless 里用真指针输入验证</strong>。</para>
/// </summary>
internal static class Chrome
{
    /// <summary>
    /// 蓝色工具栏带上的按钮：不换底色，用半透明白叠加层"浮起"。
    /// 在纯色底上这比"换个蓝"稳定，也不会让某个按钮突然变得比别的更抢眼。
    /// </summary>
    public static T OnBrand<T>(this T button) where T : Button
    {
        button.CornerRadius = Design.Radius.Md;
        return TrackStates(button, Design.Brand.OnBrandHover, Design.Brand.OnBrandPressed);
    }

    /// <summary>对话框主按钮：品牌蓝底白字。内距也在这里定 —— 各对话框的按钮不该有两种胖瘦。</summary>
    public static T Accent<T>(this T button) where T : Button
    {
        button.Background = Design.Brand.Base;
        button.Foreground = Design.Ink.OnBrand;
        button.BorderThickness = new Avalonia.Thickness(0);
        button.CornerRadius = Design.Radius.Sm;
        button.Padding = new Avalonia.Thickness(Design.Space.X3, Design.Space.X2, Design.Space.X3, Design.Space.X2);
        return TrackStates(button, Design.Brand.Bright, Design.Brand.Deep);
    }

    /// <summary>次按钮：白底描边，悬停时底变浅灰。主按钮旁边不该有两个会抢眼的实心块。</summary>
    public static T Ghost<T>(this T button) where T : Button
    {
        button.Background = Design.Surface.Base;
        button.Foreground = Design.Ink.Primary;
        button.BorderBrush = Design.Line.Strong;
        button.BorderThickness = new Avalonia.Thickness(1);
        button.CornerRadius = Design.Radius.Sm;
        button.Padding = new Avalonia.Thickness(Design.Space.X3, Design.Space.X2, Design.Space.X3, Design.Space.X2);
        return TrackStates(button, Design.Surface.Muted, Design.Line.Subtle);
    }

    /// <summary>危险动作的主按钮（永久删除）。红色只出现在这里和错误提示上，见规范 3.3。</summary>
    public static T Danger<T>(this T button) where T : Button
    {
        button.Background = Design.Semantic.Danger;
        button.Foreground = Design.Ink.OnBrand;
        button.BorderThickness = new Avalonia.Thickness(0);
        button.CornerRadius = Design.Radius.Sm;
        button.Padding = new Avalonia.Thickness(Design.Space.X3, Design.Space.X2, Design.Space.X3, Design.Space.X2);
        return TrackStates(button, Brush(0xD9, 0x3A, 0x2A), Brush(0xA6, 0x23, 0x17));
    }

    /// <summary>
    /// 每个窗口构造时调一次：底色、字体族、默认字号。
    /// 收成一个方法而不是各写各的，是因为"窗口底色"这种事一旦散开，
    /// 就会出现某个对话框忘了设、还是系统默认灰底的情况 —— 那正是改版要消灭的东西。
    /// </summary>
    public static T WindowSurface<T>(this T window) where T : Window
    {
        window.Background = Design.Surface.Base;
        window.FontFamily = Design.Font;
        window.FontSize = Design.Type.Body;
        return window;
    }

    /// <summary>输入框：圆角 + 焦点环（2px 品牌蓝描边）。</summary>
    public static TextBox Field(this TextBox box)
    {
        box.CornerRadius = Design.Radius.Sm;
        box.BorderBrush = Design.Line.Strong;
        box.BorderThickness = new Avalonia.Thickness(1);
        box.Background = Design.Surface.Base;
        box.MinHeight = 30;
        box.VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;

        box.AttachedToVisualTree += (_, _) => Restore();
        box.GotFocus += (_, _) =>
        {
            box.BorderBrush = Design.Brand.Base;
            box.BorderThickness = new Avalonia.Thickness(2);
        };
        box.LostFocus += (_, _) => Restore();
        return box;

        void Restore()
        {
            box.BorderBrush = Design.Line.Strong;
            box.BorderThickness = new Avalonia.Thickness(1);
        }
    }

    /// <summary>下拉框：与输入框同一套形状，避免"两个控件两种圆角"。</summary>
    public static ComboBox Picker(this ComboBox combo)
    {
        combo.CornerRadius = Design.Radius.Sm;
        combo.MinHeight = 30;
        return TrackStates(combo, Design.Surface.Muted, Design.Surface.Muted);
    }

    /// <summary>
    /// 接住四个指针事件切换底色。静止底色在调用时读一次 ——
    /// 调用方可能刚刚设过（工具栏按钮是 Transparent），不能写死成白。
    /// 泛型参数用 <see cref="TemplatedControl"/> 而不是 <c>Button</c>：下拉框也要同一套手感。
    /// </summary>
    private static T TrackStates<T>(T control, IBrush hover, IBrush pressed) where T : TemplatedControl
    {
        var rest = control.Background;

        control.PointerEntered += (_, _) => control.Background = hover;
        control.PointerExited += (_, _) => control.Background = rest;
        control.PointerPressed += (_, _) => control.Background = pressed;
        control.PointerReleased += (_, _) => control.Background =
            control.IsPointerOver ? hover : rest;

        return control;
    }

    private static IBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
}
