using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Andzip.App.UiTests;

/// <summary>
/// 压缩包视图左栏那条线。用户为它报了三次"太粗"（1.8.0 改淡的是<strong>分栏线</strong>，
/// 漏掉了真正刺眼的那一圈：<c>SimpleTheme</c> 给 <c>TreeView</c> 模板自带的描边，
/// 真机截图量出来是 <c>#888888</c>、1 DIP，在 150% 缩放下就是屏幕上那根 2px 实线）。
/// <para>所以这条断言量的是<strong>树自己的四条边</strong>，不是分栏线：
/// 规范 §5.1 说这一屏两栏之间只有一根发丝线，那么树就不该另带一圈框。</para>
/// </summary>
[Collection("界面状态")]
public class TreePaneLineTests
{
    [AvaloniaFact]
    public void TheTreeCarriesNoOutlineOfItsOwn()
    {
        var lang = TestLang.Chinese();
        var directory = Directory.CreateTempSubdirectory("andzip-tree-line-");

        try
        {
            var archive = Path.Combine(directory.FullName, "box.zip");
            var source = Path.Combine(directory.FullName, "box");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "sub", "inner.txt"), "inner");
            File.WriteAllText(Path.Combine(source, "top.txt"), "top");
            ArchiveCreator.Create(
                [ArchiveInputItem.FromPath(source)],
                new ArchiveCreationSettings(archive, CompressionLevelOption.Store, Format: ArchiveFormatOption.FromId("ZIP")));

            var view = new ArchiveView(lang, ArchiveReader.Read(archive), new ViewDefaults(
                ShowStatusBar: true,
                IconViewByDefault: false, AutoExpandTree: true));
            var host = new Window { Content = view.Root, Width = 1200, Height = 800 }.Shown();

            var tree = VisualQuery.Of<TreeView>(host).Single();

            // 量在树自己的边界上：描边就画在那一格（下沿取 Height-1，左沿取 x=0）。
            var edges = new (string Where, Point At)[]
            {
                ("下沿", new Point(tree.Bounds.Width / 2, tree.Bounds.Height - 1)),
                ("左沿", new Point(0, tree.Bounds.Height / 2)),
                ("右沿", new Point(tree.Bounds.Width - 1, tree.Bounds.Height / 2)),
            };

            foreach (var (where, at) in edges)
            {
                var point = tree.TranslatePoint(at, host) ?? new Point(0, 0);
                var color = Pixels.At(host, new Point(Math.Round(point.X), Math.Round(point.Y)));

                Assert.True(Pixels.Luminance(color) > 0.92,
                    $"树的{where}量到 {Pixels.Name(color)} —— SimpleTheme 的 TreeView 模板自带一圈 #888 描边，"
                    + "而这一屏两栏之间只该有一根发丝分栏线（规范 §5.1）");
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
