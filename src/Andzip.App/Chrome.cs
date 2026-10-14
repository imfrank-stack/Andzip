using Andzip.App.Views;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Andzip.App;

/// <summary>
/// 交互状态（悬停 / 按下 / 焦点）。
/// <para>为什么不用应用层样式 <c>Style(x =&gt; x.OfType&lt;Button&gt;().Class(":pointerover"))</c>：
/// 实测压不过 SimpleTheme 内部 <c>ControlTheme</c> 的 setter —— 注入悬停底色后中心像素一个字节都没变
/// （结论钉在 <c>ChromeCapabilityTests</c>）。所以状态由这里驱动，好处是这套东西
/// <strong>可以在 headless 里用真指针输入量出来</strong>（悬停与按住都能复现，见同一组用例）。</para>
/// <para>驱动时机只认 <c>IsPointerOver</c> / <c>IsPressed</c> 这两个<strong>属性</strong>，
/// 不认指针事件 —— 事件跑在置位之前，当场读到的还是旧状态（<see cref="Lift"/> 里那条实测）。</para>
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

        // 状态**现读现画**，而且只由状态自己说话 —— 重画挂在 IsPressed / IsPointerOver 这两个属性上，
        // 不挂在指针事件上。挂在事件上会错两次，都是真机量出来的（1.8.8）：
        //   1) 按下去的那一刻指针被捕获，紧接着还会补一次 PointerExited，
        //      按"哪个事件最后到"决定就把药丸清成了透明 —— 按住瞬间屏幕上是 #FFFFFF；
        //   2) 改读属性之后仍错一次：Button 的 PointerPressed 事件跑在 `IsPressed = true` **之前**
        //      （OnPointerPressed 里先 OnEvent 后置位），当场读还是 false，
        //      按住那一刻只剩悬停那一档 #F6F8FA。等属性变了再读，才拿得到真的那一档。
        void Repaint()
        {
            var pressed = control is Button { IsPressed: true };
            var over = control.IsPointerOver;

            Paint(pressed ? Design.Surface.Pressed : over ? Design.Surface.Muted : Brushes.Transparent,
                pressed ? Design.Shadow.Pill : over ? Design.Shadow.Lift : default);
        }

        control.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsPointerOverProperty || e.Property == Button.IsPressedProperty)
            {
                Repaint();
            }
        };

        // 药丸在底下，控件自己必须一直是透明的 —— 但 SimpleTheme 的 :pressed 会绕过这句话，
        // 直接给模板面皮涂一块 #686868（用户 1.8.8 的截图）。把那份"应当是透明"钉到面皮上。
        if (control is TemplatedControl templated)
        {
            templated.PinFace();
        }

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
    /// 所以要把灰色写成局部值。</para>
    /// <para><strong>展开状态也要一起管</strong>（1.8.3）：只在指针进入时写、离开时 <c>ClearValue</c>
    /// 的话，用户点开菜单、鼠标滑进子菜单之后那一项就退回模板自己的高亮 —— 一块<strong>直角</strong>的
    /// 20% 蓝，正是用户说的"扫过时圆角，点击变成直角"。所以这里画的是
    /// "指针在上面<em>或</em>子菜单开着"，两者都退去才交还给模板。</para>
    /// <para>那枚 Border 缓存一次：子菜单打开时这一项的子树里有整份列表，
    /// 每次指针进出都重走一遍没意义。</para>
    /// </summary>
    public static T MenuGrayHover<T>(this T item) where T : MenuItem
    {
        Border? root = null;

        void Paint()
        {
            if (root is null || root.Parent is null)
            {
                root = Root(item);
            }

            if (root is not { } face)
            {
                return;
            }

            if (item.IsPointerOver || item.IsSubMenuOpen)
            {
                face.SetValue(Border.BackgroundProperty, Design.Surface.Muted);
                face.SetValue(Border.CornerRadiusProperty, Design.Radius.Sm);
            }
            else
            {
                face.ClearValue(Border.BackgroundProperty);
                face.ClearValue(Border.CornerRadiusProperty);
            }
        }

        item.PointerEntered += (_, _) => Paint();
        item.PointerExited += (_, _) => Paint();
        item.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsSubMenuOpenProperty)
            {
                Paint();
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
    /// <para><strong>留白只给右下，不给左上，也不再收 <c>Popup</c> 的偏移</strong>
    /// （1.8.3 修的就是这两件事）。实测：Avalonia 是按<strong>含 margin 的外框</strong>定位弹窗的，
    /// 所以"四周都留 12 再把偏移收回 12"会把整块面板推到锚点身上 ——
    /// 菜单展开时顶部那一排菜单项被弹窗自己的第一行盖住，点上去既不关菜单也传不到菜单项，
    /// 用户看到的是"再点一次卡住"（<c>OpenSubmenuDoesNotStealClicks…</c> 量的是命中栈）。
    /// 只往右下留白之后：面板位置与改版前逐像素相同，影子有地方落，锚点一格也没被盖住。
    /// 光从上方来，左上那两边本来就没有影。</para>
    /// </summary>
    public static void RoundPopups(this Control owner)
    {
        foreach (var popup in owner.GetVisualDescendants().OfType<Popup>())
        {
            // 留白本身就是"处理过了"的记号：Name 之类不能写（控件已经 styled），
            // 而这一层每次展开都会再调一次，不能累加。
            if (popup is not { IsOpen: true } || popup.Child is not Border { Margin: { Right: 0 } } root)
            {
                continue;
            }

            root.Margin = new Thickness(0, 0, LiftRoom, LiftRoom);
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
        return TrackStates(button, Design.Surface.Muted, Design.Surface.Pressed);
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
    /// <param name="onClose">
    /// 右上角那枚叉号要做的事，默认是"关掉这个窗口"。进度窗口把它换成"取消这件事并关掉" ——
    /// 光关窗等于把框藏起来，后台还在搬字节，而用户以为停了。
    /// </param>
    public static void Chromeless(this Window window, LangCatalog lang, Control content,
        bool resizable = false, Action? onClose = null)
    {
        window.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;

        var dock = new DockPanel();
        var bar = new TitleBar(window, lang, resizable, onClose);
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
        // 轨道在自绘的 Border 上，但控件本体那枚面皮仍会被主题的伪类涂到（1.8.8 同一件事）：
        // 按住开关的瞬时整块发黑，从轨道底下透出来。钉成我们那份透明。
        return toggle.PinFace();
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
        // SimpleTheme 给 ComboBox 自带一圈 #888888（实测：屏幕上是 1 DIP 实线），
        // 比输入框那档 Border.Strong 重两级 —— 同一个表单里两种描边正是用户报过三次的那种不和谐。
        combo.BorderBrush = Design.Line.Strong;
        combo.BorderThickness = new Avalonia.Thickness(1);
        combo.MinHeight = 30;
        combo.DropDownOpened += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(combo.RoundPopups,
                Avalonia.Threading.DispatcherPriority.Render);
        // 描边三档同值：下拉框要变的只有底色，按下那一档不再交给主题（1.9.0 之前按下去是
        // 整块 #686868 涂在内嵌箭头的面皮上）。
        return TrackStates(combo, Design.Surface.Muted, Design.Surface.Pressed,
            Design.Line.Strong, Design.Line.Strong);
    }

    /// <summary>
    /// 接住悬停与按下两档。静止值在调用时读一次 ——
    /// 调用方可能刚刚设过（工具栏按钮是 Transparent），不能写死成白。
    /// <para>泛型参数用 <see cref="TemplatedControl"/> 而不是 <c>Button</c>：下拉框也要同一套手感。
    /// 描边那两档默认与静止同值（只有下拉框会传进来：它自带一圈主题给的 <c>#888888</c>，
    /// 换档时描边不该跟着变）。</para>
    /// <para>换档的时机与读法见 <see cref="Lift"/> 那段：只认属性，不认指针事件。</para>
    /// </summary>
    private static T TrackStates<T>(T control, IBrush hover, IBrush pressed,
        IBrush? restBorder = null, IBrush? hoverBorder = null) where T : TemplatedControl
    {
        var rest = control.Background;
        var restLine = restBorder ?? control.BorderBrush;
        var hoverLine = hoverBorder ?? restLine;

        // 同 Lift：由 IsPressed / IsPointerOver 这两个属性触发，当场读这两个状态。
        // 挂在指针事件上会漏掉按住那一档（Button 先发事件后置位），见 Lift 那段注释。
        void Repaint()
        {
            var isPressed = control is Button { IsPressed: true };
            control.Background = isPressed ? pressed : control.IsPointerOver ? hover : rest;
            control.BorderBrush = isPressed || control.IsPointerOver ? hoverLine : restLine;
        }

        control.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsPointerOverProperty || e.Property == Button.IsPressedProperty)
            {
                Repaint();
            }
        };

        return control.PinFace();
    }

    /// <summary>
    /// 把控件自己的底色/描边/圆角<strong>钉到模板里那枚面皮上</strong>，让主题的伪类写不上来。
    /// <para>为什么需要这一层（1.8.8，用户截图：工具栏「打开」按下去整块变 <c>#686868</c>）：
    /// <c>SimpleTheme</c> 的 <c>:pressed</c> 不是设在控件身上，而是设在模板子元素
    /// <c>PART_ContentPresenter</c> 上的一整块深灰。应用层给<strong>控件</strong>写局部值压不过它
    /// （规范 §二第一条那条实测就是这个道理），但给<strong>模板子元素</strong>写局部值压得过 ——
    /// 1.8.3 接管菜单栏高亮用的正是这一招。</para>
    /// <para>每一次都<strong>推迟一帧</strong>（<c>Render</c> 优先级）再抄：调用点往往正处在状态换档的
    /// 中途（<c>TrackStates</c> 自己就是在 <c>PropertyChanged</c> 里改底色的），当场抄会抄到改之前那个值；
    /// 构造期更是连模板都还没有，那时问 <c>PART_ContentPresenter</c> 只有 null。</para>
    /// <para>headless 能量到这件事（1.8.8 补测，纠正早先"复现不了"的判断）：合成按住真的会让裸按钮
    /// 露出那块 <c>#686868</c>，接了这里的则露出 <c>Surface.Pressed</c> —— 两条一起钉在
    /// <c>ChromeCapabilityTests</c>。"漏接一处"这种扫不出像素的，交给
    /// <c>DesignTokenTests.EveryButtonPinsItsTemplateFace</c>。</para>
    /// <para><strong>模板里内嵌的 <c>ToggleButton</c> 一起钉</strong>（1.9.0）：下拉框右边那格箭头
    /// 是模板里另一枚 <c>ToggleButton</c>，主题的伪类涂的是<strong>它自己的</strong>面皮，
    /// 只钉外层那枚够不着（实测按住时那一格里 445 个 <c>#686868</c>）。
    /// 只认 <c>ToggleButton</c> 这一种：外层模板里还会冒出 <c>TextBox</c> 与 <c>Popup</c>，
    /// 把它们的面皮一起钉住会连下拉列表里那些行的主题态也钉死 —— 代价比这一格大。</para>
    /// </summary>
    public static T PinFace<T>(this T control) where T : TemplatedControl
    {
        void Sync()
        {
            Skin(control.GetTemplateChildren().OfType<ContentPresenter>(), control);

            // 内嵌的那枚 ToggleButton 不在直接子元素里（下拉框的箭头埋在模板的 Border 与 Grid 底下），
            // 要往整棵子树里找；而它自己的面皮又只在它自己的直接子元素里 —— 两层各自量各自的。
            // 外层有没有面皮不当下酒菜：下拉框外层那枚 ContentPresenter 埋得更深，等不到它。
            foreach (var inner in control.GetVisualDescendants().OfType<ToggleButton>())
            {
                Skin(inner.GetTemplateChildren().OfType<ContentPresenter>(), inner);
            }
        }

        static bool Skin(IEnumerable<ContentPresenter> faces, TemplatedControl owner)
        {
            if (faces.FirstOrDefault(face => face.Name == "PART_ContentPresenter") is not { } skin)
            {
                return false;
            }

            skin.SetValue(TemplatedControl.BackgroundProperty, owner.Background);
            skin.SetValue(TemplatedControl.BorderBrushProperty, owner.BorderBrush);
            skin.SetValue(TemplatedControl.BorderThicknessProperty, owner.BorderThickness);
            skin.SetValue(TemplatedControl.CornerRadiusProperty, owner.CornerRadius);
            return true;
        }

        void Later() => Dispatcher.UIThread.Post(Sync, DispatcherPriority.Render);

        // 兜底那一次：附件时排的那发可能赶在模板应用之前（那时问面皮只有 null，抄了等于没抄）。
        // 什么时候一定在模板之后？<c>Bounds</c> 第一次非零 —— 布局之前 <c>OnApplyTemplate</c> 一定跑过。
        // 所以这里在首次量到尺寸时再补抄一发，只补一发（换大小不再来）。
        // headless 里这一发从来用不上（同步就把模板建好了），所以它测不到 —— 它是给真机买的保险。
        var retried = false;

        control.PropertyChanged += (_, e) =>
        {
            if (!retried && e.Property == Layoutable.BoundsProperty && control.Bounds.Width > 0)
            {
                retried = true;
                Later();
            }
        };

        control.AttachedToVisualTree += (_, _) => Later();
        control.ActualThemeVariantChanged += (_, _) => Later();
        control.PropertyChanged += (_, e) =>
        {
            if (e.Property == TemplatedControl.BackgroundProperty || e.Property == TemplatedControl.BorderBrushProperty
                || e.Property == TemplatedControl.BorderThicknessProperty || e.Property == TemplatedControl.CornerRadiusProperty)
            {
                Later();
            }
        };

        // 不需要另外接按下/抬起：伪类改的是 IsPressed，而它的下一跳就是上面这四个属性之一
        // （TrackStates / Lift 把那一档翻译成底色），四个属性的 PropertyChanged 已经把每一次换档都接住了。
        return control;
    }

    private static IBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
}
