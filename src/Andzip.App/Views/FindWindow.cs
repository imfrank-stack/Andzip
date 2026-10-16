using System.Linq;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 「查找 → 文件查找」（Ctrl+F）：在<strong>当前打开的这份清单</strong>里按名字找成员。
/// <para><strong>它是非模态的</strong>。查找这件事的天性是"搜一下、看一眼、再搜一下"，
/// 模态对话框会把中间那步"看一眼"挡掉 —— 主列表就在它后面，正是要看的东西。
/// 所以定位之后窗口留着，光标回到输入框，改一个词接着找。</para>
/// <para><strong>没有「删除」也没有「解压」</strong>。参考软件那一排是 删除 / 解压 / 关闭，
/// 但这两件我们做不到：往已有归档里删成员引擎不支持（第六节），
/// 而解压那条路只会整包解、不认"选中的这几项"。摆上去就是两颗假按钮，
/// 比少两颗按钮糟得多（0.9.12 删掉那两个下拉箭头是同一条判断）。</para>
/// <para>结果表列的是<strong>完整路径</strong>而不是文件名：匹配的就是完整路径，
/// 只显名字会让人对着一条 <code>a/b/readme.md</code> 想"我搜的 readme 在哪一层"。</para>
/// </summary>
public sealed class FindWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly ArchiveListing _listing;
    private readonly TextBox _query;
    private readonly ListBox _results;
    private readonly TextBlock _status;
    private readonly Button _reveal;

    /// <summary>用户点了「定位」或双击了某一行。调用方负责把它翻成"主列表里选到那一项"。</summary>
    public event Action<ArchiveEntryInfo>? RevealRequested;

    public FindWindow(LangCatalog lang, ArchiveListing listing)
    {
        _lang = lang;
        _listing = listing;

        Title = lang.Label("TEXT_TITLE_FIND");
        Width = 860;
        Height = 560;
        MinWidth = 620;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _query = new TextBox
        {
            Watermark = lang.Label("TEXT_FIND_HINT"),
            MinWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _query.TextChanged += (_, _) => Filter();
        // 回车就是「定位」，手不用离开键盘。光标此时正待在输入框里（单行 TextBox 把 Enter 吃掉
        // 了，派不到窗口那一层），所以接在框上，而不是接在窗口上。
        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RevealPreferred();
                e.Handled = true;
            }
        };

        _status = new TextBlock
        {
            FontSize = Design.Type.Caption,
            Foreground = Design.Ink.Secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _results = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemTemplate = RowTemplate.Of<ArchiveEntryInfo>(entry => Row(lang, entry)),
        };
        _results.SelectionChanged += (_, _) => UpdateRevealState();
        _results.DoubleTapped += (_, _) => RevealSelected();

        // 关掉横向滚动：这里的列宽是写死的（不像主列表可以拖），没有"滚过去看右边那几列"这回事。
        // 留着 Auto 反而两件坏事：行会按无限宽度度量，星号列塌成"最长那条路径"的宽度，
        // 于是行与表头错开；再就是末尾多出一条永远滚不动什么的横向滚动条。
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);

        _reveal = new Button
        {
            Content = lang.Label("TEXT_BTN_REVEAL"),
            MinWidth = 96,
            IsEnabled = false,
        }.Accent();
        _reveal.Click += (_, _) => RevealSelected();

        var close = new Button { Content = lang.Label("TEXT_BTN_CLOSE"), MinWidth = 96 }.Ghost();
        close.Click += (_, _) => Close();

        var body = new DockPanel { Margin = new Thickness(Design.Space.X4) };

        var footer = new DockPanel { Margin = new Thickness(0, Design.Space.X3, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_status);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X2,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(close);
        buttons.Children.Add(_reveal);
        footer.Children.Add(buttons);

        var field = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X2,
            Margin = new Thickness(0, 0, 0, Design.Space.X3),
        };
        field.Children.Add(new TextBlock
        {
            Text = lang.Label("TEXT_FIND_QUERY"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Design.Ink.Primary,
        });
        field.Children.Add(_query);
        DockPanel.SetDock(field, Dock.Top);

        var header = Header(lang);
        DockPanel.SetDock(header, Dock.Top);

        var table = new Border
        {
            CornerRadius = Design.Radius.Md,
            Background = Design.Surface.Base,
            BorderBrush = Design.Line.Strong,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new DockPanel { Children = { header, _results } },
        };

        body.Children.Add(field);
        body.Children.Add(footer);
        body.Children.Add(table);

        this.Chromeless(lang, body, resizable: true);
        Opened += (_, _) => _query.Focus();

        Filter();
    }

    /// <summary>当前这份清单还是不是窗口打开时那一份 —— 关了包、换了包之后这扇窗就该跟着走。</summary>
    public ArchiveListing Listing => _listing;

    private static Control Header(LangCatalog lang) => new Grid
    {
        Height = 32,
        Background = Design.Surface.Muted,
        ColumnDefinitions = Columns(),
        Children =
        {
            HeaderCell(lang.Label("TEXT_FIND_COLUMN_PATH"), 0, HorizontalAlignment.Left),
            HeaderCell(lang.Label("TEXT_COLUMN_COMPRESSED_SIZE"), 1, HorizontalAlignment.Right),
            HeaderCell(lang.Label("TEXT_COLUMN_ORIGINAL_SIZE"), 2, HorizontalAlignment.Right),
            HeaderCell(lang.Label("TEXT_COLUMN_TYPE"), 3, HorizontalAlignment.Left),
            HeaderCell(lang.Label("TEXT_COLUMN_MODIFIED"), 4, HorizontalAlignment.Left),
        },
    };

    /// <summary>
    /// 每次现做一份。<strong>不能共享同一枚 <c>ColumnDefinitions</c></strong>：它是带父节点的
    /// 样式化元素，表头与每一行各是一个 Grid，共用一份会在第二个 Grid 度量时抛
    /// <c>NullReferenceException</c>（ Avalonia 内部 ValidateDefinitionsLayout ）——
    /// 第一行就崩，界面上什么都还没画出来。
    /// </summary>
    private static ColumnDefinitions Columns() => new("*,104,104,120,140");

    private static TextBlock HeaderCell(string text, int column, HorizontalAlignment alignment)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = Design.Type.Caption,
            FontWeight = FontWeight.SemiBold,
            Foreground = Design.Ink.Secondary,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = alignment,
            Margin = new Thickness(Design.Space.X2, 0, Design.Space.X2, 0),
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static Control Row(LangCatalog lang, ArchiveEntryInfo entry) => new Grid
    {
        ColumnDefinitions = Columns(),
        Children =
        {
            Cell(entry.Path, 0, HorizontalAlignment.Left),
            Cell(entry.IsDirectory ? string.Empty : entry.CompressedSize.ToString("N0"), 1, HorizontalAlignment.Right),
            Cell(entry.IsDirectory ? string.Empty : entry.Size.ToString("N0"), 2, HorizontalAlignment.Right),
            Cell(ArchiveView.TypeText(lang, entry), 3, HorizontalAlignment.Left),
            Cell(ArchiveView.ModifiedText(entry), 4, HorizontalAlignment.Left),
        },
    };

    /// <summary>
    /// 一格。<strong><c>column</c> 必须真的写进 <c>Grid.Column</c></strong> —— 只把它当参数传进来
    /// 却忘了赋值，五格会全部堆在第 0 列上：表头读起来还是"路径 压缩后大小 …"那一排字，
    /// 行里却是五行文字叠在一起，100% 缩放下糊成一团、放大缩放后更是直接互相压字。
    /// headless 只看得到"这一格里写着什么"，看不见它落在哪一列，所以这一条只能由
    /// <c>EveryCellLandsInItsOwnColumn</c> 量坐标来钉。
    /// </summary>
    private static TextBlock Cell(string text, int column, HorizontalAlignment alignment)
    {
        var cell = new TextBlock
        {
            Text = text,
            FontSize = Design.Type.Body,
            Foreground = Design.Ink.Primary,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = alignment,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(Design.Space.X2, 0, Design.Space.X2, 0),
        };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private void Filter()
    {
        var query = _query.Text ?? string.Empty;
        var hits = ArchiveSearch.Find(_listing, query);

        _results.ItemsSource = hits;

        _status.Text = query.Trim().Length switch
        {
            0 => _lang.Label("TEXT_FIND_EMPTY"),
            _ when hits.Count == 0 => _lang.Label("TEXT_FIND_NONE"),
            _ => _lang.Format("TEXT_FIND_COUNT", ("n", hits.Count.ToString()), ("m", _listing.Entries.Count.ToString())),
        };

        UpdateRevealState();
    }

    private void UpdateRevealState()
    {
        _reveal.IsEnabled = _results.SelectedItems?.Count > 0;
    }

    private void RevealSelected()
    {
        foreach (var entry in _results.SelectedItems?.OfType<ArchiveEntryInfo>().ToArray() ?? [])
        {
            RevealRequested?.Invoke(entry);
        }
    }

    /// <summary>
    /// 回车那一条路：有选中就跳选中的那一条，一条都没选（刚打完字那种最常见的情形）就跳第一条命中。
    /// <para>没有命中时什么都不做 —— 回车在这扇窗口里唯一的承诺就是"跳过去"，跳不到就别装作跳了。</para>
    /// </summary>
    private void RevealPreferred()
    {
        var target = _results.SelectedItem as ArchiveEntryInfo
            ?? _results.Items?.OfType<ArchiveEntryInfo>().FirstOrDefault();

        if (target is not null)
        {
            RevealRequested?.Invoke(target);
        }
    }
}
