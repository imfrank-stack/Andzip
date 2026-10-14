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

    /// <summary>
    /// 整块面板那档阴影（<c>Design.Shadow.Floating</c>）在<strong>顶边</strong>也得画得出东西。
    /// <para>用户的话：「顶部也要有阴影，否则会和背景分不清」。之前三层里两层都只往下偏移，
    /// 顶边只摊到 2px 模糊的余量 —— 压在浅色桌面上就是窗沿和桌面连成一片。
    /// 1.8.0 在药丸上犯过同一个错（<c>ShadowLift</c> 那条注释记着），这次犯在整窗上。</para>
    /// <para>量的是<strong>令牌</strong>而不是某个窗口：主窗口、设置窗口、每个对话框用的都是它，
    /// 一处改坏全都看得见。判据用"同一张页底色上，远处与顶边外 2px 的差"，
    /// 所以浅色深色两套调色板都成立（两套都补了那一层不偏移的扩散）。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheAmbientShadowLiftsTheTopEdgeToo()
    {
        var panel = new Border
        {
            Width = 260,
            Height = 160,
            Background = new SolidColorBrush(Card),
            CornerRadius = new CornerRadius(12),
            BoxShadow = Design.Shadow.Floating,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var window = new Window
        {
            Content = new Panel { Children = { panel } },
            Width = 520,
            Height = 360,
            Background = new SolidColorBrush(Page),
        }.Shown();

        var far = Pixels.At(window, new Point(14, 14));
        Assert.True(Pixels.Same(far, Page),
            $"离面板很远的地方就不是页面底色了（{Pixels.Name(far)}），测量不成立");

        var above = Pixels.At(window, new Point(panel.Bounds.X + panel.Bounds.Width / 2, panel.Bounds.Y - 2));
        var below = Pixels.At(window, new Point(panel.Bounds.X + panel.Bounds.Width / 2, panel.Bounds.Bottom + 2));

        Assert.True(Luminance(Page) - Luminance(above) >= 0.01,
            $"面板顶边外 2px 是 {Pixels.Name(above)}，与页底色 {Pixels.Name(Page)} 一样亮 —— "
            + $"顶部没有阴影，窗沿和背景会连成一片（底边外 2px 作对照：{Pixels.Name(below)}）");
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
