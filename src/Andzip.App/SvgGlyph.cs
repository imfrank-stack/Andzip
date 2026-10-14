using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Andzip.App;

/// <summary>
/// 把 <c>assets/svg/*.svg</c> 那九枚双色图标画到界面上。
/// <para><strong>为什么读文件而不是把路径抄进代码</strong>：那九个 .svg 是用户在编辑器里改的，
/// 抄进 <c>Icons.cs</c> 就等于造出第二份真相 —— 而且是一份会撒谎的，因为改了 SVG 的人
/// 不会知道界面上还是旧的。读原文件之后，"设计稿与屏幕上不一样"这一类问题从结构上就不存在了。</para>
/// <para><strong>为什么不引 SVG 库、也不用 <c>System.Xml.Linq</c></strong>：这条产物是裁剪过的单文件。
/// 第一版用 <c>XDocument</c> 解析，发布体积从 20.77 MB 涨到 22.61 MB ——
/// <c>System.Private.Xml</c> 整个被拽进来，为的是读 5 KB 的扁平标记。
/// 所以这里自带一个只认这几种写法的小扫描器：<c>path/@d</c>、<c>rect</c>、<c>circle</c>
/// 与 <c>fill</c> / <c>stroke</c> / <c>stroke-width</c> / <c>stroke-linecap</c>，
/// 而 <c>Geometry.Parse</c> 本来就吃 SVG 的 path 语法（含圆弧 <c>A</c>）。
/// 认不出来的写法一律<strong>当场抛</strong>而不是悄悄画错：有一条用例把九个文件全过一遍，
/// 所以"以后有人加了个 <c>&lt;g&gt;</c>"会在测试里红，而不是在用户的屏幕上少一块。</para>
/// <para>颜色不是照抄：原稿那三个值里 <c>#414B5A</c> 在深色卡片上会直接看不见。
/// 映射见 <see cref="BrushFor"/> —— 保住"两档灰 + 一层与卡片同色的挖空"这个<strong>关系</strong>，
/// 绝对值交给调色板。</para>
/// </summary>
internal static class SvgGlyph
{
    private const string BodyColor = "#414B5A";
    private const string AccentColor = "#A7B1BF";
    private const string HaloColor = "#FFFFFF";

    /// <summary>SVG 里"不是图形"的那几种：读了丢掉，不算漏画。</summary>
    private static readonly string[] Metadata = ["title", "desc", "metadata"];

    private static readonly Dictionary<string, Drawing> Cache = new(StringComparer.Ordinal);

    /// <summary>按文件名（不含扩展名）画一枚。读不到就抛 —— 宁可界面红在测试里。</summary>
    public static Control Load(string name, double size)
    {
        var drawing = Read(name);
        var surface = new Panel { Width = drawing.Edge, Height = drawing.Edge };

        foreach (var piece in drawing.Pieces)
        {
            surface.Children.Add(piece.ToShape());
        }

        // 每片几何都带着自己在 48 分格里的绝对坐标，所以整组套一个 Viewbox 缩放就行；
        // 逐片 Stretch 会把每一笔都撑满方框，图标会糊成一团。
        return new Viewbox
        {
            Width = size,
            Height = size,
            Child = surface,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static Drawing Read(string name)
    {
        if (Cache.TryGetValue(name, out var hit))
        {
            return hit;
        }

        var elements = Scan(Source(name)).ToList();
        var root = elements.FirstOrDefault(e => e.Tag == "svg")
            ?? throw new InvalidDataException($"{name}.svg 没有 <svg> 根节点");
        var viewBox = root.Attr("viewBox")?.Split(' ')
            ?? throw new InvalidDataException($"{name}.svg 没有 viewBox");

        var drawing = new Drawing(Double(viewBox[2]), elements
            .Where(e => e.Tag != "svg" && !Metadata.Contains(e.Tag, StringComparer.Ordinal))
            .Select(PieceOf)
            .ToList());

        Cache[name] = drawing;
        return drawing;
    }

    private static Piece PieceOf(Element element)
    {
        var geometry = element.Tag switch
        {
            "path" => Geometry.Parse(element.Require("d")),
            "rect" => new RectangleGeometry(
                new Rect(Double(element.Require("x")), Double(element.Require("y")),
                    Double(element.Require("width")), Double(element.Require("height"))),
                Number(element, "rx"), Number(element, "rx")),
            "circle" => new EllipseGeometry
            {
                Center = new Point(Double(element.Require("cx")), Double(element.Require("cy"))),
                RadiusX = Double(element.Require("r")),
                RadiusY = Double(element.Require("r")),
            },
            var other => throw new InvalidDataException($"<{other}> 不在支持之列（认不出就别画）"),
        };

        return new Piece(
            geometry,
            element.Attr("fill"),
            element.Attr("stroke"),
            element.Attr("stroke-width") is { } width ? Double(width) : 0,
            element.Attr("stroke-linecap") == "round");
    }

    /// <summary>
    /// 扁平标记的小扫描器：交出每个开始标签与它的属性。
    /// <para>够用的前提写在这里：这九份文件<strong>没有嵌套、没有实体、属性一律双引号，
    /// 而且 path 的 <c>d</c> 里不出现 <c>&gt;</c></strong>。守这条前提的是那条"每片都要活下来"的用例 ——
    /// 谁把图标改成分组结构，这里抛 <c>&lt;g&gt; 不在支持之列</c>，而不是安静地画出一半。</para>
    /// </summary>
    private static IEnumerable<Element> Scan(string text)
    {
        var at = 0;

        while ((at = text.IndexOf('<', at)) >= 0)
        {
            var end = text.IndexOf('>', at);
            if (end < 0)
            {
                yield break;
            }

            var body = text.Substring(at + 1, end - at - 1);
            at = end + 1;

            // <?xml?>、<!-- -->、</svg> 这一类不是元素，跳过。
            if (body.Length == 0 || body[0] is '?' or '!' or '/')
            {
                continue;
            }

            yield return Element.Parse(body.TrimEnd('/'));
        }
    }

    /// <summary>
    /// 字面色 → 主题令牌。缺省与 <c>none</c> 是"这一层不画"。
    /// <para><c>#FFFFFF</c> 映射到<strong>卡片底色</strong>而不是白色：原稿用它当徽章外那一圈
    /// "挖空"，让徽章从盒子上断开。真给白色，深色模式下就是一圈发亮的白边 ——
    /// 而"与它坐在哪张卡片上同色"才是这个设计想表达的东西。</para>
    /// </summary>
    private static IBrush? BrushFor(string? literal) => literal switch
    {
        null or "none" => null,
        BodyColor => Design.Icon.Glyph,
        AccentColor => Design.Icon.Accent,
        HaloColor => Design.Surface.Base,
        var unknown => throw new InvalidDataException($"没给 {unknown} 这个颜色配令牌"),
    };

    private static double Number(Element element, string name)
        => element.Attr(name) is { } text ? Double(text) : 0;

    private static double Double(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    private static string Source(string name)
    {
        var assembly = typeof(SvgGlyph).Assembly;
        var key = System.Array.Find(assembly.GetManifestResourceNames(),
            n => n.EndsWith(name + ".svg", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"内嵌资源里没有 {name}.svg");

        using var stream = assembly.GetManifestResourceStream(key)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Drawing(double Edge, List<Piece> Pieces);

    private sealed record Piece(Geometry Data, string? Fill, string? Stroke, double StrokeWidth, bool RoundCap)
    {
        public Shape ToShape() => new Path
        {
            Data = Data,
            Fill = BrushFor(Fill),
            Stroke = BrushFor(Stroke),
            StrokeThickness = Stroke is null or "none" ? 0 : StrokeWidth,
            StrokeLineCap = RoundCap ? PenLineCap.Round : PenLineCap.Flat,
            StrokeJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
        };
    }

    /// <summary>一个开始标签：名字 + 属性表。</summary>
    private sealed class Element(string tag, Dictionary<string, string> attributes)
    {
        public string Tag { get; } = tag;

        public string? Attr(string name) => attributes.GetValueOrDefault(name);

        public string Require(string name)
            => Attr(name) ?? throw new InvalidDataException($"<{Tag}> 少了 {name}");

        public static Element Parse(string body)
        {
            var nameEnd = body.IndexOfAny([' ', '\t', '\n', '\r']);
            if (nameEnd < 0)
            {
                return new Element(body, []);
            }

            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            var at = nameEnd;

            while (at < body.Length && body.IndexOf('=', at) is var equals && equals > 0)
            {
                var quote = body.IndexOf('"', equals);
                var close = quote < 0 ? -1 : body.IndexOf('"', quote + 1);
                if (close < 0)
                {
                    throw new InvalidDataException($"属性值不用双引号的写法不支持：{body[at..]}");
                }

                attributes[body[at..equals].Trim()] = body[(quote + 1)..close];
                at = close + 1;
            }

            return new Element(body[..nameEnd], attributes);
        }
    }
}
