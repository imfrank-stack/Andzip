using Avalonia;
using Avalonia.Media;

namespace Andzip.App;

/// <summary>
/// 界面设计令牌：颜色、圆角、间距、字号的<strong>唯一</strong>来源。
/// <para>规范见 <c>docs/界面设计规范.md</c>；那边的每个数值都能在这里找到，
/// 反过来也成立 —— 界面上不该出现任何一处字面色值或"看着合适"的边距。
/// 这条由界面测试钉住（<c>ThemeTokenTests</c>），因为上一轮"规则只写在文档里"
/// 就出过 <c>0.13.0</c> 那种事。</para>
/// <para>为什么是 C# 常量而不是资源字典：本工程没有 XAML，控件全在代码里 new，
/// 资源字典只会变成第二套没人同步的真相。</para>
/// </summary>
internal static class Design
{
    /// <summary>品牌蓝。工具栏带、主按钮、选中态、链接。</summary>
    internal static class Brand
    {
        public static readonly IBrush Tint = Brush(0xF2, 0xF7, 0xFD);   // Brand.50
        public static readonly IBrush Soft = Brush(0xE1, 0xEE, 0xFB);   // Brand.100
        public static readonly IBrush Line = Brush(0xBF, 0xD9, 0xF4);   // Brand.200
        public static readonly IBrush Bright = Brush(0x2E, 0x86, 0xD6); // Brand.500，只用于大字/图标
        public static readonly IBrush Base = Brush(0x0F, 0x6C, 0xBD);   // Brand.600，主色
        public static readonly IBrush Deep = Brush(0x0C, 0x5A, 0xA0);   // Brand.700，按下
        public static readonly IBrush Hover = Brush(0x4A, 0x9B, 0xE8);  // Brand.400，分隔条/描边悬停

        /// <summary>叠在品牌蓝带上的半透明白：悬停 12%、按下 20%。</summary>
        public static readonly IBrush OnBrandHover = Brush(0x1F, 0xFF, 0xFF, 0xFF);
        public static readonly IBrush OnBrandPressed = Brush(0x33, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>文字与描边的中性色。</summary>
    internal static class Ink
    {
        public static readonly IBrush Primary = Brush(0x1F, 0x23, 0x28);   // 对白底 15.4:1
        public static readonly IBrush Secondary = Brush(0x5B, 0x64, 0x70); // 5.6:1
        public static readonly IBrush Disabled = Brush(0x9A, 0xA3, 0xAE);  // 只允许出现在禁用内容上
        public static readonly IBrush OnBrand = Brush(0xFF, 0xFF, 0xFF);
        public static readonly IBrush OnBrandDisabled = Brush(0x99, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>面板底色。层级靠底色差表达 —— SimpleTheme 用不了阴影。</summary>
    internal static class Surface
    {
        public static readonly IBrush Base = Brush(0xFF, 0xFF, 0xFF);
        public static readonly IBrush Muted = Brush(0xF6, 0xF8, 0xFA);
        public static readonly IBrush Sunken = Brush(0xFA, 0xFB, 0xFC);
        public static readonly IBrush RowAlt = Brush(0xFA, 0xFB, 0xFC);
    }

    internal static class Line
    {
        public static readonly IBrush Subtle = Brush(0xE6, 0xE9, 0xEC);
        public static readonly IBrush Strong = Brush(0xC9, 0xCE, 0xD6);
    }

    /// <summary>语义色。规范里写着"不许当装饰"：红只用于错误/危险，绿只用于成功。</summary>
    internal static class Semantic
    {
        public static readonly IBrush Danger = Brush(0xC4, 0x2B, 0x1C);
        public static readonly IBrush DangerTint = Brush(0xFD, 0xEC, 0xEA);
        public static readonly IBrush Success = Brush(0x1A, 0x7F, 0x37);
        public static readonly IBrush WarningText = Brush(0x8A, 0x61, 0x00);
        public static readonly IBrush WarningTint = Brush(0xFF, 0xF6, 0xDE);
        public static readonly IBrush WarningIcon = Brush(0xD9, 0xA6, 0x1F);
    }

    /// <summary>文件夹图标的面与边。内容区唯一允许填充的图标。</summary>
    internal static class Icon
    {
        public static readonly IBrush Face = Brush(0xF7, 0xC9, 0x48);
        public static readonly IBrush Edge = Brush(0xD9, 0xA6, 0x1F);
        public static readonly IBrush Glyph = Brush(0x5B, 0x64, 0x70);
    }

    /// <summary>圆角只有这 5 档（规范 4.1）。</summary>
    internal static class Radius
    {
        public static readonly CornerRadius None = new(0);
        public static readonly CornerRadius Xs = new(3);
        public static readonly CornerRadius Sm = new(6);
        public static readonly CornerRadius Md = new(8);
        public static readonly CornerRadius Lg = new(12);
        public static readonly CornerRadius Pill = new(999);
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

    private static IBrush Brush(int gray) => new SolidColorBrush(Color.FromRgb((byte)gray, (byte)gray, (byte)gray));

    private static IBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private static IBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
}
