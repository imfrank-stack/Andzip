using Andzip.App.Views;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

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
    /// 浅色卡片上的大按钮（工具栏）：悬停是<strong>比卡片深一档</strong>的圆角药丸。
    /// <para>1.6.0 之前这条带子是实心品牌蓝，悬停只能叠半透明白"浮亮"；带子改成白色卡片之后
    /// 方向就反过来了 —— 白底上再叠白看不见，往下压一档才读得出"这颗被指到了"。
    /// 规范里"悬停必须看得见"没变，变的只是它长什么样。</para>
    /// </summary>
    public static T OnCard<T>(this T button) where T : Button
    {
        button.CornerRadius = Design.Radius.Lg;
        return TrackStates(button, Design.Line.Subtle, Design.Line.Strong);
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
    /// <para>1.7.0 起窗口底色是<strong>画布</strong>而不是卡片白：卡片要浮起来，
    /// 底下那层必须比卡片暗一档，否则白上放白只能靠阴影硬撑。</para>
    /// </summary>
    public static T WindowSurface<T>(this T window) where T : Window
    {
        window.Background = Brushes.Transparent;
        window.FontFamily = Design.Font;
        window.FontSize = Design.Type.Body;
        return window;
    }

    /// <summary>
    /// 把窗口装成"一张圆角阴影面板"：系统标题栏换成自绘的那一条，外面留一圈透明边距给圆角与阴影。
    /// <para>用的是 <c>ExtendClientAreaToDecorationsHint</c> 而不是 <c>SystemDecorations=None</c> ——
    /// 前者保留系统给的贴边缩放、Win+方向键分屏与任务栏让位，后者这些都得自己写。</para>
    /// <para>可行性是实测过的：这台 Windows 10 19045 上透明窗口确实生效
    /// （圆角外能看见后面的窗口，见开发记录第七节第 31 条），不像 DWM 深色标题栏那条"返回成功但屏幕不动"。</para>
    /// </summary>
    public static void Chromeless(this Window window, LangCatalog lang, Control content, bool resizable = false)
    {
        window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;

        var dock = new DockPanel();
        var bar = new TitleBar(window, lang, resizable);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(content);

        window.Content = new Border
        {
            Margin = new Thickness(Design.Space.X2 + 2),
            CornerRadius = new CornerRadius(14),
            Background = Design.Surface.Base,
            // 一圈比白深一点的发丝边：桌面也是浅色的时候，只有阴影分不出边界。
            BorderBrush = Design.Line.Subtle,
            BorderThickness = new Thickness(1),
            BoxShadow = Design.Shadow.Floating,
            Child = dock,
        };
    }

    /// <summary>
    /// 开关。用 <see cref="ToggleButton"/> 而不是自己画一个会亮的方块：
    /// 那样能拿到<strong>键盘焦点、空格切换、读屏器认得</strong>这三件事 —— 自己画只能拿到外观。
    /// <para><strong>轨道必须画在自己的 Border 上，不能设成控件的 Background</strong>：
    /// SimpleTheme 的 ToggleButton 模板对 <c>:checked</c> 有它自己的底色 setter，
    /// 实测会把我们赋的蓝色盖掉，开着的开关显示成模板灰（规范 §二那条"应用层样式压不过
    /// ControlTheme"在这里以另一种形式复现）。控件本体的底色一律透明，
    /// 视觉全部由 Content 里那颗自绘的圆角轨道承担。</para>
    /// </summary>
    public static ToggleButton ToggleSwitch(this ToggleButton toggle)
    {
        const double trackWidth = 40;
        const double trackHeight = 22;
        const double knob = 16;

        var dot = new Border
        {
            Width = knob,
            Height = knob,
            CornerRadius = Design.Radius.Pill,
            Background = Design.Toggle.Knob,
            BoxShadow = Design.Shadow.Pill,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 3, 0),
        };

        var track = new Border
        {
            Width = trackWidth,
            Height = trackHeight,
            CornerRadius = Design.Radius.Pill,
            Child = new Panel { Children = { dot } },
        };

        toggle.Width = trackWidth;
        toggle.Height = trackHeight;
        toggle.Padding = new Thickness(0);
        toggle.BorderThickness = new Thickness(0);
        toggle.Background = Brushes.Transparent;
        toggle.CornerRadius = Design.Radius.Pill;
        toggle.VerticalAlignment = VerticalAlignment.Center;
        toggle.HorizontalAlignment = HorizontalAlignment.Right;
        toggle.Content = track;
        toggle.Cursor = new Cursor(StandardCursorType.Hand);

        void Paint()
        {
            var on = toggle.IsChecked == true;
            track.Background = on ? Design.Toggle.TrackOn : Design.Toggle.TrackOff;
            dot.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        Paint();
        toggle.IsCheckedChanged += (_, _) => Paint();
        return toggle;
    }

    /// <summary>带文字标签的开关行：左边说明、右边开关。标签同时挂成自动化名，读屏才说得出这一行是什么。</summary>
    public static ToggleButton ToggleSwitch(this ToggleButton toggle, string label)
    {
        toggle.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, label);
        return toggle.ToggleSwitch();
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
