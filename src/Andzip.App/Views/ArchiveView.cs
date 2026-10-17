using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Path = System.IO.Path;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Andzip.App.Views;

/// <summary>
/// 压缩包视图（FR-4）：左侧目录树 + 右侧当前目录的详情列表 / 图标视图 + 底部状态栏与三个视图按钮。
/// <para>
/// 数据来自 <see cref="ArchiveHierarchy"/>（扁平清单 → 目录树），本类只管呈现与导航：
/// 点树节点、双击文件夹行、双击 <c>..</c> 行，都汇到 <see cref="Navigate"/> 一个入口。
/// </para>
/// <para>
/// 图标视图按需回读条目字节生成缩略图（<see cref="ArchiveContentSession"/>），所以它是惰性的：
/// 只有真的切到图标视图才会打开第二个归档句柄。
/// </para>
/// </summary>
internal sealed class ArchiveView : IDisposable
{
    /// <summary>各列的默认宽度（逻辑像素）。前 11 列可拖拽调整，末尾还有一列吃掉剩余宽度。</summary>
    private static readonly double[] DefaultColumnWidths = [160, 96, 96, 110, 120, 96, 96, 140, 60, 80, 130];

    /// <summary>列间分隔条的宽度，同时也是鼠标命中区。</summary>
    private const double SplitterWidth = 5;

    /// <summary>左栏（目录树 / 预览那一列）的默认宽度。分栏线可以拖走它，但初始值在这里。</summary>
    private const double TreePaneWidth = 300;

    /// <summary>分栏线那一列占的宽度（比 <see cref="SplitterWidth"/> 宽一点，左右各留一点呼吸）。</summary>
    private const double SeamColumnWidth = 8;

    /// <summary>拖拽时的列宽下限，防止把某一列拖没。</summary>
    private const double MinimumColumnWidth = 48;

    /// <summary>超过这个条数就不做缩略图：WrapPanel 不虚拟化，几百张图会明显拖慢切换。</summary>
    private const int ThumbnailEntryLimit = 400;

    /// <summary>单张缩略图的解压上限；超过就按普通文件处理。</summary>
    private const long ThumbnailSourceLimit = 32L * 1024 * 1024;

    /// <summary>
    /// 左栏底下那一格预览的<strong>高度</strong>（逻辑像素）。宽是左栏的 300，
    /// 所以 200 那一格读起来是一"块"，不是一条压扁的横幅。
    /// </summary>
    private const double PreviewHeight = 200;

    /// <summary>
    /// 预览回读的解压上限。缩略图那一档（32 MB）对单张图太小 —— 一张 6000×4000 的 JPEG
    /// 很常见，而这里一次只解<strong>一张</strong>，不是几百张，所以放宽到 128 MB。
    /// </summary>
    private const long PreviewSourceLimit = 128L * 1024 * 1024;

    /// <summary>
    /// 预览解码的目标宽度（<strong>物理</strong>像素）。左栏 300 DIP 在 150% 缩放下是 450 物理像素，
    /// 512 是"够填满那一格、又不必解出原图几千万像素"的那一档。
    /// 按宽度解而不是整张解：`DecodeToWidth` 让 Skia 在解码时就降采样，一张 24 MB 的 JPEG
    /// 解成 512 宽只要几毫秒与几 MB，整张解则要先吃掉一百多 MB。
    /// </summary>
    private const int PreviewDecodeWidth = 512;

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff"];

    private static IBrush PrimaryText => Design.Ink.Primary;
    private static IBrush SecondaryText => Design.Ink.Secondary;

    private readonly LangCatalog _lang;
    private readonly ArchiveListing _listing;
    private readonly ArchiveFolderNode _root;
    private readonly Dictionary<ArchiveFolderNode, TreeViewItem> _treeItems = new();
    private readonly ColumnSpec[] _columns;

    /// <summary>
    /// 要画出来的列在 <see cref="_columns"/> 里的下标（按顺序，名称列永远第一，
    /// 末尾那条吃剩余宽度的填充列永远在）。<strong>排序、列宽、表头点击都仍按真实下标说话</strong> ——
    /// 换成"第几列"的序号来存，勾掉一列就会把所有已排序的列与记住的列宽整体错位。
    /// </summary>
    private int[] _shown;
    private readonly double[] _columnWidths = [.. DefaultColumnWidths];
    private readonly ViewDefaults _view;

    private readonly TreeView _tree = new();

    /// <summary>
    /// 左栏底下那一格图像预览。常驻在树上而不是弹在选中行旁边：预览要读的是"我现在选中的是谁"，
    /// 每换一次选中就跳一扇窗，用户找不到刚才那张图在哪。
    /// </summary>
    private readonly Border _previewFrame = new()
    {
        Height = PreviewHeight,
        Margin = new Thickness(0, Design.Space.X2, Design.Space.X2, Design.Space.X2),
        Padding = Design.Space.All(Design.Space.X1),
        Background = Design.Surface.Sunken,
        CornerRadius = Design.Radius.Sm,
        ClipToBounds = true,
    };

    private readonly Image _previewImage = new()
    {
        Stretch = Stretch.Uniform,
        IsVisible = false,
    };

    /// <summary>预览那一格里的"为什么没有图"。只在解不出图时出现，见 <see cref="UpdatePreview"/>。</summary>
    private readonly TextBlock _previewNote = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        FontSize = Design.Type.Caption,
        Foreground = Design.Ink.Secondary,
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false,
    };

    private bool _showPreview;
    private Bitmap? _previewBitmap;
    private CancellationTokenSource? _previewCts;
    private Grid _body = new();

    // 树与内容区之间只有一根可拖的发丝线（1.7.0）：整块窗口是一张面板，
    // 两栏各自成卡片会把一屏切成四块白板，用户的评价是"割裂"。
    private readonly Border _treePane = new() { Padding = new Thickness(Design.Space.X2, Design.Space.X1, 0, 0) };
    private readonly Control _splitter = Seam.Splitter(1);
    private readonly ContentControl _detailHost = new();
    private readonly TextBlock _status = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(Design.Space.X3, 0, Design.Space.X3, 0),
        FontSize = Design.Type.Caption,
        Foreground = Design.Ink.Secondary,
    };
    private readonly Button _listToggle;
    private readonly Button _iconToggle;
    private readonly Button _locateButton;

    private ArchiveFolderNode _current;
    private bool _navigating;
    private ListBox? _detailList;
    private bool _iconView;
    private int _sortColumn;
    private bool _sortAscending = true;
    private CancellationTokenSource? _navigateCts;
    private Task<ArchiveContentSession?>? _session;

    /// <summary>
    /// 压缩包视图（FR-4）：树 + 详情列表 / 图标视图 + 底部栏。
    /// </summary>
    /// <param name="view">「设置 → 查看」带来的界面骨架选择：树要不要、状态栏要不要、
    /// 开出来是列表还是图标。默认全开、默认列表 —— 与引入这一页之前完全一致。</param>
    /// <param name="showPreview">「视图 → 图像预览」那一档。默认开着 —— 这一格是<strong>新加的功能</strong>，
    /// 关掉它当默认等于没人知道有这件事；但它只在"恰好选中一个能解码的图片"时才占位置，
    /// 所以默认开着不会改变任何一格已有的版面（没选中图的时候这里什么都没有）。</param>
    public ArchiveView(LangCatalog lang, ArchiveListing listing, ViewDefaults? view = null,
        IEnumerable<string>? hiddenColumns = null, bool showPreview = true)
    {
        _lang = lang;
        _listing = listing;
        _view = view ?? ViewDefaults.Fallback;
        _showPreview = showPreview;
        _root = ArchiveHierarchy.Build(listing);
        _current = _root;
        _columns = BuildColumns(lang);
        _shown = Shown(_columns, hiddenColumns);

        _listToggle = ViewBar.Toggle(Icons.ListMode, lang.Label("TEXT_VIEW_DETAILS"), () => SetIconView(false));
        _iconToggle = ViewBar.Toggle(Icons.IconMode, lang.Label("TEXT_VIEW_ICONS"), () => SetIconView(true));
        _locateButton = ViewBar.Toggle(Icons.Locate, lang.Label("TEXT_OPEN_ARCHIVE_LOCATION"), () => RevealArchive());

        // 图标视图要在建树之前定下来：Render() 会按它生成详情区内容。
        _iconView = _view.IconViewByDefault;

        BuildTree();
        Render();
        UpdateSummary();
        Root = BuildLayout();
    }

    /// <summary>整块内容（树 + 详情 + 底部栏），挂到主窗口的内容区。</summary>
    public Control Root { get; }

    /// <summary>
    /// 换掉"关掉了哪些列"并<strong>就地</strong>重画（「视图 → 列设置」勾一下就走这条路）。
    /// <para>不重建整份视图：重建会把当前所在的文件夹、展开状态与选中项一起丢掉，
    /// 而"把 CRC 那一列关掉"不该让用户回到根目录重找一遍自己刚才在看的东西。</para>
    /// </summary>
    public void SetHiddenColumns(IEnumerable<string> hiddenColumns)
    {
        _shown = Shown(_columns, hiddenColumns);
        Render();
    }

    /// <summary>
    /// 「视图 → 图像预览」当场勾上 / 取消。与 <see cref="SetHiddenColumns"/> 同一条规矩：
    /// 就地改，不重建 —— 勾一格预览不该把用户当前所在的文件夹与选中项一起丢掉。
    /// </summary>
    public void SetPreviewVisible(bool visible)
    {
        if (_showPreview == visible)
        {
            return;
        }

        _showPreview = visible;
        UpdatePreview();
    }

    // ---------- 布局 ----------

    private Control BuildLayout()
    {
        // SimpleTheme 给 TreeView 的模板自带一圈描边（实测 #888888、1 DIP，1.5 缩放下就是
        // 屏幕上那根 2px 实线）。这一屏两栏之间只该有一根发丝分栏线（规范 §5.1），
        // 所以整圈去掉 —— 1.8.0 那次"左栏线太粗"只改淡了分栏线，这一圈漏掉了。
        _tree.Background = Brushes.Transparent;
        _tree.BorderThickness = new Thickness(0);

        // 预览钉在左栏底下（用户给的参考图就是那一格：树在上、图在下）。
        // DockPanel 里钉住的那一头要<strong>先加</strong>，吃剩余的那一个最后加 —— 顺序反了
        // 树会把预览挤出可视区，而不是预览把树压短。
        var preview = BuildPreviewPane();
        DockPanel.SetDock(preview, Dock.Bottom);
        _treePane.Child = new DockPanel { Children = { preview, _tree } };

        var detailPane = new Border { Child = _detailHost };

        _body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{TreePaneWidth},{SeamColumnWidth},*"),
            Margin = new Thickness(0, Design.Space.X1, 0, 0),
        };

        Grid.SetColumn(_treePane, 0);
        Grid.SetColumn(_splitter, 1);
        Grid.SetColumn(detailPane, 2);
        _body.Children.Add(_treePane);
        _body.Children.Add(_splitter);
        _body.Children.Add(detailPane);

        _status.IsVisible = _view.ShowStatusBar;

        var panel = new DockPanel();
        var bar = ViewBar.Create(_listToggle, _iconToggle, _locateButton, _status);
        DockPanel.SetDock(bar, Dock.Bottom);
        panel.Children.Add(bar);
        panel.Children.Add(_body);
        return panel;
    }

    /// <summary>
    /// 预览那一格。它<strong>平时不占位置</strong>（<c>IsVisible = false</c>）：
    /// 没选中图片时那一块是一片空白，而"左栏底下有一块永远空着"会被读成界面坏了一块。
    /// </summary>
    private Control BuildPreviewPane()
    {
        _previewFrame.Child = new Panel { Children = { _previewImage, _previewNote } };
        _previewFrame.IsVisible = false;
        return _previewFrame;
    }

    // ---------- 图像预览 ----------

    /// <summary>
    /// 按"现在选中的是谁"重画预览那一格。
    /// <para>只在<strong>恰好选中一个、且它的扩展名在图片表里</strong>时出现。多选不猜要看哪一张 ——
    /// 猜错的那一次，用户看到的是"另一张图"，而界面不会告诉他为什么。</para>
    /// <para>在途的请求一律作废：连着往下按方向键翻一屏照片时，前一张的解码还在后台跑，
    /// 晚到的那张会盖掉当前这张（画面停在刚路过的那一格上）。作废之后位图照样释放，
    /// 否则每一张被路过的图都留在内存里。</para>
    /// </summary>
    private void UpdatePreview()
    {
        _previewCts?.Cancel();
        _previewCts = null;

        var target = _showPreview ? PreviewTarget() : null;
        if (target is null)
        {
            ClearPreview();
            return;
        }

        _previewFrame.IsVisible = true;
        _previewImage.IsVisible = false;
        _previewNote.IsVisible = false;
        LoadPreview(target);
    }

    /// <summary>这一格该画谁：恰好选中一个、不是目录、扩展名在图片表里。否则 null。</summary>
    private ArchiveEntryInfo? PreviewTarget()
    {
        var rows = SelectedRows;
        return rows.Count == 1 && !rows[0].IsDirectory && IsImage(rows[0]) ? rows[0] : null;
    }

    private void ClearPreview()
    {
        _previewFrame.IsVisible = false;
        _previewImage.IsVisible = false;
        _previewImage.Source = null;
        _previewNote.IsVisible = false;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
    }

    private void LoadPreview(ArchiveEntryInfo entry)
    {
        var token = (_previewCts = new CancellationTokenSource()).Token;
        var sessionTask = OpenSession();
        var index = entry.Index;

        _ = Task.Run(async () =>
        {
            Bitmap? bitmap = null;
            string note = string.Empty;
            try
            {
                var session = await sessionTask;
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (session is null)
                {
                    note = _lang.Label("TEXT_PREVIEW_UNREADABLE");
                }
                else
                {
                    var bytes = session.ReadEntry(index, PreviewSourceLimit);
                    if (bytes is not { Length: > 0 })
                    {
                        // 引擎按"解压出来多大"拒的，所以这句说的是解压后的体积，不是包里那一格的压缩尺寸。
                        note = _lang.Label("TEXT_PREVIEW_TOO_BIG");
                    }
                    else
                    {
                        using var stream = new MemoryStream(bytes);
                        bitmap = Bitmap.DecodeToWidth(stream, PreviewDecodeWidth, BitmapInterpolationMode.MediumQuality);
                    }
                }
            }
            catch (Exception)
            {
                bitmap?.Dispose();
                bitmap = null;
                note = _lang.Label("TEXT_PREVIEW_FAILED");
            }

            if (bitmap is null && note.Length == 0)
            {
                return;
            }

            var decoded = bitmap;
            var caption = note;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    decoded?.Dispose();
                    return;
                }

                _previewBitmap?.Dispose();
                _previewBitmap = decoded;
                _previewImage.Source = decoded;
                _previewImage.IsVisible = decoded is not null;
                _previewNote.Text = caption;
                _previewNote.IsVisible = decoded is null && caption.Length > 0;
            });
        });
    }

    private void BuildTree()
    {
        var rootItem = CreateTreeItem(_root, Path.GetFileName(_listing.ArchivePath));
        rootItem.IsExpanded = true;
        _tree.Items.Add(rootItem);

        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: ArchiveFolderNode node })
            {
                Navigate(node);
            }
        };

        rootItem.IsSelected = true;
    }

    private TreeViewItem CreateTreeItem(ArchiveFolderNode node, string header)
    {
        var item = new TreeViewItem
        {
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { Icons.Folder(Design.IconSize.Inline), new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center } },
            },
            Tag = node,
        };
        _treeItems[node] = item;

        // 根节点始终展开（不展开就什么都看不见），这一项管的是里面那些子文件夹
        // 要不要预先摊开。关掉它，深包打开时是一棵收起来的树，一层层自己点。
        item.IsExpanded = _view.AutoExpandTree;

        foreach (var child in node.Folders)
        {
            item.Items.Add(CreateTreeItem(child, child.Name));
        }

        return item;
    }

    // ---------- 导航 ----------

    private void Navigate(ArchiveFolderNode node)
    {
        // 重入守卫：这里给树写入选中状态，会同步回调到 _tree.SelectionChanged，
        // 而那一刻 TreeView 报回来的"当前选中项"可能还是**上一个**节点 ——
        // 于是那次回调会把刚定下来的 _current 又改回去。用户点树的时候看不出问题
        // （事件报的就是被点的那一项），程序化跳层（查找窗口的「定位」）必然被弹回。
        if (ReferenceEquals(node, _current) || _navigating)
        {
            return;
        }

        _navigating = true;
        try
        {
            // 上一项要**显式取消选中**，不能只指望"给下一项写 true 就会把它挤掉"。
            // TreeView 的单选是在容器被重新实现时才回放的：查找窗口的「定位」把选中写进树之后
            // 一关掉，主窗口重新拿到焦点、根节点那一格被重新实现，它带着自己那份"我是选中的"
            // 回来 —— 于是列表还在 docs/notes，树却把高亮退回压缩包根，两边各说各话。
            if (_treeItems.TryGetValue(_current, out var leaving))
            {
                leaving.IsSelected = false;
            }

            _current = node;
            if (_treeItems.TryGetValue(node, out var item))
            {
                // 从根一路摊到这一层。只摊最后那一格没有用：父级收着的时候，它压根不在屏幕上，
                // 选中与滚动都落不到它身上。
                var chain = new List<string>();
                for (var path = node.Path; path.Length > 0;)
                {
                    chain.Add(path);
                    var cut = path.LastIndexOf('/');
                    path = cut < 0 ? string.Empty : path[..cut];
                }

                chain.Reverse();
                foreach (var ancestor in chain)
                {
                    if (FindFolder(_root, ancestor) is { } folder && _treeItems.TryGetValue(folder, out var cell))
                    {
                        cell.IsExpanded = true;
                    }
                }

                item.IsSelected = true;
                item.BringIntoView();
            }

            Render();
            UpdateSummary();
        }
        finally
        {
            _navigating = false;
        }
    }

    /// <summary>
    /// 把某一项<strong>在主列表里选到并滚过去</strong>（查找窗口的「定位」）。
    /// <para>三件事都要做，少一件用户都会以为没反应：先跳到它所在的<strong>那一层文件夹</strong>
    /// （列表只显示当前层的成员，深层的东西不跳层根本不在屏幕上）、
    /// 图标视图下先换回详情列表（图标页没有"选中一行"这回事）、最后选中并滚动到位。</para>
    /// </summary>
    public bool RevealEntry(ArchiveEntryInfo entry)
    {
        var folder = FindFolder(_root, ArchiveHierarchy.ParentPathOf(entry.Path));
        if (folder is null)
        {
            return false;
        }

        if (_iconView)
        {
            SetIconView(false);
        }

        Navigate(folder);

        if (_detailList is not { } list)
        {
            return false;
        }

        // 只能给 SelectedItem：多选模式下 SelectedItems 是只读的（"fixed size selection"），
        // 而定位要的恰恰是"只选它一个"。
        list.SelectedItem = entry;
        list.ScrollIntoView(entry);
        UpdateSelectionSummary(list);
        return true;
    }

    /// <summary>
    /// 现在显示的是哪一层、选中了哪几行。
    /// <para>存在的理由只有一个：headless 里换层之后详情区那张新列表要等一次真布局才会
    /// 挂进可视树，测试从窗口往下找只能找到旧的那张（真机没这个问题）。
    /// 于是"定位到底跳没跳层、选没选上"这件事改由视图自己回答，
    /// 屏幕上到底长什么样交给发布前的真机核对那一步（清单里有那一条）。</para>
    /// </summary>
    internal string CurrentFolderPath => _current.Path;

    /// <summary>这个视图摆的是哪一份清单。给界面用例用：要按名字选中某一行，就得先拿到那一行。</summary>
    internal ArchiveListing Listing => _listing;

    internal IReadOnlyList<ArchiveEntryInfo> SelectedRows
        => _detailList?.SelectedItems?.OfType<ArchiveEntryInfo>().ToArray() ?? [];

    /// <summary>选择变了（多了、少了、换了一层）。窗口订阅它来重算「编辑」那几项的启用状态。</summary>
    internal event Action? SelectionChanged;

    /// <summary>
    /// 跳到某一层。改完编辑重建视图时用来自动回到用户原来待的那一层 ——
    /// 删掉深层的一个文件后把人扔回根目录，是在惩罚他做了件对的事。
    /// <para>找不到那一层就留在根：它可能正是刚被删掉的那一层。</para>
    /// </summary>
    internal void ShowFolder(string folderPath)
    {
        if (folderPath.Length > 0 && FindFolder(_root, folderPath) is { } folder)
        {
            Navigate(folder);
        }
    }

    private ArchiveFolderNode? FindFolder(ArchiveFolderNode node, string path)
    {
        if (string.Equals(node.Path, path, StringComparison.Ordinal))
        {
            return node;
        }

        return node.Folders
            .Select(child => FindFolder(child, path))
            .FirstOrDefault(hit => hit is not null);
    }

    private void Render()
    {
        // 旧的缩略图请求作废：切目录后不能让上一个目录的图落到新行上。
        _navigateCts?.Cancel();
        _navigateCts = new CancellationTokenSource();

        _detailHost.Content = _iconView ? BuildIconView() : BuildListView();
        ViewBar.Highlight(_listToggle, !_iconView);
        ViewBar.Highlight(_iconToggle, _iconView);

        // 换层（或换列表/图标视图）之后选中是空的，预览要跟着收起来：留着上一张，
        // 用户看到的是"我已经在另一个文件夹里了，图还是刚才那张"，读起来像界面卡住。
        UpdatePreview();
    }

    private void SetIconView(bool icon)
    {
        if (_iconView == icon)
        {
            return;
        }

        _iconView = icon;
        Render();
    }

    /// <summary>当前目录要显示的行：<c>..</c>（非根时，恒在首行）+ 排序后的子项。</summary>
    private List<ArchiveEntryInfo> CurrentRows()
    {
        var rows = new List<ArchiveEntryInfo>(_current.Rows);
        var compare = _columns[_sortColumn].Compare;
        if (compare is not null)
        {
            rows.Sort(compare);
            if (!_sortAscending)
            {
                rows.Reverse();
            }
        }

        if (!_current.IsRoot)
        {
            rows.Insert(0, new ArchiveEntryInfo(_current.Parent!.Path, "..", true, 0, 0, false, null, null));
        }

        return rows;
    }

    private void OpenRow(ArchiveEntryInfo row)
    {
        if (row.Name == "..")
        {
            Navigate(_current.Parent!);
            return;
        }

        if (row.IsDirectory)
        {
            var target = _current.Folders.FirstOrDefault(f => string.Equals(f.Name, row.Name, StringComparison.Ordinal));
            if (target is not null)
            {
                Navigate(target);
            }
        }

        // 双击文件（解压到临时目录 + 交给系统关联程序）尚未实现，这里刻意不响应。
    }

    // ---------- 详情列表 ----------

    private Control BuildListView()
    {
        // 表头不能再套一层 ScrollViewer：那层的 PointerPressed 处理会跟列分隔条的拖拽抢事件，
        // 而且表头本身不需要滚动条 —— 把它平移到位移量上、外面用一个裁剪边框拦住就够了。
        var header = BuildHeader();
        var shift = new TranslateTransform();
        header.RenderTransform = shift;
        var headerHost = new Border
        {
            ClipToBounds = true,
            Height = 32,
            Background = Design.Surface.Muted,
            BorderBrush = Design.Line.Hair,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header,
        };

        var list = new ListBox
        {
            ItemsSource = CurrentRows(),
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = RowTemplate.Of<ArchiveEntryInfo>(BuildRow, recycle: true),
            Background = Design.Surface.Base,
            BorderThickness = new Thickness(0),
        };

        // ItemsControl 默认不允许横向溢出，于是超宽的列既滚不到、也不出滚动条 —— 右侧那几列
        // 就等于不存在。显式打开 Auto，行内容超出视口时由列表自己的 ScrollViewer 裁剪并出条。
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);

        list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            // 列表横向滚多少，表头就往反方向平移多少；纵向仍由 ListBox 自己虚拟化。
            if (e.Source is ScrollViewer source)
            {
                shift.X = -source.Offset.X;
            }
        });

        list.SelectionChanged += (_, _) => UpdateSelectionSummary(list);
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is ArchiveEntryInfo row)
            {
                OpenRow(row);
            }
        };

        // 查找窗口的「定位」要能选到某一行，所以留一份当前那张列表的引用。
        // 每次 Render() 都会重建详情区，因此这个字段跟着重建，不能只赋值一次。
        _detailList = list;

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(headerHost, 0);
        Grid.SetRow(list, 1);
        grid.Children.Add(headerHost);
        grid.Children.Add(list);

        return grid;
    }

    private Control BuildHeader()
    {
        var header = new Grid
        {
            ColumnDefinitions = BuildColumnDefinitions(),
            VerticalAlignment = VerticalAlignment.Top,
        };

        // 画的是 _shown 里那几列，但每一格都带着自己的<strong>真实列号</strong>（index）：
        // 排序状态、列宽、菜单里的勾选都以真实列号为身份 —— 用"第几格"当身份，
        // 勾掉一列就会让记住的宽度与排序整体错位一格。
        for (var position = 0; position < _shown.Length; position++)
        {
            var index = _shown[position];
            var cell = new Button
            {
                Content = new TextBlock
                {
                    Text = _columns[index].Title,
                    FontSize = Design.Type.Body,
                    FontWeight = Design.Type.Semibold,
                    Foreground = PrimaryText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                // 表头是贴边的，圆角会在角上露出底色缝 —— 规范 4.1 里明写的两处例外之一。
                Background = Design.Surface.Muted,
                BorderThickness = new Thickness(0, 0, 1, 0),
                BorderBrush = Design.Line.Subtle,
                Padding = new Thickness(Design.Space.X2, 0, Design.Space.X1, 0),
                CornerRadius = Design.Radius.None,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
            }.PinFace();

            if (_columns[index].Compare is not null)
            {
                cell.Cursor = new Cursor(StandardCursorType.Hand);
                cell.Click += (_, _) => SortBy(index);
            }

            if (index == _sortColumn && _columns[index].Compare is not null)
            {
                ((TextBlock)cell.Content).Text += _sortAscending ? "  ▲" : "  ▼";
            }

            Grid.SetColumn(cell, ContentColumn(position));
            header.Children.Add(cell);

            if (index < _columnWidths.Length)
            {
                var splitter = new GridSplitter
                {
                    Width = SplitterWidth,
                    Background = Brushes.Transparent,
                    ResizeDirection = GridResizeDirection.Columns,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Cursor = new Cursor(StandardCursorType.SizeWestEast),
                };
                splitter.DragCompleted += (_, _) => CommitColumnWidths(header, position);
                Grid.SetColumn(splitter, ContentColumn(position) + 1);
                header.Children.Add(splitter);
            }
        }

        return header;
    }

    /// <summary>
    /// 列布局：每条内容列后面跟一条分隔列，末尾再挂一条 <c>*</c> 填充列吃掉剩余宽度。
    /// 用对象而不是拼字符串，是为了给每列带上 <see cref="ColumnDefinition.MinWidth"/> ——
    /// 拖拽时免得把某一列拖没。每个 Grid 都要拿到自己的实例（不可跨 Grid 共享）。
    /// </summary>
    private ColumnDefinitions BuildColumnDefinitions()
    {
        var definitions = new ColumnDefinitions();
        foreach (var index in _shown)
        {
            // 末尾那条填充列不吃宽度：它占的就是最后那一列星号，所以这里跳过它。
            if (index >= _columnWidths.Length)
            {
                continue;
            }

            definitions.Add(new ColumnDefinition(new GridLength(_columnWidths[index], GridUnitType.Pixel))
            {
                MinWidth = MinimumColumnWidth,
            });
            definitions.Add(new ColumnDefinition(new GridLength(SplitterWidth, GridUnitType.Pixel))
            {
                MinWidth = SplitterWidth,
            });
        }

        definitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        return definitions;
    }

    /// <summary>第 index 列的内容落在网格的第几列（分隔列夹在中间）。</summary>
    private static int ContentColumn(int index) => index * 2;

    /// <summary>
    /// 松手之后才把宽度落到 <see cref="_columnWidths"/> 并重画列表。拖的过程中只有
    /// 表头那两格在动 —— 每帧重建整个列表会让拖动本身卡顿。
    /// <para>
    /// 取两个列是因为 Avalonia 的 GridSplitter 只有「与隔壁交换宽度」这一种语义
    /// （<see cref="GridResizeBehavior.PreviousAndNext"/>）：拖宽左边就等于拖窄右边。
    /// 重画时表头也按 <see cref="_columnWidths"/> 重建，所以被 MinWidth 拦下来的
    /// 那一列会回到一致的值，不会出现表头与内容错位。
    /// </para>
    /// </summary>
    private void CommitColumnWidths(Grid header, int position)
    {
        RememberWidth(header, position);
        RememberWidth(header, position + 1);
        Render();
    }

    private void RememberWidth(Grid header, int position)
    {
        if (position >= _shown.Length || _shown[position] >= _columnWidths.Length)
        {
            return;
        }

        var index = _shown[position];
        var width = header.ColumnDefinitions[ContentColumn(position)].Width.Value;
        if (double.IsFinite(width) && width >= MinimumColumnWidth)
        {
            _columnWidths[index] = width;
        }
    }

    private void SortBy(int index)
    {
        if (_columns[index].Compare is null)
        {
            return;
        }

        if (_sortColumn == index)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = index;
            _sortAscending = true;
        }

        Render();
    }

    private Control BuildRow(ArchiveEntryInfo entry)
    {
        var row = new Grid
        {
            ColumnDefinitions = BuildColumnDefinitions(),
            Height = 28,
        };

        for (var position = 0; position < _shown.Length; position++)
        {
            var text = new TextBlock
            {
                Text = _columns[_shown[position]].Text(entry),
                FontSize = Design.Type.Body,
                LineHeight = Design.Type.BodyLineHeight,
                Foreground = position == 0 ? PrimaryText : SecondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Control cell = text;
            if (position == 0)
            {
                // 名称列是「图标 + 文字」。这里必须用 Grid 而不是 StackPanel：StackPanel 测量
                // 子项时给的是无限宽度，TextBlock 因此永远不会被约束，长名字会直接压到隔壁列上。
                var name = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                var glyph = entry.IsDirectory
                    ? Icons.Folder(Design.IconSize.Row)
                    : Icons.File(Design.IconSize.Row);
                glyph.Margin = new Thickness(0, 0, Design.Space.X2 - 2, 0);
                name.Children.Add(glyph);
                Grid.SetColumn(text, 1);
                name.Children.Add(text);
                cell = name;
            }

            Grid.SetColumn(cell, ContentColumn(position));
            row.Children.Add(cell);
        }

        return row;
    }

    // ---------- 图标视图 ----------

    private Control BuildIconView()
    {
        var rows = CurrentRows();
        var allowThumbnails = rows.Count <= ThumbnailEntryLimit;

        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(Design.Space.X2),
        };
        foreach (var row in rows)
        {
            panel.Children.Add(BuildIconCell(row, allowThumbnails));
        }

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Design.Surface.Base,
        };
    }

    private Control BuildIconCell(ArchiveEntryInfo entry, bool allowThumbnails)
    {
        var placeholder = entry.IsDirectory
            ? Icons.Folder(Design.IconSize.Tile)
            : Icons.File(Design.IconSize.Tile);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;

        var image = new Image { Width = 96, Height = 96, Stretch = Stretch.Uniform };

        // 缩略图区自己一层圆角（Sm），卡片外层再一层（Md）—— 内层必须比外层小，
        // 否则两级圆角看着一样大，会显出"双层边"（规范 4.1）。
        var frame = new Border
        {
            Width = 100,
            Height = 100,
            Background = Design.Surface.Sunken,
            CornerRadius = Design.Radius.Sm,
            Child = new Panel { Children = { placeholder, image } },
        };

        if (!entry.IsDirectory && allowThumbnails && IsImage(entry))
        {
            LoadThumbnail(placeholder, image, entry);
        }

        var cell = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = 126,
            Margin = new Thickness(Design.Space.X2, Design.Space.X2, Design.Space.X2, Design.Space.X4),
            Spacing = Design.Space.X2,
            Children =
            {
                frame,
                new TextBlock
                {
                    Text = entry.Name,
                    FontSize = Design.Type.Caption,
                    Foreground = PrimaryText,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 38,
                },
            },
        };

        cell.DoubleTapped += (_, _) => OpenRow(entry);
        return cell;
    }

    private static bool IsImage(ArchiveEntryInfo entry)
        => ImageExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 那份长驻的读取会话，<strong>惰建且只建一次</strong>：缩略图与图像预览共用一个句柄 ——
    /// 一个句柄同一时刻只能被一个线程碰，多开一个只是多排队一份，而清单已经在盘上了。
    /// <para>建失败记成 <c>null</c> 而不是抛：这一层在后台线程上，抛出去的异常没人接，
    /// 而"预览不出来"该在界面上是一句话，不是一根红条（<see cref="LoadPreview"/> 读这个 null）。</para>
    /// </summary>
    private Task<ArchiveContentSession?> OpenSession()
        => _session ??= Task.Run(() =>
        {
            try
            {
                return ArchiveContentSession.Open(_listing.ArchivePath);
            }
            catch (Exception)
            {
                return null;
            }
        });

    /// <summary>后台回读条目字节并解码缩略图。任何失败都只留下通用图标，不打扰用户。</summary>
    private void LoadThumbnail(Control placeholder, Image target, ArchiveEntryInfo entry)
    {
        var token = (_navigateCts ??= new CancellationTokenSource()).Token;
        var sessionTask = OpenSession();
        _ = Task.Run(async () =>
        {
            Bitmap? bitmap = null;
            try
            {
                var session = await sessionTask;
                if (session is null || token.IsCancellationRequested)
                {
                    return;
                }

                var bytes = session.ReadEntry(entry.Index, ThumbnailSourceLimit);
                if (bytes is { Length: > 0 } && !token.IsCancellationRequested)
                {
                    using var stream = new MemoryStream(bytes);
                    bitmap = Bitmap.DecodeToWidth(stream, 128, BitmapInterpolationMode.MediumQuality);
                }
            }
            catch (Exception)
            {
                bitmap?.Dispose();
                return;
            }

            if (bitmap is null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }

                placeholder.IsVisible = false;
                target.Source = bitmap;
            });
        });
    }

    // ---------- 状态栏 ----------

    private void UpdateSummary()
    {
        var size = _listing.ArchiveSize > 0 ? _listing.ArchiveSize : _listing.TotalSize;
        _status.Text = _lang.Format(
            "TEXT_STATUS_ARCHIVE_SUMMARY",
            ("files", _listing.FileCount.ToString()),
            ("folders", _listing.DirectoryCount.ToString()),
            ("size", SizeText.Format(size)));
    }

    private void UpdateSelectionSummary(ListBox list)
    {
        // 「编辑」那几项是按选择起落的（2 项改名、1 项以上才谈得上删/复制），
        // 所以选择一变就得让窗口重算一次菜单 —— 这是视图往外说的唯一一句话。
        SelectionChanged?.Invoke();

        // 预览读的也是"现在选中的是谁"，所以挂在同一个地方：选择会变的地方有四处
        // （点行、点树、导航、定位），逐个去补必然漏掉一两处。
        UpdatePreview();

        var selected = list.SelectedItems?.OfType<ArchiveEntryInfo>().Where(e => !e.IsDirectory).ToList() ?? [];
        if (selected.Count == 0)
        {
            UpdateSummary();
            return;
        }

        var size = selected.Sum(e => e.Size);
        var compressed = selected.Sum(e => e.CompressedSize);
        var percent = size > 0 ? (int)Math.Round(compressed * 100.0 / size) : 0;
        _status.Text = _lang.Format(
            "TEXT_STATUS_SELECTED_SUMMARY",
            ("files", selected.Count.ToString()),
            ("size", size.ToString()),
            ("compressed", compressed.ToString()),
            ("percent", percent.ToString()));
    }

    // ---------- 打开压缩文件位置 ----------

    /// <summary>
    /// 在系统文件管理器里定位压缩包（底部栏的第三个按钮）。
    /// 实现挪到 <see cref="Services.ArchiveReveal"/>，与「文件 → 打开压缩文件位置」共用一份 ——
    /// 顺带修掉那里的一处写法问题（<c>/select,</c> 与路径被当成一个参数交给 explorer，
    /// 它认不出来，结果是"打开了目录但没选中文件"）。
    /// </summary>
    private void RevealArchive() => Services.ArchiveReveal.Reveal(_listing.ArchivePath);

    // ---------- 列与文案 ----------

    private static ColumnSpec[] BuildColumns(LangCatalog lang) =>
    [
        new("name", lang.Label("TEXT_COLUMN_NAME"), e => e.Name, (a, b) => string.CompareOrdinal(a.Name, b.Name)),
        new("compressed-size", lang.Label("TEXT_COLUMN_COMPRESSED_SIZE"), e => e.IsDirectory ? string.Empty : e.CompressedSize.ToString("N0"), Number(e => e.CompressedSize)),
        new("original-size", lang.Label("TEXT_COLUMN_ORIGINAL_SIZE"), e => e.IsDirectory ? string.Empty : e.Size.ToString("N0"), Number(e => e.Size)),
        new("type", lang.Label("TEXT_COLUMN_TYPE"), e => TypeText(lang, e), (a, b) => string.CompareOrdinal(TypeText(lang, a), TypeText(lang, b))),
        new("modified", lang.Label("TEXT_COLUMN_MODIFIED"), ModifiedText,
            (a, b) => DateTime.Compare(a.Modified ?? default, b.Modified ?? default)),
        new("method", lang.Label("TEXT_LABEL_COMPRESSION_METHOD"), e => e.Method ?? string.Empty,
            (a, b) => string.CompareOrdinal(a.Method ?? string.Empty, b.Method ?? string.Empty)),
        new("encryption", lang.Label("TEXT_LABEL_ENCRYPTION_METHOD"), e => e.IsEncrypted ? lang.Label("TEXT_ENCRYPTED_FLAG") : string.Empty, null),
        new("crc", lang.Label("TEXT_COLUMN_CRC"), e => e.Crc?.ToString("x8") ?? string.Empty, null),
        new("attributes", lang.Label("TEXT_COLUMN_ATTRIBUTES"), AttributeText, null),
        new("comment", lang.Label("TEXT_COLUMN_COMMENT"), e => e.Comment ?? string.Empty, null),
        new("extra", lang.Label("TEXT_COLUMN_EXTRA"), e => ExtraText(lang, e), null),
        new(string.Empty, string.Empty, _ => string.Empty, null),
    ];

    /// <summary>
    /// 「视图 → 列设置」那一排要列出来的列：<strong>名称列永远在，末尾那条吃剩余宽度的填充列不算</strong>。
    /// <para>标题与表头读的是同一份 <see cref="BuildColumns"/> —— 菜单里写「修改日期」而表头写「修改时间」
    /// 这种错，在这里结构上就发生不了。</para>
    /// </summary>
    public static IReadOnlyList<(string Key, string Title)> ToggleableColumns(LangCatalog lang)
        => [.. BuildColumns(lang)
            .Where(column => column.Key.Length > 0 && column.Key != "name")
            .Select(column => (column.Key, column.Title))];

    /// <summary>按"关掉了哪些"算出要画的下标表。空集合与 null 同义：全都显示。</summary>
    private static int[] Shown(ColumnSpec[] columns, IEnumerable<string>? hiddenColumns)
    {
        var hidden = new HashSet<string>(hiddenColumns ?? [], StringComparer.Ordinal);

        return [.. Enumerable.Range(0, columns.Length)
            .Where(i => columns[i].Key.Length == 0 || columns[i].Key == "name" || !hidden.Contains(columns[i].Key))];
    }

    private static Comparison<ArchiveEntryInfo> Number(Func<ArchiveEntryInfo, long> selector)
        => (a, b) => selector(a).CompareTo(selector(b));

    /// <summary>日期列的写法。查找窗口用的是同一个，两处不许长得不一样。</summary>
    internal static string ModifiedText(ArchiveEntryInfo entry)
        => entry.Modified?.ToString("yyyy/M/d H:mm") ?? string.Empty;

    /// <summary>类型列：目录 → 「文件夹」；文件 → 「JPG 文件」；无扩展名 → 「文件」。</summary>
    /// <remarks>查找窗口那一列走的是同一个方法 —— 两处各写一份，改一处就会不一样。</remarks>
    internal static string TypeText(LangCatalog lang, ArchiveEntryInfo entry)
    {
        if (entry.IsDirectory)
        {
            return lang.Label("TEXT_TYPE_FOLDER");
        }

        var extension = Path.GetExtension(entry.Name);
        return string.IsNullOrEmpty(extension)
            ? lang.Label("TEXT_TYPE_FILE")
            : lang.Format("TEXT_TYPE_FILE_EXT", ("ext", extension.TrimStart('.').ToUpperInvariant()));
    }

    /// <summary>
    /// 属性列：参考软件用三个字符位显示 Win32 属性（<c>D__</c> / <c>A__</c> / <c>R__</c>…）。
    /// 第一位是目录位（否则取存档位），后两位是只读与隐藏。
    /// <para>
    /// 只看低 16 位：ZIP 的外部属性字段高 16 位装的是 Unix mode（Python 的 zipfile 就写
    /// <c>081A00000</c> 这种值），整字去测 D/A 位会一个都测不到。
    /// </para>
    /// <para>格式没存属性时按 Windows 建条目时的默认值补（目录 D、文件 A），留一列空白没有意义。</para>
    /// </summary>
    private static string AttributeText(ArchiveEntryInfo entry)
    {
        const uint readOnlyFlag = 0x01, hiddenFlag = 0x02, directoryFlag = 0x10, archiveFlag = 0x20;

        var stored = (entry.Attributes ?? 0) & 0xFFFF;
        var attributes = stored != 0 ? stored : (entry.IsDirectory ? directoryFlag : archiveFlag);
        var first = (attributes & directoryFlag) != 0 ? 'D' : (attributes & archiveFlag) != 0 ? 'A' : '_';
        var second = (attributes & readOnlyFlag) != 0 ? 'R' : '_';
        var third = (attributes & hiddenFlag) != 0 ? 'H' : '_';
        return string.Concat(first, second, third);
    }

    /// <summary>
    /// 附加信息列：参考软件显示 “OS:Dos, UTF8…”。引擎不报「写入方操作系统」，
    /// 所以这里按**实际拿到的字段**推断：带 POSIX 权限位 → Unix 系；带 Win32 属性 → Windows/DOS 系。
    /// </summary>
    private static string ExtraText(LangCatalog lang, ArchiveEntryInfo entry)
    {
        var os = entry.PosixMode != 0
            ? lang.Label("TEXT_EXTRA_OS_UNIX")
            : entry.Attributes is not null
                ? lang.Label("TEXT_EXTRA_OS_WINDOWS")
                : null;

        var unicode = entry.Name.Any(c => c > 0x7F) ? lang.Label("TEXT_EXTRA_UTF8") : null;
        return string.Join(", ", new[] { os, unicode }.Where(s => !string.IsNullOrEmpty(s)));
    }

    // 图标几何已经收进 Icons.cs（规范 6.2：路径只有一个来源）。

    public void Dispose()
    {
        _navigateCts?.Cancel();
        _navigateCts?.Dispose();

        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewBitmap?.Dispose();
        _previewBitmap = null;

        // 会话可能还在建立中；等它落地再释放，避免留下未关闭的归档句柄。
        _session?.ContinueWith(
            static task => { try { task.Result?.Dispose(); } catch { /* 没建起来，没什么可释放 */ } },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>一列的定义：表头文案、单元格文本、可选比较器（null 表示该列不可排序）。</summary>
    private sealed record ColumnSpec(string Key, string Title, Func<ArchiveEntryInfo, string> Text, Comparison<ArchiveEntryInfo>? Compare);
}
