using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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

    /// <summary>拖拽时的列宽下限，防止把某一列拖没。</summary>
    private const double MinimumColumnWidth = 48;

    /// <summary>超过这个条数就不做缩略图：WrapPanel 不虚拟化，几百张图会明显拖慢切换。</summary>
    private const int ThumbnailEntryLimit = 400;

    /// <summary>单张缩略图的解压上限；超过就按普通文件处理。</summary>
    private const long ThumbnailSourceLimit = 32L * 1024 * 1024;

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff"];

    private static readonly IBrush PrimaryText = new SolidColorBrush(Color.Parse("#1F1F1F"));
    private static readonly IBrush SecondaryText = new SolidColorBrush(Color.Parse("#6B6B6B"));

    private readonly LangCatalog _lang;
    private readonly ArchiveListing _listing;
    private readonly ArchiveFolderNode _root;
    private readonly Dictionary<ArchiveFolderNode, TreeViewItem> _treeItems = new();
    private readonly ColumnSpec[] _columns;
    private readonly double[] _columnWidths = [.. DefaultColumnWidths];
    private readonly ViewDefaults _view;

    private readonly TreeView _tree = new();
    private readonly Border _treePane = new() { Background = Brushes.White };
    private readonly GridSplitter _splitter = new()
    {
        Width = 4,
        Background = new SolidColorBrush(Color.Parse("#D6D6D6")),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
    };
    private readonly ContentControl _detailHost = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
    private readonly Button _listToggle;
    private readonly Button _iconToggle;
    private readonly Button _locateButton;

    private ArchiveFolderNode _current;
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
    public ArchiveView(LangCatalog lang, ArchiveListing listing, ViewDefaults? view = null)
    {
        _lang = lang;
        _listing = listing;
        _view = view ?? ViewDefaults.Fallback;
        _root = ArchiveHierarchy.Build(listing);
        _current = _root;
        _columns = BuildColumns(lang);

        _listToggle = ViewBar.Toggle(ViewIcon.List, lang.Label("TEXT_VIEW_DETAILS"), () => SetIconView(false));
        _iconToggle = ViewBar.Toggle(ViewIcon.Grid, lang.Label("TEXT_VIEW_ICONS"), () => SetIconView(true));
        _locateButton = ViewBar.Toggle(ViewIcon.Folder, lang.Label("TEXT_OPEN_ARCHIVE_LOCATION"), () => RevealArchive());

        // 图标视图要在建树之前定下来：Render() 会按它生成详情区内容。
        _iconView = _view.IconViewByDefault;

        BuildTree();
        Render();
        UpdateSummary();
        Root = BuildLayout();
    }

    /// <summary>整块内容（树 + 详情 + 底部栏），挂到主窗口的内容区。</summary>
    public Control Root { get; }

    // ---------- 布局 ----------

    private Control BuildLayout()
    {
        _treePane.Child = _tree;

        var detailPane = new Border { Background = Brushes.White, Child = _detailHost };

        // 关掉树的时候把两列宽度一起归零，只藏控件不够 —— Grid 仍会给 300px 与 4px 留位置，
        // 结果是一条拖不动也点不到的空白。
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(_view.ShowTree ? "300,4,*" : "0,0,*"),
        };
        _treePane.IsVisible = _view.ShowTree;
        _splitter.IsVisible = _view.ShowTree;

        Grid.SetColumn(_treePane, 0);
        Grid.SetColumn(_splitter, 1);
        Grid.SetColumn(detailPane, 2);
        body.Children.Add(_treePane);
        body.Children.Add(_splitter);
        body.Children.Add(detailPane);

        _status.IsVisible = _view.ShowStatusBar;

        var panel = new DockPanel();
        var bar = ViewBar.Create(_listToggle, _iconToggle, _locateButton, _status);
        DockPanel.SetDock(bar, Dock.Bottom);
        panel.Children.Add(bar);
        panel.Children.Add(body);
        return panel;
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
                Children = { Glyph(FolderGeometry, 16), new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center } },
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
        if (ReferenceEquals(node, _current))
        {
            return;
        }

        _current = node;
        if (_treeItems.TryGetValue(node, out var item))
        {
            item.IsSelected = true;
            item.IsExpanded = true;
            item.BringIntoView();
        }

        Render();
        UpdateSummary();
    }

    private void Render()
    {
        // 旧的缩略图请求作废：切目录后不能让上一个目录的图落到新行上。
        _navigateCts?.Cancel();
        _navigateCts = new CancellationTokenSource();

        _detailHost.Content = _iconView ? BuildIconView() : BuildListView();
        ViewBar.Highlight(_listToggle, !_iconView);
        ViewBar.Highlight(_iconToggle, _iconView);
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
            Height = 24,
            Child = header,
        };

        var list = new ListBox
        {
            ItemsSource = CurrentRows(),
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<ArchiveEntryInfo>((entry, _) => BuildRow(entry!), supportsRecycling: true),
            Background = Brushes.White,
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

        for (var i = 0; i < _columns.Length; i++)
        {
            var index = i;
            var cell = new Button
            {
                Content = new TextBlock
                {
                    Text = _columns[i].Title,
                    FontSize = 13,
                    Foreground = PrimaryText,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Background = new SolidColorBrush(Color.Parse("#FAFAFA")),
                BorderThickness = new Thickness(0, 0, 1, 1),
                BorderBrush = new SolidColorBrush(Color.Parse("#DCDCDC")),
                Padding = new Thickness(6, 0, 2, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            if (_columns[i].Compare is not null)
            {
                cell.Cursor = new Cursor(StandardCursorType.Hand);
                cell.Click += (_, _) => SortBy(index);
            }

            if (index == _sortColumn && _columns[i].Compare is not null)
            {
                ((TextBlock)cell.Content).Text += _sortAscending ? "  ▲" : "  ▼";
            }

            Grid.SetColumn(cell, ContentColumn(index));
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
                splitter.DragCompleted += (_, _) => CommitColumnWidths(header, index);
                Grid.SetColumn(splitter, ContentColumn(index) + 1);
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
        foreach (var width in _columnWidths)
        {
            definitions.Add(new ColumnDefinition(new GridLength(width, GridUnitType.Pixel))
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
    private void CommitColumnWidths(Grid header, int index)
    {
        RememberWidth(header, index);
        RememberWidth(header, index + 1);
        Render();
    }

    private void RememberWidth(Grid header, int index)
    {
        if (index >= _columnWidths.Length)
        {
            return;
        }

        var width = header.ColumnDefinitions[ContentColumn(index)].Width.Value;
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
            Margin = new Thickness(0, 2, 0, 2),
        };

        for (var i = 0; i < _columns.Length; i++)
        {
            var text = new TextBlock
            {
                Text = _columns[i].Text(entry),
                FontSize = 13,
                Foreground = i == 0 ? PrimaryText : SecondaryText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Control cell = text;
            if (i == 0)
            {
                // 名称列是「图标 + 文字」。这里必须用 Grid 而不是 StackPanel：StackPanel 测量
                // 子项时给的是无限宽度，TextBlock 因此永远不会被约束，长名字会直接压到隔壁列上。
                var name = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                var glyph = entry.IsDirectory ? Glyph(FolderGeometry, 14) : Glyph(FileGeometry, 14);
                glyph.Margin = new Thickness(0, 0, 5, 0);
                name.Children.Add(glyph);
                Grid.SetColumn(text, 1);
                name.Children.Add(text);
                cell = name;
            }

            Grid.SetColumn(cell, ContentColumn(i));
            row.Children.Add(cell);
        }

        return row;
    }

    // ---------- 图标视图 ----------

    private Control BuildIconView()
    {
        var rows = CurrentRows();
        var allowThumbnails = rows.Count <= ThumbnailEntryLimit;

        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        foreach (var row in rows)
        {
            panel.Children.Add(BuildIconCell(row, allowThumbnails));
        }

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.White,
        };
    }

    private Control BuildIconCell(ArchiveEntryInfo entry, bool allowThumbnails)
    {
        var placeholder = entry.IsDirectory
            ? Glyph(FolderGeometry, 72)
            : Glyph(FileGeometry, 72);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;

        var image = new Image { Width = 96, Height = 96, Stretch = Stretch.Uniform };

        var frame = new Border
        {
            Width = 100,
            Height = 100,
            Background = Brushes.White,
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
            Margin = new Thickness(4, 4, 4, 10),
            Children =
            {
                frame,
                new TextBlock
                {
                    Text = entry.Name,
                    FontSize = 13,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 38,
                },
            },
        };

        cell.DoubleTapped += (_, _) => OpenRow(entry);
        return cell;
    }

    private static bool IsImage(ArchiveEntryInfo entry)
        => ImageExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>后台回读条目字节并解码缩略图。任何失败都只留下通用图标，不打扰用户。</summary>
    private void LoadThumbnail(Control placeholder, Image target, ArchiveEntryInfo entry)
    {
        var token = (_navigateCts ??= new CancellationTokenSource()).Token;
        _session ??= Task.Run(() =>
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

        var sessionTask = _session;
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
        new(lang.Label("TEXT_COLUMN_NAME"), e => e.Name, (a, b) => string.CompareOrdinal(a.Name, b.Name)),
        new(lang.Label("TEXT_COLUMN_COMPRESSED_SIZE"), e => e.IsDirectory ? string.Empty : e.CompressedSize.ToString("N0"), Number(e => e.CompressedSize)),
        new(lang.Label("TEXT_COLUMN_ORIGINAL_SIZE"), e => e.IsDirectory ? string.Empty : e.Size.ToString("N0"), Number(e => e.Size)),
        new(lang.Label("TEXT_COLUMN_TYPE"), e => TypeText(lang, e), (a, b) => string.CompareOrdinal(TypeText(lang, a), TypeText(lang, b))),
        new(lang.Label("TEXT_COLUMN_MODIFIED"), e => e.Modified?.ToString("yyyy/M/d H:mm") ?? string.Empty,
            (a, b) => DateTime.Compare(a.Modified ?? default, b.Modified ?? default)),
        new(lang.Label("TEXT_LABEL_COMPRESSION_METHOD"), e => e.Method ?? string.Empty,
            (a, b) => string.CompareOrdinal(a.Method ?? string.Empty, b.Method ?? string.Empty)),
        new(lang.Label("TEXT_LABEL_ENCRYPTION_METHOD"), e => e.IsEncrypted ? lang.Label("TEXT_ENCRYPTED_FLAG") : string.Empty, null),
        new(lang.Label("TEXT_COLUMN_CRC"), e => e.Crc?.ToString("x8") ?? string.Empty, null),
        new(lang.Label("TEXT_COLUMN_ATTRIBUTES"), AttributeText, null),
        new(lang.Label("TEXT_COLUMN_COMMENT"), e => e.Comment ?? string.Empty, null),
        new(lang.Label("TEXT_COLUMN_EXTRA"), e => ExtraText(lang, e), null),
        new(string.Empty, _ => string.Empty, null),
    ];

    private static Comparison<ArchiveEntryInfo> Number(Func<ArchiveEntryInfo, long> selector)
        => (a, b) => selector(a).CompareTo(selector(b));

    /// <summary>类型列：目录 → 「文件夹」；文件 → 「JPG 文件」；无扩展名 → 「文件」。</summary>
    private static string TypeText(LangCatalog lang, ArchiveEntryInfo entry)
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

    // ---------- 图标 ----------

    private const string FolderGeometry = "M0,2 h6 l1.6,2.2 H16 V15 H0 z";
    private const string FileGeometry = "M0,0 h9 l5,5 V16 H0 z";

    private static ShapePath Glyph(string geometry, double size) => new()
    {
        Data = Geometry.Parse(geometry),
        Fill = geometry == FolderGeometry
            ? new SolidColorBrush(Color.Parse("#F7C948"))
            : new SolidColorBrush(Color.Parse("#F2F2F2")),
        Stroke = geometry == FolderGeometry
            ? new SolidColorBrush(Color.Parse("#D9A61F"))
            : new SolidColorBrush(Color.Parse("#9A9A9A")),
        StrokeThickness = Math.Max(0.7, size / 24),
        Width = size * (geometry == FolderGeometry ? 1 : 0.875),
        Height = size,
        Stretch = Stretch.Fill,
    };

    public void Dispose()
    {
        _navigateCts?.Cancel();
        _navigateCts?.Dispose();

        // 会话可能还在建立中；等它落地再释放，避免留下未关闭的归档句柄。
        _session?.ContinueWith(
            static task => { try { task.Result?.Dispose(); } catch { /* 没建起来，没什么可释放 */ } },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>一列的定义：表头文案、单元格文本、可选比较器（null 表示该列不可排序）。</summary>
    private sealed record ColumnSpec(string Title, Func<ArchiveEntryInfo, string> Text, Comparison<ArchiveEntryInfo>? Compare);
}
