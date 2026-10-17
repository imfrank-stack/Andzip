using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Andzip.App.UiTests;

/// <summary>
/// 「设置 → 查看 → 自动展开树里的文件夹」这条开关到底管不管事。
/// <para>它管的是<strong>别人构造出来的那棵树</strong>，所以断言只能从装配好的视图往下读
/// <c>TreeViewItem.IsExpanded</c> —— 相信"代码里赋了值"就是这次 bug 的成因。</para>
/// </summary>
[Collection("界面状态")]
public class TreeExpansionTests
{
    [AvaloniaFact]
    public void AutoExpandOffLeavesSubfoldersCollapsed()
    {
        var (host, items) = OpenTree(autoExpand: false);

        Assert.NotEmpty(items);

        // 根节点（压缩包自己）永远展开：不展开就什么都看不见。这一项管的是里面的子文件夹。
        var root = items[0];
        Assert.True(root.IsExpanded, "根节点收起来，整棵树就是空的。");

        var expanded = items.Skip(1).Where(item => item.IsExpanded).ToList();
        Assert.True(
            expanded.Count == 0,
            "关掉「自动展开」之后还有子文件夹是摊开的：" + string.Join(" / ", expanded.Select(Name)));
    }

    [AvaloniaFact]
    public void AutoExpandOnSpreadsTheWholeTree()
    {
        var (_, items) = OpenTree(autoExpand: true);

        Assert.True(
            items.All(item => item.IsExpanded),
            "开着「自动展开」却有文件夹是收起来的：" + string.Join(" / ", items.Where(i => !i.IsExpanded).Select(Name)));
    }

    /// <summary>
    /// 什么都没设过（新装、或删过 settings.ini）时树应当是<strong>收起来的</strong>。
    /// 这一条钉的是默认值本身：默认摊开的话，深包第一屏全是文件夹名。
    /// </summary>
    [AvaloniaFact]
    public void WithNoSettingsAtAllTheTreeStartsCollapsed()
    {
        var (_, items) = OpenTree(ViewDefaults.Fallback);

        Assert.True(items[0].IsExpanded, "根节点还是要展开的。");
        Assert.True(items.Skip(1).All(item => !item.IsExpanded), "没有设置时子文件夹不该预先摊开。");
    }

    /// <summary>造一个三层嵌套的包，按给定的开关装配出压缩包视图，返回宿主窗口与树里的全部节点。</summary>
    private static (Window Host, IReadOnlyList<TreeViewItem> Items) OpenTree(bool autoExpand)
        => OpenTree(new ViewDefaults(
            ShowStatusBar: true,
            IconViewByDefault: false,
            AutoExpandTree: autoExpand));

    private static (Window Host, IReadOnlyList<TreeViewItem> Items) OpenTree(ViewDefaults view)
    {
        var lang = TestLang.Chinese();
        var directory = Directory.CreateTempSubdirectory("andzip-tree-");

        try
        {
            var archive = CreateNestedArchive(directory.FullName);
            var listing = ArchiveReader.Read(archive);
            var host = new Window { Content = new ArchiveView(lang, listing, view).Root, Width = 1200, Height = 800 }.Shown();

            return (host, VisualQuery.Of<TreeViewItem>(host));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string Name(TreeViewItem item)
        => HeaderText(item.Header).Replace("\n", " ", StringComparison.Ordinal);

    private static string HeaderText(object? header)
        => header is StackPanel panel
            ? string.Concat(panel.Children.OfType<TextBlock>().Select(t => t.Text))
            : header?.ToString() ?? string.Empty;

    /// <summary>包内是 <c>box/a/b/c/deep.txt</c> 这种四层结构。</summary>
    private static string CreateNestedArchive(string root)
    {
        var source = Path.Combine(root, "box");
        Directory.CreateDirectory(Path.Combine(source, "a", "b", "c"));
        File.WriteAllText(Path.Combine(source, "a", "b", "c", "deep.txt"), "deep");
        File.WriteAllText(Path.Combine(source, "top.txt"), "top");

        var archive = Path.Combine(root, "nested.zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(source)],
            new ArchiveCreationSettings(archive, CompressionLevelOption.Store, Format: ArchiveFormatOption.FromId("ZIP")));
        return archive;
    }
}
