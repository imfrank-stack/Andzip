using Andzip.App;
using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Andzip.App.UiTests;

/// <summary>
/// 树与列表之间那根线（<see cref="Seam"/>）。用户的反馈是「这条线应该是可以拖的，
/// 现在无法调整」—— 线画得对（1px 发丝、上下留白），但<strong>拖不动</strong>。
/// <para>原因有两条，都是量出来的：<c>GridSplitter</c> 只认自己<strong>的直接父级</strong>那枚 Grid
/// （它按父级找要改哪几列），而这里为了"看得见 1px、点得住 8px"把它套进了一层 <c>Grid</c> 宿主 ——
/// 于是它想改的是那层 8px 宽的宿主，而不是外面的三列；另外那 8px 命中带必须有<strong>一层可命中的
/// 填充</strong>，模板根留 null 的话整条带子只有中间那 1px 点得着（把填充删掉这条就红）。</para>
/// <para>所以这条用例量的不是属性，是<strong>拖完之后左栏真的变宽了没有</strong>，
/// 而且手按在命中带的边上、不按在那根线上。</para>
/// </summary>
[Collection("界面状态")]
public class SeamDragTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-seam-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public void DraggingTheSeamWidensTheTreePane()
    {
        var window = new Window { Content = OpenedArchive(), Width = 1200, Height = 800 }.Shown();
        var tree = VisualQuery.Of<TreeView>(window).Single();
        var seam = SeamOf(window, tree);

        // 故意不瞄在那 1px 线上：8px 命中带存在的意义就是"差几个像素也拖得动"。
        var at = OnWindow(seam, seam.Bounds.Width - 2, seam.Bounds.Height / 2);
        Pixels.At(window, at);   // 命中与模板都要先有一帧渲染过

        var before = tree.Bounds.Width;
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Point(120, 0));
        window.MouseUp(at + new Point(120, 0), MouseButton.Left);
        UiWait.Pump(120);

        var after = tree.Bounds.Width;
        Assert.True(after > before + 60,
            $"在命中带边上按住往右拖 120，左栏只从 {before} 走到 {after} —— 分栏线拖不动。"
            + "两件事要同时成立：GridSplitter 得直接待在那枚有三列的 Grid 里，"
            + "而那 8px 命中带得有一层可命中的填充（模板根留 null 就只剩 1px 点得着）");
    }

    /// <summary>
    /// 首页那一屏（还没开包）的同一根线：那里没有树可以量宽度，所以量装配本身 ——
    /// <c>GridSplitter</c> 必须<strong>直接</strong>待在那枚有三列的 Grid 里。
    /// </summary>
    [AvaloniaFact]
    public void TheHomeSeamSitsInsideTheGridWhoseColumnsItResizes()
    {
        var window = new Window { Content = HomeView.Create(TestLang.Chinese()), Width = 1200, Height = 800 }.Shown();
        Pixels.At(window, new Point(4, 4));

        var seam = VisualQuery.Of<GridSplitter>(window).Single();

        Assert.True(seam.Parent is Grid body && body.ColumnDefinitions.Count == 3,
            $"分栏的 GridSplitter 的父级是 {seam.Parent?.GetType().Name ?? "null"}，"
            + "不是那枚有三列的 Grid —— 它找不到要改哪两列，于是拖不动");
    }

    private Control OpenedArchive()
    {
        var archive = Path.Combine(_root, "box.zip");
        var source = Path.Combine(_root, "box");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "sub", "inner.txt"), "inner");
        File.WriteAllText(Path.Combine(source, "top.txt"), "top");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(source)],
            new ArchiveCreationSettings(archive, CompressionLevelOption.Store, Format: ArchiveFormatOption.FromId("ZIP")));

        return new ArchiveView(TestLang.Chinese(), ArchiveReader.Read(archive), new ViewDefaults(
            ShowStatusBar: true, ShowTree: true,
            IconViewByDefault: false, AutoExpandTree: true)).Root;
    }

    /// <summary>树与列表之间那一枚：详情表头也有分栏，但它只有一行高。</summary>
    private static GridSplitter SeamOf(Window window, TreeView tree)
        => VisualQuery.Of<GridSplitter>(window)
            .First(s => s.Bounds.Height > tree.Bounds.Height / 2);

    private static Point OnWindow(Visual from, double x, double y)
        => from.TranslatePoint(new Point(x, y), (Visual)GetWindow(from)!) ?? default;

    private static Visual? GetWindow(Visual v)
    {
        Visual? p = v;
        while (p is not null and not Window)
        {
            p = p.Parent as Visual;
        }

        return p;
    }
}
