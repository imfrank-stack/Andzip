using System.Globalization;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 「选项 → 密码管理器」：本程序为哪些包存过口令，以及把其中某条（或全部）删掉。
/// <para>这一扇<strong>不能新增也不能改</strong>，只能看和删。新增只有一条路：
/// 打开那个包时输对一次，并且「设置 → 安全」里那一层是开着的。
/// 把"添加"做成一个手填路径 + 口令的表单，等于给用户一个机会去存一条他自己都记不清
/// 属于哪个包的口令；而这一屏上真正有价值的动作是<strong>删</strong>。</para>
/// <para><strong>清单里没有口令，一次都不出现</strong>：不显示、不复制、不做"点一下显出明文"。
/// 这一屏是会被人截图发出来的那种界面，"我的口令管理器长这样"这句话里不该包含所有口令。
/// 要用的时候直接打开那个包，它自己去凭据里取。界面上因此只有路径和时间两列 ——
/// 而路径能显示是因为索引在设置文件里（那里本来就有 <c>recent_files</c>），
/// 凭据的目标名是路径的哈希，见 <see cref="PasswordVaultKey"/>。</para>
/// <para>存储是<strong>注入</strong>进来的（<paramref name="store"/>）而不是这里自己去开：
/// 测试构造这一扇时若拿到这台机器上真实的凭据存储，点一次「全部删除」就会清掉用户存过的口令 ——
/// 那是一次真实破坏，而且是一条<strong>永远绿着</strong>的用例（它删得动，所以断言通过）。
/// 见开发记录第七节第 56 条同一族的事故：默认值跑起来，改的是这台开发机。</para>
/// </summary>
public sealed class PasswordManagerWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly IPersistentPasswordStore? _store;
    private readonly ListBox _entries;
    private readonly TextBlock _summary;
    private readonly Button _delete;
    private readonly Button _deleteAll;

    private List<Control> _rows = [];

    public PasswordManagerWindow(LangCatalog lang, IPersistentPasswordStore? store)
    {
        _lang = lang;
        _store = store;

        Title = lang.Label("TEXT_TITLE_PASSWORD_MANAGER");
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _entries = new ListBox
        {
            MinHeight = 160,
            MaxHeight = 320,
            IsVisible = store is not null,
        };

        _summary = new TextBlock
        {
            FontSize = Design.Type.Caption,
            Foreground = Design.Ink.Secondary,
            TextWrapping = TextWrapping.Wrap,
        };

        _delete = new Button { Content = lang.Label("TEXT_PASSWORD_MANAGER_DELETE_ONE"), MinWidth = 120 }.Ghost();
        _deleteAll = new Button { Content = lang.Label("TEXT_PASSWORD_MANAGER_DELETE_ALL"), MinWidth = 120 }.Danger();
        var close = new Button { Content = lang.Label("TEXT_BTN_CLOSE"), MinWidth = 96 }.Ghost();

        _delete.Click += (_, _) => DeleteSelected();
        _deleteAll.Click += async (_, _) => await DeleteAllAsync();
        close.Click += (_, _) => Close();
        _entries.SelectionChanged += (_, _) => UpdateButtons();

        var body = new StackPanel { Margin = new Thickness(Design.Space.X4), Spacing = Design.Space.X3 };
        body.Children.Add(new TextBlock
        {
            Text = lang.Label(store is null ? "TEXT_PASSWORD_MANAGER_UNSUPPORTED" : "TEXT_PASSWORD_MANAGER_INTRO"),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(_entries);
        body.Children.Add(_summary);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X2,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(close);
        buttons.Children.Add(_deleteAll);
        buttons.Children.Add(_delete);
        body.Children.Add(buttons);

        this.Chromeless(lang, new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        Refresh();
    }

    /// <summary>屏幕上列出的条数（测试用它核对"删掉一条"真的少了一条）。</summary>
    internal int EntryCount => _rows.Count;

    internal bool HasSelection => _entries.SelectedItem is not null;

    internal bool DeleteOneEnabled => _delete.IsEnabled;

    internal bool DeleteAllEnabled => _deleteAll.IsEnabled;

    internal string SummaryText => _summary.Text ?? string.Empty;

    /// <summary>这一屏上列出的路径（按显示顺序）。<strong>只有路径</strong> —— 界面上没有别的东西可拿。</summary>
    internal IReadOnlyList<string> ListedPaths => [.. _rows.Select(row => (string)row.Tag!)];

    private void Refresh()
    {
        _rows = _store?.List().Select(BuildRow).ToList() ?? [];
        _entries.ItemsSource = _rows;

        var count = _rows.Count;
        _summary.Text = count == 0
            ? _lang.Label("TEXT_PASSWORD_MANAGER_EMPTY")
            : _lang.Format("TEXT_PASSWORD_MANAGER_COUNT", ("count", count.ToString("N0", CultureInfo.CurrentCulture)));
        UpdateButtons();
    }

    /// <summary>
    /// 一行两格：路径 + 什么时候存的。<see cref="Control.Tag"/> 上挂路径给删除用。
    /// <para>直接把 <c>Control</c> 当列表项而不是数据对象：Avalonia 对非 Control 的项会退化成
    /// <c>ToString()</c>，那样两列就变成一行字符串，路径长的会被挤在一起看不出边界。</para>
    /// </summary>
    private Control BuildRow(PasswordVaultEntry entry)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X3,
            Margin = new Thickness(Design.Space.X1),
            Tag = entry.ArchivePath,
        };
        var path = new TextBlock
        {
            Text = entry.ArchivePath,
            Width = 340,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(path, entry.ArchivePath);
        row.Children.Add(path);
        row.Children.Add(new TextBlock
        {
            Text = FormatWhen(entry.SavedAt),
            Foreground = Design.Ink.Secondary,
            FontSize = Design.Type.Caption,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    private string FormatWhen(DateTimeOffset savedAt)
        => _lang.Format("TEXT_PASSWORD_MANAGER_SAVED",
            ("when", savedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)));

    private void UpdateButtons()
    {
        var any = EntryCount > 0;
        _deleteAll.IsEnabled = any;
        _delete.IsEnabled = any && HasSelection;
    }

    private void DeleteSelected()
    {
        if (_store is null || _entries.SelectedItem is not Control row)
        {
            return;
        }

        _store.Remove((string)row.Tag!);
        Refresh();
    }

    /// <summary>
    /// 全部删除之前先问一句，并把条数写进那句问话里 —— 这一按删掉的是
    /// "以后打开这些包都得重新输口令"，而用户可能以为自己在关某个开关。
    /// </summary>
    private async Task DeleteAllAsync()
    {
        if (_store is null || EntryCount == 0)
        {
            return;
        }

        var ask = _lang.Format("TEXT_PASSWORD_MANAGER_ASK_DELETE_ALL",
            ("count", EntryCount.ToString("N0", CultureInfo.CurrentCulture)));
        if (!await MessageWindow.ConfirmAsync(this, _lang, _lang.Label("TEXT_TITLE_PASSWORD_MANAGER"), ask,
                "TEXT_PASSWORD_MANAGER_DELETE_ALL"))
        {
            return;
        }

        _store.Clear();
        Refresh();
    }
}
