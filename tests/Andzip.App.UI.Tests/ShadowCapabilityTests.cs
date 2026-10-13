using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.UiTests;

/// <summary>
/// 阴影这件事的**先探后写**：规范 §二 记着"SimpleTheme 的控件模板不吃阴影属性"，
/// 但那说的是 Button 那一类模板控件。卡片是自己搭的 <see cref="Border"/>，
/// 而 <c>Border.BoxShadow</c> 是 Border 自己的属性 —— 两者不是一回事，得分开量。
/// <para>
/// 这里只问一个问题：卡片<strong>外沿几个像素</strong>有没有比页面底色暗。
/// 暗了才有阴影，没暗就说明这条路走不通，设计得换表达层级的手段。
/// </para>
/// </summary>
[Collection("界面状态")]
public class ShadowCapabilityTests
{
    private static readonly Color Page = Color.FromRgb(0xF6, 0xF8, 0xFA);
    private static readonly Color Card = Color.FromRgb(0xFF, 0xFF, 0xFF);

    [AvaloniaFact]
    public void BorderBoxShadowPaintsDarkPixelsOutsideTheCard()
    {
        var card = new Border
        {
            Width = 160,
            Height = 90,
            Background = new SolidColorBrush(Card),
            CornerRadius = new CornerRadius(12),
            BoxShadow = BoxShadows.Parse("0 3 14 0 #3D0B1E33"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var window = new Window
        {
            Content = new Panel { Children = { card } },
            Width = 520,
            Height = 360,
            Background = new SolidColorBrush(Page),
        }.Shown();

        var far = Pixels.At(window, new Point(14, card.Bounds.Y + card.Bounds.Height / 2));
        Assert.True(Pixels.Same(far, Page),
            $"离卡片很远的地方就不是页面底色了（{Pixels.Name(far)}），测量不成立");

        // 卡片右缘外 4px：有阴影应当比底色暗，没阴影就还是底色
        var outside = Pixels.At(window, new Point(card.Bounds.Right + 4, card.Bounds.Y + card.Bounds.Height / 2));
        var darker = Luminance(outside) < Luminance(Page) - 0.01;
        Assert.True(darker,
            $"卡片外沿 4px 处是 {Pixels.Name(outside)}，与页面底色 {Pixels.Name(Page)} 一样亮 —— "
            + "Border.BoxShadow 没画出来，阴影不能这么实现");
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
