using Andzip.App.Views;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;

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
    /// 提示气泡（ToolTip）换成自己搭的那层圆角阴影外壳。
    /// <para>为什么单独处理：菜单与下拉列表的弹窗根是一枚 <c>Border</c>（见 <see cref="RoundPopups"/>），
    /// 而 SimpleTheme 的 ToolTip 模板里<strong>只有一枚 ContentPresenter</strong>，没有可改的 Border ——
    /// 换掉模板是唯一能给它圆角与阴影的办法。模板是实例级的本地值，压得过 ControlTheme
    /// （规范 §二 那条"应用层样式压不过 ControlTheme"说的是伪类 setter，不是模板）。</para>
    /// </summary>
    public static void InstallBubbles(Application app)
    {
        app.Styles.Add(new Style(x => x.OfType<ToolTip>())
        {
            Setters =
            {
                new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ToolTip>((_, _) => new Border
                {
                    // 外面这层是透明的留白：弹窗宿主按内容尺寸裁剪，影子必须有地方画
                    Background = Brushes.Transparent,
                    Padding = new Thickness(LiftRoom),
                    Child = new Border
                    {
                        CornerRadius = Design.Radius.Md,
                        Background = Design.Surface.Base,
                        BorderBrush = Design.Line.Subtle,
                        BorderThickness = new Thickness(1),
                        BoxShadow = Design.Shadow.Lift,
                        Padding = new Thickness(Design.Space.X2, Design.Space.X1, Design.Space.X2, Design.Space.X1),
                        // 模板里的 ContentPresenter 会自动接住 TemplatedParent 的 Content，
                        // 所以这里不用写 Binding —— 写了反而在换内容时不更新。
                        Child = new ContentPresenter(),
                    },
                })),
            },
        });
    }

    /// <summary>
    /// 悬停时浮起的一颗圆角药丸：底色压深一档 + 一层收得很紧的软阴影。
    /// <para><strong>为什么要在控件底下垫一枚 Border</strong>：SimpleTheme 的模板控件
    /// （Button / ComboBox / ToggleButton）根本没有 <c>BoxShadow</c> 属性，阴影只能画在
    /// 自己搭的 <c>Border</c> 上（规范 §二 的实测表）。所以这里把控件包进一层 Panel：
    /// 药丸在下面一层、控件在上面一层，控件自己的底色留透明，看见的灰与影都来自药丸。</para>
    /// <para>Panel 的尺寸由控件决定（药丸自己不带内容），所以包一层不会改变布局；
    /// 药丸的 <c>Margin</c> 只让它比控件小一圈 —— 标题栏那种"格子 46×40、药丸 36×30"
    /// 的效果就是这么来的，命中区一点没缩。</para>
    /// </summary>
    public static Control Lift(this Control control, Thickness inset, CornerRadius radius)
    {
        var pill = new Border
        {
            Margin = inset,
            CornerRadius = radius,
            Background = Brushes.Transparent,
            // 指针要落到控件身上，不然悬停永远不触发（药丸与控件重叠，谁在上面都得让路）
            IsHitTestVisible = false,
        };

        void Paint(IBrush face, BoxShadows shadow)
        {
            pill.Background = face;
            pill.BoxShadow = shadow;
        }

        control.PointerEntered += (_, _) => Paint(Design.Surface.Muted, Design.Shadow.Lift);
        control.PointerExited += (_, _) => Paint(Brushes.Transparent, default);
        control.PointerPressed += (_, _) => Paint(Design.Line.Subtle, Design.Shadow.Pill);
        control.PointerReleased += (_, _) => Paint(
            control.IsPointerOver ? Design.Surface.Muted : Brushes.Transparent,
            control.IsPointerOver ? Design.Shadow.Lift : default);

        return new Panel { Children = { pill, control } };
    }

    public static Control Lift(this Control control) => control.Lift(new Thickness(0), Design.Radius.Lg);

    /// <summary>
    /// 这一项展开子菜单时，把弹窗修成圆角阴影面板。
    /// <para>为什么要<strong>推迟一帧</strong>：<c>SubmenuOpened</c> 触发的那一刻，弹窗里的
    /// 内容还没建出来（<c>Popup.Child</c> 是打开时才生成的），当场改属性等于改在空气上。
    /// 排到 <c>Render</c> 就落在同一帧的绘制之前，用户看不见中间状态。</para>
    /// <para>为什么逐项挂而不是在 <c>Menu</c> 上收冒泡：<c>SubmenuOpenedEvent</c> 实测
    /// 不冒泡到 Menu（试过，父层收不到），逐项订阅才是可靠的。动态重建的子项（「最近的文件」）
    /// 自己没有下一级，不需要这一层。</para>
    /// </summary>
    public static T RoundOnOpen<T>(this T item) where T : MenuItem
    {
        item.SubmenuOpened += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(item.RoundPopups,
                Avalonia.Threading.DispatcherPriority.Render);
        return item;
    }

    /// <summary>
    /// 菜单栏的悬停：只轻微置灰，不要 SimpleTheme 那层 20% 品牌蓝。
    /// <para>高亮画在模板里的 <c>Border#root</c> 上，由 ControlTheme 的 setter 决定，
    /// 应用层样式压不过它（规范 §二 那条实测）。但<strong>实例上的本地值压得过 setter</strong> ——
    /// 所以进来时把灰色写成局部值，离开时 <c>ClearValue</c> 交还给模板：
    /// 展开子菜单时那一项的高亮仍由 SimpleTheme 自己管，我们不抢它的事。</para>
    /// </summary>
    public static T MenuGrayHover<T>(this T item) where T : MenuItem
    {
        item.PointerEntered += (_, _) =>
        {
            if (Root(item) is not { } root)
            {
                return;
            }

            root.SetValue(Border.BackgroundProperty, Design.Surface.Muted);
            root.SetValue(Border.CornerRadiusProperty, Design.Radius.Sm);
        };
        item.PointerExited += (_, _) =>
        {
            if (Root(item) is { } root)
            {
                root.ClearValue(Border.BackgroundProperty);
                root.ClearValue(Border.CornerRadiusProperty);
            }
        };
        return item;
    }

    /// <summary>模板里那枚负责高亮的 Border（SimpleTheme 给它起的名字就是 root）。</summary>
    private static Border? Root(MenuItem item)
        => item.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "root");

    /// <summary>弹窗四周留给阴影的空白。</summary>
    private const double LiftRoom = 12;

    /// <summary>
    /// 把已经打开的弹窗（子菜单 / 右键菜单 / 下拉列表）修成圆角阴影面板。
    /// <para>实测：SimpleTheme 的菜单弹窗挂在本窗口的 OverlayLayer 上
    /// （<c>OverlayPopupHost</c>），而 <c>Popup.Child</c> 就是一枚普通 <c>Border</c> ——
    /// 直角、白底、1px 灰边。它有自己的 <c>BoxShadow</c>，所以圆角和阴影直接改它就够，
    /// 不必替整个弹窗重写 ControlTheme。</para>
    /// <para><strong>那 12px 的 Margin 不是留白，是影子的活动空间</strong>：宿主
    /// <c>ClipToBounds=true</c>，影子一律画不出宿主矩形（实测把宿主裁剪关掉也没用，
    /// 它自己的矩形就是裁剪框）。给这枚 Border 加 Margin 会让宿主量到的尺寸一起变大，
    /// 影子才有地方落；再把 <c>Popup</c> 的偏移收回同样的距离，面板就还贴在锚点上。</para>
    /// <para>只能在"已经打开"之后调：弹窗的 Child 是打开时才建出来的，所以调用点是
    /// <c>SubmenuOpened</c> / <c>DropDownOpened</c>，不是构造时。</para>
    /// </summary>
    public static void RoundPopups(this Control owner)
    {
        foreach (var popup in owner.GetVisualDescendants().OfType<Popup>())
        {
            // 留白本身就是"处理过了"的记号：Name 之类不能写（控件已经 styled），
            // 而这一层每次展开都会再调一次，不能累加。
            if (popup is not { IsOpen: true } || popup.Child is not Border { Margin: { Left: < LiftRoom } } root)
            {
                continue;
            }

            root.Margin = new Thickness(LiftRoom);
            popup.HorizontalOffset -= LiftRoom;
            popup.VerticalOffset -= LiftRoom;

            root.CornerRadius = Design.Radius.Md;
            root.Background = Design.Surface.Base;
            root.BorderBrush = Design.Line.Subtle;
            root.BorderThickness = new Thickness(1);
            root.BoxShadow = Design.Shadow.Lift;
        }
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
    /// 每个窗口构造时调一次：底色、字体族、默认字号、任务栏图标。
    /// 收成一个方法而不是各写各的，是因为"窗口底色"这种事一旦散开，
    /// 就会出现某个对话框忘了设、还是系统默认灰底的情况 —— 那正是改版要消灭的东西。
    /// <para>1.7.0 起窗口底色是<strong>画布</strong>而不是卡片白：卡片要浮起来，
    /// 底下那层必须比卡片暗一档，否则白上放白只能靠阴影硬撑。</para>
    /// <para>图标也要在这里发一次：不设 <c>Window.Icon</c> 时任务栏拿的是 Avalonia 的默认图标，
    /// 与标题栏那枚、与桌面上那个快捷方式都对不上。</para>
    /// </summary>
    public static T WindowSurface<T>(this T window) where T : Window
    {
        window.Background = Brushes.Transparent;
        window.FontFamily = Design.Font;
        window.FontSize = Design.Type.Body;
        window.Icon = Assets.AppIcon;
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
            Margin = new Thickness(Design.Float.Margin),
            CornerRadius = Design.Radius.Panel,
            Background = Design.Surface.Base,
            // 与主窗口同一件事：不描边。参考图里那张卡片没有任何描边，
            // 边界是"卡片比背景暗/亮一档 + 一层软影"读出来的，画一圈灰线就假了。
            BorderThickness = new Thickness(0),
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

    /// <summary>
    /// 下拉框：与输入框同一套形状，避免"两个控件两种圆角"。
    /// 列表弹出来也要跟着这套语言走 —— 圆角与阴影在打开的那一刻补上（<see cref="RoundPopups"/>）。
    /// </summary>
    public static ComboBox Picker(this ComboBox combo)
    {
        combo.CornerRadius = Design.Radius.Sm;
        combo.MinHeight = 30;
        combo.DropDownOpened += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(combo.RoundPopups,
                Avalonia.Threading.DispatcherPriority.Render);
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
