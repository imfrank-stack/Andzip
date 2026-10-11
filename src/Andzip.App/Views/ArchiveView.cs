using System.Diagnostics;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
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
    /// <summary>列宽。每个 Grid 必须拿到自己的实例（<see cref="ColumnDefinitions"/> 不可跨 Grid 共享）。</summary>
    private const string ColumnLayout = "160,96,96,110,120,96,96,140,60,80,130,*";

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

    private readonly TreeView _tree = new();
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

    public ArchiveView(LangCatalog lang, ArchiveListing listing)
    {
        _lang = lang;
        _listing = listing;
        _root = ArchiveHierarchy.Build(listing);
        _current = _root;
        _columns = BuildColumns(lang);

        _listToggle = ViewBar.Toggle(ViewIcon.List, lang.Get("TEXT_VIEW_DETAILS"), () => SetIconView(false));
        _iconToggle = ViewBar.Toggle(ViewIcon.Grid, lang.Get("TEXT_VIEW_ICONS"), () => SetIconView(true));
        _locateButton = ViewBar.Toggle(ViewIcon.Folder, lang.Get("TEXT_OPEN_ARCHIVE_LOCATION"), () => RevealArchive());

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
        var treePane = new Border { Background = Brushes.White, Child = _tree };

        var splitter = new GridSplitter
        {
            Width = 4,
            Background = new SolidColorBrush(Color.Parse("#D6D6D6")),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var detailPane = new Border { Background = Brushes.White, Child = _detailHost };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("300,4,*") };
        Grid.SetColumn(treePane, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(detailPane, 2);
        body.Children.Add(treePane);
        body.Children.Add(splitter);
        body.Children.Add(detailPane);

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
        // 表头单独放在一个只横向滚动的 ScrollViewer 里，再把它与列表的横向偏移同步起来：
        // 列表纵向要虚拟化，不能整个塞进外层 ScrollViewer。
        var headerHost = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = BuildHeader(),
        };

        var list = new ListBox
        {
            ItemsSource = CurrentRows(),
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<ArchiveEntryInfo>((entry, _) => BuildRow(entry!), supportsRecycling: true),
            Background = Brushes.White,
            BorderThickness = new Thickness(0),
        };

        list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is ScrollViewer source)
            {
                headerHost.Offset = new Vector(source.Offset.X, 0);
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
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnLayout), Height = 24 };
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

            Grid.SetColumn(cell, index);
            header.Children.Add(cell);
        }

        return header;
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
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnLayout), Margin = new Thickness(0, 2, 0, 2) };

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

            if (i == 0)
            {
                // 名称列：图标 + 文字。
                var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                name.Children.Add(entry.IsDirectory ? Glyph(FolderGeometry, 14) : Glyph(FileGeometry, 14));
                name.Children.Add(text);
                Grid.SetColumn(name, 0);
                row.Children.Add(name);
                continue;
            }

            Grid.SetColumn(text, i);
            row.Children.Add(text);
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

    /// <summary>在系统文件管理器里定位压缩包（底部栏的第三个按钮）。</summary>
    private void RevealArchive()
    {
        var (file, argument) = OperatingSystem.IsWindows()
            ? ("explorer.exe", "/select," + _listing.ArchivePath)
            : OperatingSystem.IsMacOS()
                ? ("open", _listing.ArchivePath)
                : ("xdg-open", Path.GetDirectoryName(_listing.ArchivePath) ?? _listing.ArchivePath);

        try
        {
            // 路径来自文件选择框，可能含空格与引号，所以走 ArgumentList 而不是拼命令串。
            var startInfo = new ProcessStartInfo { FileName = file, UseShellExecute = true };
            startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
        }
        catch (Exception)
        {
            // 没有可用的文件管理器（精简系统 / 无桌面会话）时静默失败即可。
        }
    }

    // ---------- 列与文案 ----------

    private static ColumnSpec[] BuildColumns(LangCatalog lang) =>
    [
        new(lang.Get("TEXT_COLUMN_NAME"), e => e.Name, (a, b) => string.CompareOrdinal(a.Name, b.Name)),
        new(lang.Get("TEXT_COLUMN_COMPRESSED_SIZE"), e => e.IsDirectory ? string.Empty : e.CompressedSize.ToString("N0"), Number(e => e.CompressedSize)),
        new(lang.Get("TEXT_COLUMN_ORIGINAL_SIZE"), e => e.IsDirectory ? string.Empty : e.Size.ToString("N0"), Number(e => e.Size)),
        new(lang.Get("TEXT_COLUMN_TYPE"), e => TypeText(lang, e), (a, b) => string.CompareOrdinal(TypeText(lang, a), TypeText(lang, b))),
        new(lang.Get("TEXT_COLUMN_MODIFIED"), e => e.Modified?.ToString("yyyy/M/d H:mm") ?? string.Empty,
            (a, b) => DateTime.Compare(a.Modified ?? default, b.Modified ?? default)),
        new(lang.Get("TEXT_LABEL_COMPRESSION_METHOD"), e => e.Method ?? string.Empty,
            (a, b) => string.CompareOrdinal(a.Method ?? string.Empty, b.Method ?? string.Empty)),
        new(lang.Get("TEXT_LABEL_ENCRYPTION_METHOD"), e => e.IsEncrypted ? lang.Get("TEXT_ENCRYPTED_FLAG") : string.Empty, null),
        new(lang.Get("TEXT_COLUMN_CRC"), e => e.Crc?.ToString("x8") ?? string.Empty, null),
        new(lang.Get("TEXT_COLUMN_ATTRIBUTES"), AttributeText, null),
        new(lang.Get("TEXT_COLUMN_COMMENT"), e => e.Comment ?? string.Empty, null),
        new(lang.Get("TEXT_COLUMN_EXTRA"), e => ExtraText(lang, e), null),
        new(string.Empty, _ => string.Empty, null),
    ];

    private static Comparison<ArchiveEntryInfo> Number(Func<ArchiveEntryInfo, long> selector)
        => (a, b) => selector(a).CompareTo(selector(b));

    /// <summary>类型列：目录 → 「文件夹」；文件 → 「JPG 文件」；无扩展名 → 「文件」。</summary>
    private static string TypeText(LangCatalog lang, ArchiveEntryInfo entry)
    {
        if (entry.IsDirectory)
        {
            return lang.Get("TEXT_TYPE_FOLDER");
        }

        var extension = Path.GetExtension(entry.Name);
        return string.IsNullOrEmpty(extension)
            ? lang.Get("TEXT_TYPE_FILE")
            : lang.Format("TEXT_TYPE_FILE_EXT", ("ext", extension.TrimStart('.').ToUpperInvariant()));
    }

    /// <summary>
    /// 属性列：参考软件用三个字符位显示 Win32 属性（<c>D__</c> / <c>A__</c> / <c>R__</c>…）。
    /// 第一位是目录位（否则取存档位），后两位是只读与隐藏。格式没存属性时留空，不猜。
    /// </summary>
    private static string AttributeText(ArchiveEntryInfo entry)
    {
        const uint readOnlyFlag = 0x01, hiddenFlag = 0x02, directoryFlag = 0x10, archiveFlag = 0x20;

        // 很多包（尤其 ZIP 的简写入方）不存外部属性。这时按 Windows 建条目时的默认值补：
        // 目录补 D、文件补 A —— 参考软件也是这么显示的，留一列空白没有意义。
        var attributes = entry.Attributes ?? (entry.IsDirectory ? directoryFlag : archiveFlag);
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
            ? lang.Get("TEXT_EXTRA_OS_UNIX")
            : entry.Attributes is not null
                ? lang.Get("TEXT_EXTRA_OS_WINDOWS")
                : null;

        var unicode = entry.Name.Any(c => c > 0x7F) ? lang.Get("TEXT_EXTRA_UTF8") : null;
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
