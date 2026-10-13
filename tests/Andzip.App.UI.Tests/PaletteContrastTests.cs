using Avalonia.Media;

namespace Andzip.App.UiTests;

/// <summary>
/// 两套调色板的对比度实算（WCAG 2.1 相对亮度公式）。
/// <para>规范文档里那些"5.6:1 ✅ AA"的数字不是估的，就是这条测试报出来的 ——
/// 颜色是会在改配色时悄悄跌破门槛的东西，写进文档的数字迟早会变成假的。</para>
/// <para>深色那一份特别值得算：同一个"看起来挺亮"的灰，压在深底上的对比度常常比压在白底上的
/// 同名灰低一截，而人眼在暗环境里对低对比更不敏感，于是"看着还行"的文字实际读不清。</para>
/// </summary>
public class PaletteContrastTests
{
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void TextOnItsBackgroundsClearsWcag(string which)
    {
        var board = which == "dark" ? Palette.Dark : Palette.Light;
        var failures = new List<string>();

        void Check(string label, IBrush foreground, IBrush background, double floor)
        {
            var ratio = Contrast(foreground, background);
            if (ratio < floor)
            {
                // 报出来的是"哪一对、实际多少、门槛多少"，只说"不达标"没法定位。
                failures.Add($"{label}：{Name(foreground)} on {Name(background)} = {ratio:F2}:1，门槛 {floor:F1}:1");
            }
        }

        Check("正文 / 内容底", board.InkPrimary, board.SurfaceBase, 7.0);
        Check("次要文字 / 悬停底", board.InkSecondary, board.SurfaceMuted, 4.5);
        Check("正文 / 页脚与表头", board.InkPrimary, board.SurfaceMuted, 7.0);
        Check("次要文字 / 内容底", board.InkSecondary, board.SurfaceBase, 4.5);
        Check("次要文字 / 页脚与表头", board.InkSecondary, board.SurfaceMuted, 4.5);
        Check("正文 / 选中行底", board.InkPrimary, board.BrandSoft, 4.5);
        Check("主按钮文字 / 品牌蓝底", board.InkOnBrand, board.BrandBase, 4.5);
        // 开关这里要断言的不是"滑块看得见"，而是"开着和关着分得开"：
        // 浅色下滑块是白的、关闭轨道也是浅灰，两者本来就该差得少（iOS/Windows 的开关同理），
        // 拿 3:1 卡它会逼出一个难看的深灰轨道。真正不能让步的是两态之间的差。
        Check("开关滑块 / 开启轨道", board.ToggleKnob, board.ToggleTrackOn, 3.0);
        Check("开关两态（开轨道 / 关轨道）", board.ToggleTrackOn, board.ToggleTrackOff, 3.0);
        Check("错误文字 / 内容底", board.Danger, board.SurfaceBase, 4.5);
        Check("成功文字 / 内容底", board.Success, board.SurfaceBase, 4.5);
        Check("警告文字 / 内容底", board.WarningText, board.SurfaceBase, 4.5);

        Assert.True(failures.Count == 0, $"{board.Name} 调色板对比度不达标：\n    " + string.Join("\n    ", failures));
    }

    private static Color Of(IBrush brush) => brush is ISolidColorBrush solid
        ? solid.Color
        : throw new InvalidOperationException($"这个令牌不是纯色画刷（{brush.GetType().Name}），对比度没法算。");

    private static string Name(IBrush brush)
    {
        var c = Of(brush);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private static double Contrast(IBrush foreground, IBrush background)
    {
        var a = Luminance(Of(foreground));
        var b = Luminance(Of(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>WCAG 相对亮度：sRGB 线性化后按 0.2126 / 0.7152 / 0.0722 加权。</summary>
    private static double Luminance(Color c)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
