namespace Andzip.Core.Iconography;

/// <summary>
/// 图标上那五个颜色与一条渐变方向 —— 用户能改的就是这一份状态。
/// <para>为什么存在 Core 而不是界面里：这套东西要能被单钉（预设、混白、十六进制解析都是纯算式），
/// 而"把 <c>#4AD9DC</c> 这种字面量写进 <c>src/Andzip.App</c>"会直接撞上那条
/// 「界面代码零字面色值」的扫描门禁（<c>DesignTokenTests</c>）。
/// 颜色的<strong>来源</strong>是用户输入，不是设计令牌 —— 这件事本来就不该和令牌混在同一层。</para>
/// <para>颜色存的是 <c>#RRGGBB</c> 字符串而不是 <c>Color</c>：设置文件里存的就是这一串，
/// 语言无关、可 diff，而 Core 不该依赖 Avalonia 的类型。</para>
/// </summary>
/// <param name="Front0">前层渐变起点（最亮那一头）。</param>
/// <param name="Front1">前层渐变中点，固定在 52%。</param>
/// <param name="Front2">前层渐变止点（最深那一头）。</param>
/// <param name="Back0">后层（那块退到后面、半透明的板子）渐变起点。</param>
/// <param name="Back1">后层渐变止点。</param>
/// <param name="Direction"><see cref="Directions"/> 的下标。</param>
public sealed record IconPalette(
    string Front0,
    string Front1,
    string Front2,
    string Back0,
    string Back1,
    int Direction)
{
    /// <summary>出厂那一套（与 <see cref="Presets"/> 第一项同一个），也是「恢复默认」回到的地方。</summary>
    public static IconPalette Default { get; } =
        new("#4AD9DC", "#2E9BF0", "#2264E8", "#6FE6E4", "#2E8FF0", 0);

    /// <summary>
    /// 四组渐变锚点：↘ ↓ → ↙（百分比，前层与后层共用同一组 —— 参考工具里那两个渐变是一起转的）。
    /// </summary>
    public static readonly (double X1, double Y1, double X2, double Y2)[] Directions =
    [
        (16, 6, 88, 96),
        (50, 0, 50, 100),
        (0, 50, 100, 50),
        (88, 6, 16, 96),
    ];

    /// <summary>八套预设。第一套就是出厂那套，所以「预设」这一排不能是空的。</summary>
    public static readonly IconPalette[] Presets =
    [
        new("#4AD9DC", "#2E9BF0", "#2264E8", "#6FE6E4", "#2E8FF0", 0),
        new("#FF6A3D", "#E555D8", "#6E5BFF", "#FF8A5B", "#8B5CFF", 0),
        new("#A8F0C6", "#2EC4A6", "#11826F", "#C4F7DC", "#23A88E", 0),
        new("#7FC8FF", "#2B6FF0", "#1B3FD0", "#A8DBFF", "#2A66E0", 0),
        new("#C79BFF", "#8B5CFF", "#5B3BE0", "#D9BBFF", "#7C4DFF", 0),
        new("#FFC96B", "#FF7A3D", "#E5342B", "#FFD98F", "#FF6A3D", 0),
        new("#FFC0D9", "#FF6FA8", "#D9367F", "#FFD6E6", "#F0578F", 0),
        new("#B8BFCC", "#6E7787", "#3A4050", "#CFD5E0", "#5C6474", 0),
    ];

    /// <summary>方向编号取模：设置文件里被人手改坏成一个不存在的下标时，退回第一组而不是抛。</summary>
    public (double X1, double Y1, double X2, double Y2) Anchors =>
        Directions[Math.Clamp(Direction, 0, Directions.Length - 1)];

    /// <summary>
    /// 「后层跟随前景」：后层不自己挑色，而是从前层两头各掺一档白。
    /// <para>掺的幅度（0.30 / 0.25）与参考工具一致 —— 后层要的是"同一套色但退到后面去"，
    /// 自己调很容易调成另一套配色，两块板子就打架了。</para>
    /// </summary>
    public IconPalette WithBackFromFront() => this with
    {
        Back0 = MixWhite(Front0, 0.30),
        Back1 = MixWhite(Front2, 0.25),
    };

    /// <summary>往白色方向掺。掺满（amount=1）就是纯白，掺 0 就是原色。</summary>
    public static string MixWhite(string hex, double amount)
    {
        var (r, g, b) = Rgb(hex);
        var k = Math.Clamp(amount, 0, 1);

        // 半数往上取（AwayFromZero）。默认的 Math.Round 是"四舍六入五成双"，
        // 而参考工具跑的是浏览器的 Math.round —— 220 + 35×0.3 正好落在 230.5 上，
        // 用成双就会得到 #E6 而不是 #E7：一个通道差 1，肉眼看不出来，断言看得出来。
        static byte TowardWhite(byte channel, double t)
            => (byte)Math.Round(channel + (255 - channel) * t, MidpointRounding.AwayFromZero);

        return $"#{TowardWhite(r, k):X2}{TowardWhite(g, k):X2}{TowardWhite(b, k):X2}";
    }

    /// <summary>
    /// 把用户输入收成 <c>#RRGGBB</c>（大写）。接受 <c>4ad9dc</c>、<c>#4AD9DC</c>、带首尾空格。
    /// <para>不合法就返回 null 而不是"猜一个"：界面拿 null 时保留原值。
    /// 一个打错的十六进制被静默当成黑色，用户会看到图标突然变黑，然后开始怀疑软件。</para>
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var value = text.Trim();
        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        if (value.Length != 6)
        {
            return null;
        }

        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return $"#{value.ToUpperInvariant()}";
    }

    /// <summary>拆成三个字节。调用方要么先过 <see cref="Normalize"/>，要么传的是内置预设。</summary>
    public static (byte R, byte G, byte B) Rgb(string hex)
    {
        var value = Normalize(hex) ?? throw new ArgumentException($"这不是一个十六进制颜色：{hex}", nameof(hex));
        return (
            Convert.ToByte(value[1..3], 16),
            Convert.ToByte(value[3..5], 16),
            Convert.ToByte(value[5..7], 16));
    }
}
