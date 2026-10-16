using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Media;

namespace Andzip.App;

/// <summary>
/// 界面设计令牌：颜色、圆角、间距、字号的<strong>唯一</strong>来源。
/// <para>规范见 <c>docs/界面设计规范.md</c>；那边的每个数值都能在这里找到，
/// 反过来也成立 —— 界面上不该出现任何一处字面色值或"看着合适"的边距。
/// 这条由界面测试钉住（<c>DesignTokenTests</c>），因为上一轮"规则只写在文档里"
/// 就出过 <c>0.13.0</c> 那种事。</para>
/// <para>为什么是 C# 常量而不是资源字典：本工程没有 XAML，控件全在代码里 new，
/// 资源字典只会变成第二套没人同步的真相。</para>
/// <para><strong>颜色为什么是属性而不是字段，而且画刷为什么全进程只有一份</strong>：
/// 没有 XAML 就意味着每个控件的颜色都是在构造那一刻从这里的属性<em>抄一份引用</em>到自己身上
/// （<c>button.Background = Design.Surface.Base;</c>）。抄来的引用要能跟着主题走，
/// 被引用的那一份就<strong>不能换</strong> —— 只能改它自己的颜色。
/// 所以浅深两套调色板<strong>共用同一批 <see cref="SolidColorBrush"/> 实例</strong>，
/// <see cref="Use"/> 换档时逐个改写它们的 <c>Color</c>；<see cref="Active"/> 换的是
/// <em>颜色表</em>，不是画刷。</para>
/// <para>这条是 1.4.0 那条「不许把 Design 的画刷抄进静态字段」（<c>DesignTokenTests</c>）的续集：
/// 那条只挡住了「字段里再存一份」，挡不住「控件实例在构造时抓一次」。
/// 用户 2026-10-16 的话是「每当我切换模式的时候，颜色都会不正常，但是重新打开还是正常的」——
/// 重新打开正常、切换后不正常，说的就是<strong>有一批颜色停在建窗口那一次的调色板上</strong>：
/// 深色下白底浅字的菜单弹窗（<c>Chrome.RoundPopups</c> 第一次展开写下的浅色局部值）、
/// 以及任何没被 <c>MainWindow.ApplyTheme</c> 重做一遍的窗口。画刷做成活的那一秒，
/// 这两类一起消失：抓一次与抓每次是同一件事。</para>
/// <para><strong>仍然跟着构造走的是 <see cref="Shadow"/></strong>：<c>BoxShadows</c> 是结构体，
/// 没有"活的实例"可以改，所以换档后已建好的控件留着旧的那档影子（差别只有不透明度，
/// 深色下更不明显）。位图画出来的图标同理 —— 那一路靠 <c>ApplyTheme</c> 重做。</para>
/// </summary>
internal static class Design
{
    /// <summary>当前生效的调色板。所有颜色属性都是从它转发出去的。</summary>
    public static Palette Active { get; private set; } = Palette.Light;

    /// <summary>用户的偏好（"跟随系统"与"当前是深色"是两件事，前者在这里）。</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    /// <summary>调色板换了。已经建好的控件不会自己重画，听到这个事件的一方要重建自己那部分界面。</summary>
    public static event Action? ActiveChanged;

    /// <summary>
    /// 按偏好把界面切到浅色/深色：既翻我们自己的调色板，也告诉 Avalonia 要哪个主题变体。
    /// <para>两件事都要做。只翻调色板，SimpleTheme 自己画的那一小半（勾选框内部、下拉列表、
    /// 提示气泡）还停在浅色；只改变体，我们的表面色一点不动 —— 实测 SimpleTheme 并没有深色
    /// 表面色，所以深浅主要靠前者，后者是补齐。</para>
    /// </summary>
    public static void Apply(ThemeMode mode)
    {
        Mode = mode;

        var app = Avalonia.Application.Current;
        if (app is not null)
        {
            app.RequestedThemeVariant = mode switch
            {
                ThemeMode.Light => Avalonia.Styling.ThemeVariant.Light,
                ThemeMode.Dark => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }

        // 系统读数只能在 RequestedThemeVariant 是 Default 时才代表系统；
        // 显式选了浅/深时它代表的是"我们自己的选择"，但那不影响结果 —— 那时 mode 已经决定了深浅。
        Use(mode.PrefersDark(app?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark));
    }

    /// <summary>切到某个主题；没变化时不触发事件（避免设置页每次刷新都把界面重建一遍）。</summary>
    public static void Use(bool dark)
    {
        var next = dark ? Palette.Dark : Palette.Light;
        if (ReferenceEquals(next, Active))
        {
            return;
        }

        Active = next;

        // 先改色再广播：听到事件的一方（MainWindow.ApplyTheme）会现读这些画刷，
        // 顺序反过来它读到的是上一档。
        Brand.Use(next);
        Ink.Use(next);
        Surface.Use(next);
        Line.Use(next);
        Toggle.Use(next);
        Semantic.Use(next);
        Icon.Use(next);

        ActiveChanged?.Invoke();
    }

    /// <summary>品牌蓝。工具栏带、主按钮、选中态、链接。</summary>
    internal static class Brand
    {
        static readonly SolidColorBrush
            _tint = new(Palette.Light.BrandTint),
            _soft = new(Palette.Light.BrandSoft),
            _line = new(Palette.Light.BrandLine),
            _bright = new(Palette.Light.BrandBright),
            _base = new(Palette.Light.BrandBase),
            _deep = new(Palette.Light.BrandDeep),
            _hover = new(Palette.Light.BrandHover),
            _onHover = new(Palette.Light.OnBrandHover),
            _onPressed = new(Palette.Light.OnBrandPressed);

        public static IBrush Tint => _tint;
        public static IBrush Soft => _soft;
        public static IBrush Line => _line;
        public static IBrush Bright => _bright;
        public static IBrush Base => _base;
        public static IBrush Deep => _deep;
        public static IBrush Hover => _hover;

        /// <summary>叠在品牌蓝带上的半透明白：悬停 12%、按下 20%。两套主题下都用白叠加。</summary>
        public static IBrush OnBrandHover => _onHover;

        public static IBrush OnBrandPressed => _onPressed;

        internal static void Use(Palette p)
        {
            _tint.Color = p.BrandTint;
            _soft.Color = p.BrandSoft;
            _line.Color = p.BrandLine;
            _bright.Color = p.BrandBright;
            _base.Color = p.BrandBase;
            _deep.Color = p.BrandDeep;
            _hover.Color = p.BrandHover;
            _onHover.Color = p.OnBrandHover;
            _onPressed.Color = p.OnBrandPressed;
        }
    }

    /// <summary>文字与描边的中性色。</summary>
    internal static class Ink
    {
        static readonly SolidColorBrush
            _primary = new(Palette.Light.InkPrimary),
            _secondary = new(Palette.Light.InkSecondary),
            _disabled = new(Palette.Light.InkDisabled),
            _onBrand = new(Palette.Light.InkOnBrand),
            _onBrandDisabled = new(Palette.Light.InkOnBrandDisabled);

        public static IBrush Primary => _primary;
        public static IBrush Secondary => _secondary;
        public static IBrush Disabled => _disabled;
        public static IBrush OnBrand => _onBrand;
        public static IBrush OnBrandDisabled => _onBrandDisabled;

        internal static void Use(Palette p)
        {
            _primary.Color = p.InkPrimary;
            _secondary.Color = p.InkSecondary;
            _disabled.Color = p.InkDisabled;
            _onBrand.Color = p.InkOnBrand;
            _onBrandDisabled.Color = p.InkOnBrandDisabled;
        }
    }

    /// <summary>
    /// 面板底色。1.7.0 起<strong>整块窗口就是一张表面</strong>（<see cref="Base"/>），
    /// 内部不再切二级卡片 —— 用户对着"外壳卡 + 左卡 + 右卡 + 底栏卡"的评价是割裂。
    /// 分层改由<strong>外阴影 + 一圈发丝边 + 内部 1px 线</strong>承担
    /// （<c>ShadowCapabilityTests</c> 量过 <c>Border.BoxShadow</c> 真的画得出来）。
    /// <see cref="Muted"/> 只用于"压一档"的小块：悬停、表头、标题栏按钮。
    /// </summary>
    internal static class Surface
    {
        static readonly SolidColorBrush
            _base = new(Palette.Light.SurfaceBase),
            _muted = new(Palette.Light.SurfaceMuted),
            _sunken = new(Palette.Light.SurfaceSunken),
            _pressed = new(Palette.Light.SurfacePressed);

        public static IBrush Base => _base;
        public static IBrush Muted => _muted;
        public static IBrush Sunken => _sunken;

        /// <summary>按下那一档（比悬停再沉一点）。只给"自己画状态"的控件用，
        /// 别让 SimpleTheme 的 <c>:pressed</c> 替我们决定 —— 它给的是深灰块。</summary>
        public static IBrush Pressed => _pressed;

        internal static void Use(Palette p)
        {
            _base.Color = p.SurfaceBase;
            _muted.Color = p.SurfaceMuted;
            _sunken.Color = p.SurfaceSunken;
            _pressed.Color = p.SurfacePressed;
        }
    }

    internal static class Line
    {
        static readonly SolidColorBrush
            _hair = new(Palette.Light.LineHair),
            _subtle = new(Palette.Light.LineSubtle),
            _strong = new(Palette.Light.LineStrong);

        /// <summary>最淡的一档：面板内部分区用的发丝线（树与列表之间、内容区与底栏之间）。</summary>
        public static IBrush Hair => _hair;

        public static IBrush Subtle => _subtle;
        public static IBrush Strong => _strong;

        internal static void Use(Palette p)
        {
            _hair.Color = p.LineHair;
            _subtle.Color = p.LineSubtle;
            _strong.Color = p.LineStrong;
        }
    }

    /// <summary>
    /// 开关（Toggle）的三段色。设置页的布尔项用它而不是勾选框 —— 参考风格就是开关，
    /// 而且开关的"开/关"是位置语义，比一个勾更贴近"这是程序的某个行为开关"。
    /// </summary>
    internal static class Toggle
    {
        static readonly SolidColorBrush
            _trackOff = new(Palette.Light.ToggleTrackOff),
            _trackOn = new(Palette.Light.ToggleTrackOn),
            _knob = new(Palette.Light.ToggleKnob);

        public static IBrush TrackOff => _trackOff;
        public static IBrush TrackOn => _trackOn;
        public static IBrush Knob => _knob;

        internal static void Use(Palette p)
        {
            _trackOff.Color = p.ToggleTrackOff;
            _trackOn.Color = p.ToggleTrackOn;
            _knob.Color = p.ToggleKnob;
        }
    }

    /// <summary>
    /// 三档阴影（规范 4.4）。每一档都是<strong>两层</strong>：一层紧贴的接触影（小而实）
    /// + 一层弥散的环境影（大而虚）—— 单层大模糊会糊成一片灰雾，看着脏；
    /// 两层才有"浮起来"而不是"变暗了"的感觉。
    /// <para>深色下阴影几乎不可见（底本来就黑），所以那一份只把不透明度提高一档，
    /// 真正管用的是 <c>Line.Subtle</c> 那圈发丝边。</para>
    /// <para><strong>这一档不跟着换档走</strong>：<c>BoxShadows</c> 是结构体，控件身上存的是值不是引用，
    /// 没有"活的实例"可以改写颜色。所以换主题后已经建好的控件留着旧的那档影子，
    /// 要一起换只能重做控件（<c>MainWindow.ApplyTheme</c> 对工具栏与图标就是这么办的）。
    /// 两档影子几何完全相同、只差一点不透明度，深色下几乎看不出，所以这一处留白是量过的取舍，
    /// 不是漏的。</para>
    /// </summary>
    internal static class Shadow
    {
        /// <summary>整块窗口面板浮在桌面上：主窗口、设置窗口、每个对话框。</summary>
        public static BoxShadows Floating => Active.ShadowFloating;

        /// <summary>
        /// 一块"能被指到"的小面浮在面板上：悬停药丸、弹出的菜单与下拉列表。
        /// <para>比 <see cref="Floating"/> 收得很紧 —— 这些东西只有几十像素高，
        /// 用整窗那档大弥散影会在旁边糊出一圈灰雾，看着像脏了而不是浮起来。</para>
        /// </summary>
        public static BoxShadows Lift => Active.ShadowLift;

        /// <summary>小件：开关的滑块。</summary>
        public static BoxShadows Pill => Active.ShadowPill;
    }

    /// <summary>语义色。规范里写着"不许当装饰"：红只用于错误/危险，绿只用于成功。</summary>
    internal static class Semantic
    {
        static readonly SolidColorBrush
            _danger = new(Palette.Light.Danger),
            _dangerTint = new(Palette.Light.DangerTint),
            _success = new(Palette.Light.Success),
            _warningText = new(Palette.Light.WarningText),
            _warningTint = new(Palette.Light.WarningTint),
            _warningIcon = new(Palette.Light.WarningIcon);

        public static IBrush Danger => _danger;
        public static IBrush DangerTint => _dangerTint;
        public static IBrush Success => _success;
        public static IBrush WarningText => _warningText;
        public static IBrush WarningTint => _warningTint;
        public static IBrush WarningIcon => _warningIcon;

        internal static void Use(Palette p)
        {
            _danger.Color = p.Danger;
            _dangerTint.Color = p.DangerTint;
            _success.Color = p.Success;
            _warningText.Color = p.WarningText;
            _warningTint.Color = p.WarningTint;
            _warningIcon.Color = p.WarningIcon;
        }
    }

    /// <summary>图标的面与边。内容区唯一允许填充的图标是文件夹；工具栏那九枚双色图形见 <c>Icon.Accent</c>。</summary>
    internal static class Icon
    {
        static readonly SolidColorBrush
            _face = new(Palette.Light.FolderFace),
            _edge = new(Palette.Light.FolderEdge),
            _glyph = new(Palette.Light.IconGlyph),
            _accent = new(Palette.Light.IconAccent);

        public static IBrush Face => _face;
        public static IBrush Edge => _edge;
        public static IBrush Glyph => _glyph;

        /// <summary>
        /// 双色图标的第二档：同一枚图形里"次要的那一层"（盒盖、折角）。
        /// <para>工具栏那九枚来自 <c>assets/svg/</c>，原稿用 <c>#414B5A</c> + <c>#A7B1BF</c>
        /// 两档灰。深色下面 <c>#414B5A</c> 会直接融进卡片，所以这里不是照抄两个色，
        /// 而是把"亮一档 / 暗一档"的关系按调色板各算一遍 —— 关系保住，绝对值让给主题。</para>
        /// </summary>
        public static IBrush Accent => _accent;

        internal static void Use(Palette p)
        {
            _face.Color = p.FolderFace;
            _edge.Color = p.FolderEdge;
            _glyph.Color = p.IconGlyph;
            _accent.Color = p.IconAccent;
        }
    }

    /// <summary>圆角只有这 6 档（规范 4.1）。与主题无关，所以是常量。</summary>
    internal static class Radius
    {
        public static readonly CornerRadius None = new(0);
        public static readonly CornerRadius Xs = new(3);
        public static readonly CornerRadius Sm = new(6);
        public static readonly CornerRadius Md = new(8);
        public static readonly CornerRadius Lg = new(12);

        /// <summary>
        /// 整块窗口那张面板的圆角。单独一档而不是复用 <see cref="Lg"/>：
        /// 面板是全屏最大的一块，同一半径在它上面读起来比在小卡片上小得多 ——
        /// 1.8.0 之前用 16，用户的评价是角落"不自然"（量出来参考图约 22）。
        /// </summary>
        public static readonly CornerRadius Panel = new(22);
        public static readonly CornerRadius Pill = new(999);
    }

    /// <summary>
    /// 面板浮起来占的那一圈空间。
    /// <para><strong>这个数必须不小于 <see cref="Shadow"/> 能伸出去的最远距离</strong>，
    /// 否则阴影会被窗口的边界切掉一刀 —— 切痕在四个角上最明显，看起来就是"不自然"。
    /// 这条由 <c>HoverAndPopupTests</c> 用 alpha 通道钉住（窗口最外一圈必须全透明）。</para>
    /// </summary>
    internal static class Float
    {
        public const double Margin = Space.X5;
    }

    /// <summary>4px 栅格（规范 4.2）。要边距请从这里取，不要现场写数字。</summary>
    internal static class Space
    {
        public const int X1 = 4;
        public const int X2 = 8;
        public const int X3 = 12;
        public const int X4 = 16;
        public const int X5 = 20;
        public const int X6 = 24;
        public const int X8 = 32;

        public static Thickness All(int units) => new(units);
        public static Thickness All(int horizontal, int vertical) => new(horizontal, vertical);
    }

    /// <summary>字号与字重（规范 4.3）。</summary>
    internal static class Type
    {
        public const double Caption = 12;
        public const double Body = 14;
        public const double Subtitle = 16;
        public const double Title = 20;

        public static readonly FontWeight Semibold = FontWeight.SemiBold;

        /// <summary>正文行高：14px 取 20（约 1.4 倍）。</summary>
        public const double BodyLineHeight = 20;
    }

    /// <summary>
    /// 字体族。Win11 拿 Segoe UI Variable，Win10 回退 Segoe UI，
    /// 中文再回退到雅黑 UI —— 宋体在 12px 下会糊。
    /// </summary>
    public static readonly FontFamily Font =
        FontFamily.Parse("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, sans-serif");

    /// <summary>图标尺寸档位（规范 6.3）。非这几档不许出现。</summary>
    internal static class IconSize
    {
        public const double Row = 14;
        public const double Inline = 16;
        public const double Dialog = 20;
        public const double ToolBar = 40;
        public const double Tile = 72;
    }
}

/// <summary>
/// 一套完整的颜色表：34 个颜色 + 3 档阴影，一个都不能少 —— 少一个就是深色模式下漏出一块浅色。
/// <para>深色那一份刻意<strong>不用纯黑</strong>：画布停在 <c>#14171A</c>、卡片 <c>#1F2328</c>，
/// 纯黑配高对比文字会在暗环境里产生"光晕"，眼睛要不停重新对焦。
/// 品牌蓝在深色下反而要<strong>压深</strong>（<c>#1B5E9E</c>）而不是提亮：
/// 它是白字的底，提亮会把白字的对比度打到 AA 以下。</para>
/// <para>每个"文字色 / 底色"配对的对比度由 <c>PaletteContrastTests</c> 按 WCAG 公式实算，
/// 规范表里那些数字就是那条测试报出来的。</para>
/// </summary>
internal sealed class Palette
{
    private Palette(string name) => Name = name;

    public string Name { get; }

    public required Color BrandTint { get; init; }
    public required Color BrandSoft { get; init; }
    public required Color BrandLine { get; init; }
    public required Color BrandBright { get; init; }
    public required Color BrandBase { get; init; }
    public required Color BrandDeep { get; init; }
    public required Color BrandHover { get; init; }
    public required Color OnBrandHover { get; init; }
    public required Color OnBrandPressed { get; init; }

    public required Color InkPrimary { get; init; }
    public required Color InkSecondary { get; init; }
    public required Color InkDisabled { get; init; }
    public required Color InkOnBrand { get; init; }
    public required Color InkOnBrandDisabled { get; init; }

    public required Color SurfaceBase { get; init; }
    public required Color SurfaceMuted { get; init; }
    public required Color SurfaceSunken { get; init; }

    /// <summary>按下去那一档：比 <see cref="SurfaceMuted"/> 再沉一点。
    /// 设置页导航项用它 —— 那一处不能让 SimpleTheme 自己画 <c>:pressed</c>，
    /// 它给的是 <c>#686868</c> 那种深灰块，压在浅底上像按坏了（1.8.7，用户截图）。</summary>
    public required Color SurfacePressed { get; init; }

    public required Color LineHair { get; init; }
    public required Color LineSubtle { get; init; }
    public required Color LineStrong { get; init; }

    public required Color ToggleTrackOff { get; init; }
    public required Color ToggleTrackOn { get; init; }
    public required Color ToggleKnob { get; init; }

    public required BoxShadows ShadowFloating { get; init; }
    public required BoxShadows ShadowLift { get; init; }
    public required BoxShadows ShadowPill { get; init; }

    public required Color Danger { get; init; }
    public required Color DangerTint { get; init; }
    public required Color Success { get; init; }
    public required Color WarningText { get; init; }
    public required Color WarningTint { get; init; }
    public required Color WarningIcon { get; init; }

    public required Color FolderFace { get; init; }
    public required Color FolderEdge { get; init; }
    public required Color IconGlyph { get; init; }

    /// <summary>双色图标的第二档（盒盖 / 折角），永远与 <see cref="IconGlyph"/> 差一档明度。</summary>
    public required Color IconAccent { get; init; }

    public static Palette Light { get; } = new("light")
    {
        BrandTint = Rgb(0xF2, 0xF7, 0xFD),
        BrandSoft = Rgb(0xE1, 0xEE, 0xFB),
        BrandLine = Rgb(0xBF, 0xD9, 0xF4),
        BrandBright = Rgb(0x2E, 0x86, 0xD6),
        BrandBase = Rgb(0x0F, 0x6C, 0xBD),
        BrandDeep = Rgb(0x0C, 0x5A, 0xA0),
        BrandHover = Rgb(0x4A, 0x9B, 0xE8),
        OnBrandHover = Argb(0x1F, 0xFF, 0xFF, 0xFF),
        OnBrandPressed = Argb(0x33, 0xFF, 0xFF, 0xFF),

        InkPrimary = Rgb(0x1F, 0x23, 0x28),
        InkSecondary = Rgb(0x5B, 0x64, 0x70),
        InkDisabled = Rgb(0x9A, 0xA3, 0xAE),
        InkOnBrand = Rgb(0xFF, 0xFF, 0xFF),
        InkOnBrandDisabled = Argb(0x99, 0xFF, 0xFF, 0xFF),

        SurfaceBase = Rgb(0xFF, 0xFF, 0xFF),
        SurfaceMuted = Rgb(0xF6, 0xF8, 0xFA),
        SurfaceSunken = Rgb(0xFA, 0xFB, 0xFC),
        SurfacePressed = Rgb(0xE9, 0xED, 0xF1),

        LineHair = Rgb(0xF0, 0xF2, 0xF5),
        LineSubtle = Rgb(0xE6, 0xE9, 0xEC),
        LineStrong = Rgb(0xC9, 0xCE, 0xD6),

        ToggleTrackOff = Rgb(0xD6, 0xDC, 0xE4),
        ToggleTrackOn = Rgb(0x0F, 0x6C, 0xBD),
        ToggleKnob = Rgb(0xFF, 0xFF, 0xFF),

        // 影子的颜色不是黑的，是品牌深墨蓝：灰黑影子在白底上发脏，带一点冷的墨色才干净。
        // 参考图（用户给的那张）量出来是"边缘外 2-4px 约 9% 暗，13px 内衰减干净"，
        // 而且**没有描边** —— 所以这里只有一层向下偏移的软影，加一层几乎看不见的接触影。
        // 之前那种"紧贴边缘的实影 + 1px 灰描边"投不出任何真实的光，读起来就是画上去的一圈线。
        // 距离必须整个装得进 Float.Margin，否则窗口边界会切掉影子（角上最明显）。
        // 中间那一层<strong>不偏移、带 1px 扩散</strong>：只有向下偏移的两层时，顶边只摊到 2px 的
        // 模糊余量，压在浅色桌面上就是"窗口和背景分不清"（用户的话：顶部也要有阴影）。
        // 同一件事 1.8.0 在药丸上就犯过（下面 ShadowLift 那条注释），只是这次犯在整块面板上。
        ShadowFloating = Shadows("0 1 3 0 #120B1E33, 0 0 6 1 #1F0B1E33, 0 6 14 0 #1F0B1E33"),
        // 两层分工明确：第二层往下方弥散（重量），第一层不偏移但带 1px 扩散（四周那一圈接触影）。
        // 只有下方偏移的那一层，药丸<strong>上方</strong>读不出浮起 —— 实测差值只有 1/255，
        // 用户要的"带阴影的圆角框"必须一眼看得见。
        ShadowLift = Shadows("0 2 10 0 #240B1E33, 0 0 3 1 #1F0B1E33"),
        ShadowPill = Shadows("0 1 2 0 #1A0B1E33"),

        Danger = Rgb(0xC4, 0x2B, 0x1C),
        DangerTint = Rgb(0xFD, 0xEC, 0xEA),
        Success = Rgb(0x1A, 0x7F, 0x37),
        WarningText = Rgb(0x8A, 0x61, 0x00),
        WarningTint = Rgb(0xFF, 0xF6, 0xDE),
        WarningIcon = Rgb(0xD9, 0xA6, 0x1F),

        FolderFace = Rgb(0xF7, 0xC9, 0x48),
        FolderEdge = Rgb(0xD9, 0xA6, 0x1F),
        IconGlyph = Rgb(0x5B, 0x64, 0x70),
        IconAccent = Rgb(0xA7, 0xB1, 0xBF),
    };

    public static Palette Dark { get; } = new("dark")
    {
        // 选中/信息类的蓝色要能压住深底，所以比浅色那份更亮一点；
        // 而"带子/主按钮"那种白字底反而要更深 —— 两者不是一回事。
        BrandTint = Rgb(0x17, 0x29, 0x3B),
        BrandSoft = Rgb(0x1E, 0x3A, 0x52),
        BrandLine = Rgb(0x2F, 0x5C, 0x86),
        BrandBright = Rgb(0x5C, 0xA8, 0xE8),
        BrandBase = Rgb(0x1B, 0x5E, 0x9E),
        BrandDeep = Rgb(0x14, 0x4A, 0x80),
        BrandHover = Rgb(0x3D, 0x86, 0xC8),
        OnBrandHover = Argb(0x26, 0xFF, 0xFF, 0xFF),
        OnBrandPressed = Argb(0x3D, 0xFF, 0xFF, 0xFF),

        InkPrimary = Rgb(0xE7, 0xEB, 0xF0),
        InkSecondary = Rgb(0xA7, 0xB0, 0xBA),
        InkDisabled = Rgb(0x6B, 0x74, 0x80),
        InkOnBrand = Rgb(0xFF, 0xFF, 0xFF),
        InkOnBrandDisabled = Argb(0x8C, 0xFF, 0xFF, 0xFF),

        SurfaceBase = Rgb(0x1F, 0x23, 0x28),
        SurfaceMuted = Rgb(0x26, 0x2B, 0x31),
        SurfaceSunken = Rgb(0x1A, 0x1E, 0x22),
        SurfacePressed = Rgb(0x30, 0x37, 0x3F),

        LineHair = Rgb(0x27, 0x2C, 0x32),
        LineSubtle = Rgb(0x2E, 0x34, 0x3A),
        LineStrong = Rgb(0x45, 0x4D, 0x56),

        // 关着的轨道要压得够暗，否则与开着的蓝分不出两态（PaletteContrastTests 卡 3:1）。
        ToggleTrackOff = Rgb(0x2E, 0x35, 0x3D),
        ToggleTrackOn = Rgb(0x3D, 0x86, 0xC8),
        ToggleKnob = Rgb(0xE7, 0xEB, 0xF0),

        // 同一套几何，不透明度提高一档：纯黑上的黑影子只有加深边缘这一种作用，太淡等于没有。
        ShadowFloating = Shadows("0 1 3 0 #33000000, 0 6 14 0 #59000000"),
        ShadowLift = Shadows("0 2 10 0 #73000000, 0 0 3 1 #59000000"),
        ShadowPill = Shadows("0 1 2 0 #66000000"),

        Danger = Rgb(0xF2, 0x8B, 0x82),
        DangerTint = Rgb(0x3A, 0x1F, 0x1D),
        Success = Rgb(0x7F, 0xDC, 0xA4),
        WarningText = Rgb(0xF5, 0xD2, 0x7A),
        WarningTint = Rgb(0x3A, 0x31, 0x17),
        WarningIcon = Rgb(0xE8, 0xB9, 0x3C),

        FolderFace = Rgb(0xD9, 0xA6, 0x1F),
        FolderEdge = Rgb(0x8A, 0x61, 0x00),
        IconGlyph = Rgb(0xA7, 0xB0, 0xBA),
        IconAccent = Rgb(0x6E, 0x76, 0x81),
    };

    private static Color Rgb(int r, int g, int b) => Color.FromRgb((byte)r, (byte)g, (byte)b);

    private static Color Argb(int a, int r, int g, int b) => Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);

    /// <summary>阴影只允许在这里写死：CSS 那串"偏移 模糊 扩散 颜色"散到视图代码里就没法统一调档了。</summary>
    private static BoxShadows Shadows(string spec) => BoxShadows.Parse(spec);
}
