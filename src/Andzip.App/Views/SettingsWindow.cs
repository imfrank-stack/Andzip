using System.Diagnostics;
using Andzip.App.Services;
using Andzip.Core.Archive;
using Andzip.Core.Iconography;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Andzip.App.Views;

/// <summary>
/// 设置窗口（选项 → 设置，F5）：左侧导航 + 右侧页面 + 底部「重置 / 确定」。
/// <para>
/// 九个页面全部实装（常规 / 文件关联 / 上下文菜单 / 压缩 / 解压 / 查看 / 安全 / 高级 / 语言），
/// 每页只放真正接得上线的开关 —— 参考软件上有、这里做不到的项一律不摆，
/// 空着的导航项和假控件都比"做不到的功能"更糟。
/// </para>
/// <para>
/// 页与页之间<strong>不共享写入时机</strong>：一半是点「立即应用」就落盘，另一半等「确定」。
/// 所以任何一处写盘都要走 <see cref="Persist" />，否则后写的那一份会盖掉先写的那一份
/// （第七节第 29 条记的就是这个）。
/// </para>
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>设置里保存「已勾选哪些关联」的键名。</summary>
    public const string AssociationSettingKey = "file_associations";

    private static IBrush PageText => Design.Ink.Primary;
    private static IBrush LinkBlue => Design.Brand.Base;
    private static IBrush HintText => Design.Ink.Secondary;

    private readonly ContentControl _pageHost = new();
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.Ordinal);
    private readonly ISettingsPage[] _pages;
    private ISettingsPage? _currentPage;

    /// <param name="startPage">开在哪一页（导航键）。工具栏那颗「切换语言」要落在语言页上 ——
    /// 它做的事是"把人带到真正能换语言的地方"，而不是自己假装能换。</param>
    public SettingsWindow(LangCatalog lang, AppSettings settings, string settingsPath, string? startPage = null)
    {
        Title = lang.Label("TEXT_TITLE_SETTINGS");
        Width = 1000;
        Height = 660;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.WindowSurface();

        // 导航顺序与参考软件一致。
        var navKeys = new[]
        {
            "TEXT_SETTINGS_GENERAL",
            "TEXT_SETTINGS_FILE_ASSOCIATION",
            "TEXT_SETTINGS_CONTEXT_MENU",
            "TEXT_SETTINGS_EXTRACT",
            "TEXT_SETTINGS_COMPRESS",
            "TEXT_SETTINGS_VIEW",
            "TEXT_SETTINGS_SECURITY",
            "TEXT_SETTINGS_ADVANCED",
            "TEXT_SETTINGS_LANGUAGE",
        };

        _pages =
        [
            new GeneralPage(this, lang, settings, settingsPath),
            new FileAssociationPage(this, lang, settings, settingsPath),
            new ContextMenuPage(this, lang, settings, settingsPath),
            new ExtractionPage(lang, settingsPath),
            new CompressionPage(this, lang, settings, settingsPath),
            new ViewPage(lang, settings, settingsPath),
            new SecurityPage(this, lang, settings, settingsPath),
            new AdvancedPage(this, lang, settings, settingsPath),
            new LanguagePage(lang, settings, settingsPath),
        ];

        // Spacing 必须是 0：条目之间那几像素是点不动的死区（1.8.7 用户第二条反馈）。
        // 行的间隔改由药丸自己的上下内距给，整行都归这一项命中。
        var nav = new StackPanel
        {
            Margin = new Thickness(Design.Space.X2, Design.Space.X3, Design.Space.X2, Design.Space.X3),
        };
        for (var i = 0; i < navKeys.Length; i++)
        {
            var index = i;
            var button = NavButton(lang.Label(navKeys[i]), () => ShowPage(index));
            _navButtons[navKeys[i]] = button;
            nav.Children.Add(button);
        }

        // 导航条是"位置"不是"命令"：整条实心蓝（1.3.x）→ 浅灰底 + 右分隔线（1.4.0）→
        // 现在直接坐在画布上，选中项是一颗<strong>浮起来的白色药丸</strong>。
        // 表示选中不再需要颜色或线条，阴影就够了 —— 这是有了阴影之后才做得出的选择。
        var navPane = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Design.Line.Subtle,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = nav,
            Width = 200,
        };
        var contentPane = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(Design.Space.X5, Design.Space.X3, Design.Space.X5, Design.Space.X3),
            Child = _pageHost,
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(navPane, 0);
        Grid.SetColumn(contentPane, 1);
        body.Children.Add(navPane);
        body.Children.Add(contentPane);

        var resetButton = new Button { Content = lang.Label("TEXT_BTN_RESET"), MinWidth = 120 }.Ghost();
        resetButton.Click += (_, _) => _currentPage?.Reload();
        var okButton = new Button { Content = lang.Label("TEXT_BTN_OK"), MinWidth = 120, IsDefault = true }.Accent();
        okButton.Click += (_, _) => AcceptAndClose();

        // 按钮贴右：这一窗口的动作在右下角，靠左会让"确定"离它要确定的那卡片最远。
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = Design.Space.X3,
            Margin = new Thickness(Design.Space.X4, Design.Space.X2, Design.Space.X4, Design.Space.X3),
            Children = { resetButton, okButton },
        };

        var root = new DockPanel();
        var footerBar = new Border
        {
            Background = Brushes.Transparent,
            Child = footer,
        };
        DockPanel.SetDock(footerBar, Dock.Bottom);
        root.Children.Add(footerBar);
        root.Children.Add(body);

        this.Chromeless(lang, root, resizable: true);
        ShowPage(startPage is { } key ? System.Array.IndexOf(navKeys, key) : 0);
    }

    private void ShowPage(int index)
    {
        var page = _pages[index];
        _currentPage = page;

        foreach (var (key, button) in _navButtons)
        {
            PaintNav(button, key == page.NavKey);
        }

        page.Reload();
        _pageHost.Content = page.Root;
    }

    /// <summary>
    /// 左侧导航项。两件事都反直觉，都有用户截图为证（1.8.7）：
    /// <para><strong>药丸画在 Content 里，不交给按钮模板。</strong>
    /// SimpleTheme 的 Button 在 <c>:pressed</c> 上自己涂一整块 <c>#686868</c>，
    /// 应用层给 <c>Background</c> 写的局部值压不过它 —— 于是"按下"这一下在浅底上是一块脏灰，
    /// 文字几乎看不见。与开关轨道同一族的问题，同一个解法（规范 §二）：
    /// 视觉全画在 Content 那颗自绘 <c>Border</c> 上，控件本体只管行为（点击、键盘、焦点）。
    /// 也正因为如此，药丸必须<strong>铺满整行</strong>：留一圈 margin 的话，
    /// 模板那层深灰会从缝里透出来，等于没修。</para>
    /// <para><strong>行与行之间不留空隙。</strong>以前条目之间留了 4px 的
    /// <c>StackPanel.Spacing</c>，那是一条点不动的死区 —— 用户的手指在列表上移动时一定会划到它，
    /// 划到就没反应。现在条目紧贴，呼吸感做进药丸自己的上下内距里，
    /// 整行都是这一项的命中区。</para>
    /// </summary>
    private Button NavButton(string text, Action onClick)
    {
        var pill = new Border
        {
            Padding = new Thickness(Design.Space.X3, Design.Space.X2 + 6, Design.Space.X2, Design.Space.X2 + 6),
            CornerRadius = Design.Radius.Md,
            Child = new TextBlock
            {
                Text = text,
                FontSize = Design.Type.Body,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var button = new Button
        {
            Content = pill,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            CornerRadius = Design.Radius.None,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        button.Click += (_, _) => onClick();

        // 状态一律现读现画（IsPointerOver / IsPressed / Tag 里的选中），
        // 不在事件里传"我是悬停还是按下"——那种写法一旦漏一个退出事件就会卡在深色那一档。
        button.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsPointerOverProperty
                || e.Property == Button.IsPressedProperty)
            {
                PaintNav(button, Equals(button.Tag, true));
            }
        };
        return button.PinFace();   // 主题那份 :pressed 的深灰也不能从药丸底下透出来
    }

    /// <summary>
    /// 导航项的三档：选中 = 白色药丸 + 亮边 + 半粗；按下 = <c>Surface.Pressed</c>；
    /// 悬停 = <c>Surface.Muted</c>；其余透明。按下压在悬停之上，选中压在按下之下 ——
    /// 按下去时选中项也要给出"这里被按住了"的反馈，但松手后它还得是那颗白色药丸。
    /// </summary>
    private static void PaintNav(Button button, bool selected)
    {
        button.Tag = selected;
        if (button.Content is not Border pill || pill.Child is not TextBlock label)
        {
            return;
        }

        pill.Background = button.IsPressed
            ? Design.Surface.Pressed
            : selected ? Design.Surface.Base : button.IsPointerOver ? Design.Surface.Muted : Brushes.Transparent;
        pill.BorderBrush = selected ? Design.Line.Subtle : Brushes.Transparent;
        pill.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
        label.Foreground = selected ? Design.Ink.Primary : Design.Ink.Secondary;
        label.FontWeight = selected ? Design.Type.Semibold : FontWeight.Normal;
    }

    private void AcceptAndClose()
    {
        // 「确定」= 让当前页把没应用的改动落盘再关窗；右上角的 X 才是放弃改动。
        _currentPage?.Commit();
        Close();
    }

    /// <summary>
    /// 只改自己那几项的写法：从磁盘重读一份、改完写回去，再把主窗口手里那份快照同步上新值。
    /// <para>
    /// 两步都必要。<strong>重读</strong>：设置窗口里各页的写入时机不同（有的点「立即应用」就落盘，
    /// 有的等到「确定」），拿窗口打开时那份快照整文件写回，会把别处刚落的盘抹掉 —— 这正是
    /// 0.13.0「设置不生效」的成因。<strong>同步</strong>：主窗口那份快照还在被人用来写别的键
    /// （最近文件、上次输出目录），不同步的话它下次写盘一样是旧文件覆盖新文件。
    /// </para>
    /// </summary>
    private static void Persist(AppSettings shared, string settingsPath, Action<AppSettings> mutate)
    {
        var fresh = AppSettings.Load(settingsPath);
        mutate(fresh);
        fresh.Save(settingsPath);
        shared.Reload(settingsPath);
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = Design.Type.Title,
        FontWeight = Design.Type.Semibold,
        Foreground = PageText,
    };

    /// <summary>控件下方那行灰色小字说明。</summary>
    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = Design.Type.Caption,
        Foreground = HintText,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(Design.Space.X4, Design.Space.X1, 0, 0),
    };

    /// <summary>带"改动就重新算要不要允许应用"回调的复选框。好几个页面都要这个形状。</summary>
    /// <summary>
    /// 开关行：左边说明（下面一行灰字补充），右边一个开关。带"改动就重算要不要允许应用"的回调。
    /// <para><strong>为什么设置页的布尔项换成开关，而文件关联页仍然用勾选框</strong>：
    /// 这一页的每一项是"程序的某个行为开不开"，开关的位置语义（左/右）比一个勾更贴合；
    /// 文件关联那一页是"这一批里选哪几个"的多选清单，换成几十个开关只会更慢也更难看。
    /// 两种控件各管各的语义，不是改版改到哪儿就推到哪儿。</para>
    /// </summary>
    private static SettingToggle Check(string label, Action onToggled, string? hint = null)
        => new(label, hint, onToggled);

    /// <summary>左标签右控件的一行；标签给空串就是只占缩进的单独控件行。</summary>
    private static Control LabeledRow(string label, Control control, Control? trailing = null)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 14, 0, 0),
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Width = 170,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(control);
        if (trailing is not null)
        {
            row.Children.Add(trailing);
        }

        return row;
    }

    /// <summary>每页对外的三件事：导航键、要呈现的控件、重新读状态 / 落盘。</summary>
    private interface ISettingsPage
    {
        string NavKey { get; }

        Control Root { get; }

        /// <summary>从真实来源（设置文件或注册表）重读一遍，放弃界面里没应用的改动。</summary>
        void Reload();

        void Commit();
    }

    /// <summary>还没实现的页面：显示占位文案，而不是摆一堆不生效的控件。</summary>
    private sealed class PlaceholderPage : ISettingsPage
    {
        public PlaceholderPage(LangCatalog lang, string navKey)
        {
            NavKey = navKey;

            var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20), Spacing = 14 };
            panel.Children.Add(SectionTitle(lang.Label(navKey)));
            panel.Children.Add(new TextBlock
            {
                Text = lang.Label("TEXT_SETTINGS_NOT_IMPLEMENTED"),
                Foreground = HintText,
                TextWrapping = TextWrapping.Wrap,
            });
            Root = panel;
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
        }

        public void Commit()
        {
        }
    }

    /// <summary>
    /// 常规页：进度窗口能否按 Esc 取消。
    /// <para>
    /// 这里原来还有「双击压缩包时」那一项，1.6.0 挪到解压页了 —— 它和落点规则是一件事，
    /// 而且两页各摆一份会各自写同一个键，一边改完另一边不知道。
    /// </para>
    /// </summary>
    private sealed class GeneralPage : ISettingsPage
    {
        private readonly AppSettings _settings;
        private readonly string _settingsPath;
        private readonly SettingToggle _escCancel;

        public GeneralPage(SettingsWindow owner, LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_GENERAL";
            _settings = settings;
            _settingsPath = settingsPath;

            _escCancel = Check(lang.Label("TEXT_SETTINGS_ESC_CANCEL"), () => { },
                lang.Label("TEXT_SETTINGS_ESC_CANCEL_HINT"));

            var panel = new StackPanel();
            panel.Children.Add(SectionTitle(lang.Label("TEXT_SETTINGS_GENERAL_TITLE")));
            panel.Children.Add(_escCancel);
            Root = panel;
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            _escCancel.IsChecked = AppSettings.Load(_settingsPath)
                .GetBool(SettingKeys.AllowEscCancelProgress, fallback: true);
        }

        public void Commit()
        {
            var allowEsc = _escCancel.IsChecked == true;
            Persist(_settings, _settingsPath, fresh => fresh.SetBool(SettingKeys.AllowEscCancelProgress, allowEsc));
        }
    }

    /// <summary>
    /// 文件关联页：分组复选框 + 全选 / 取消全选 + 立即应用。
    /// <para>
    /// 候选项来自 <see cref="FileAssociationCatalog.Supported"/>，也就是引擎实际报告的扩展名集合 ——
    /// 勾不上我们读不了的格式。勾选状态<strong>读自注册表</strong>而不是设置文件：后者记的是
    /// 「上次写成功的那批」，而注册表会被别的软件改、被清理软件清，照它回显就会显示一堆没生效的勾。
    /// </para>
    /// </summary>
    private sealed class FileAssociationPage : ISettingsPage
    {
        private readonly SettingsWindow _owner;
        private readonly LangCatalog _lang;
        private readonly AppSettings _settings;
        private readonly string _settingsPath;
        private readonly IReadOnlyList<AssociationGroup> _groups;
        private readonly Dictionary<string, CheckBox> _boxes = new(StringComparer.Ordinal);
        private readonly Button _applyButton;

        /// <summary>
        /// 「已关联但默认程序还不是我们」的那串扩展名，以及对应的提示行和「设为默认…」按钮。
        /// <para>这一栏存在的理由就是用户报的那个 bug：<c>.7z</c> 勾上了、ProgID 也写对了，
        /// 双击却还是 Bandizip —— 因为 <c>UserChoice</c> 排在 <c>Software\Classes</c> 前面。
        /// 光显示勾很容易让人以为一切正常，所以必须把"被挡住"这件事摆在脸上并给一条出路。</para>
        /// </summary>
        private readonly TextBlock _shadowHint;
        private readonly Button _setDefaultButton;
        private List<string> _shadowed = [];

        /// <summary>接管前的默认程序（扩展名 → ProgID，空串表示当时没有）。撤销时按它还原。</summary>
        private readonly Dictionary<string, string> _originals;

        private HashSet<string> _live = new(StringComparer.Ordinal);

        public FileAssociationPage(SettingsWindow owner, LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_FILE_ASSOCIATION";
            _owner = owner;
            _lang = lang;
            _settings = settings;
            _settingsPath = settingsPath;
            _groups = FileAssociationCatalog.Supported(ArchiveFormatOption.OpenableExtensions);
            _originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var original in AssociationOriginals.Decode(settings.GetString(AssociationOriginals.SettingKey)))
            {
                _originals[original.Extension] = original.ProgId;
            }

            var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var title = SectionTitle(lang.Label("TEXT_SETTINGS_FILE_ASSOCIATION"));
            Grid.SetColumn(title, 0);
            titleRow.Children.Add(title);

            var defaultAppsLink = new Button
            {
                Content = lang.Label("TEXT_SETTINGS_WINDOWS_DEFAULT_APPS"),
                Foreground = LinkBlue,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(6, 0, 0, 0),
            }.PinFace();
            defaultAppsLink.Click += (_, _) => OpenWindowsDefaultAppSettings(null);
            Grid.SetColumn(defaultAppsLink, 1);
            titleRow.Children.Add(defaultAppsLink);

            var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
            list.Children.Add(new TextBlock
            {
                Text = lang.Label("TEXT_SETTINGS_ASSOC_HINT"),
                Foreground = HintText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            _shadowHint = new TextBlock
            {
                Foreground = Design.Semantic.Danger,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false,
            };
            _setDefaultButton = new Button
            {
                Content = lang.Label("TEXT_BTN_SET_DEFAULT"),
                MinWidth = 120,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false,
            }.Ghost();
            _setDefaultButton.Click += (_, _) => OpenWindowsDefaultAppSettings(_shadowed.FirstOrDefault());

            var shadowRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            Grid.SetColumn(_shadowHint, 0);
            Grid.SetColumn(_setDefaultButton, 1);
            shadowRow.Children.Add(_shadowHint);
            shadowRow.Children.Add(_setDefaultButton);
            list.Children.Add(shadowRow);

            foreach (var group in _groups)
            {
                list.Children.Add(GroupHeader(lang.Label(group.TextKey)));
                var items = new WrapPanel { Margin = new Thickness(4, 2, 0, 6) };
                foreach (var association in group.Items)
                {
                    var box = new CheckBox
                    {
                        Content = association.Label,
                        Width = 120,
                        Margin = new Thickness(0, 2, 0, 2),
                    };
                    ToolTip.SetTip(box, string.Join(' ', association.Extensions.Select(x => "." + x)));
                    box.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == ToggleButton.IsCheckedProperty)
                        {
                            UpdateApplyState();
                        }
                    };
                    _boxes[association.Id] = box;
                    items.Children.Add(box);
                }

                list.Children.Add(items);
            }

            var selectAll = new Button { Content = lang.Label("TEXT_BTN_SELECT_ALL"), MinWidth = 150, Margin = new Thickness(0, 0, 0, 8) }.Ghost();
            selectAll.Click += (_, _) => SetAll(true);
            var deselectAll = new Button { Content = lang.Label("TEXT_BTN_DESELECT_ALL"), MinWidth = 150, Margin = new Thickness(0, 0, 0, 8) }.Ghost();
            deselectAll.Click += (_, _) => SetAll(false);

            _applyButton = new Button
            {
                Content = lang.Label("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Bottom,
            }.Ghost();
            _applyButton.Click += (_, _) => Apply();

            var side = new DockPanel { Margin = new Thickness(14, 0, 0, 0) };
            var sideTop = new StackPanel { Children = { selectAll, deselectAll } };
            DockPanel.SetDock(sideTop, Dock.Top);
            DockPanel.SetDock(_applyButton, Dock.Bottom);
            side.Children.Add(sideTop);
            side.Children.Add(_applyButton);

            var columns = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(22, 18, 22, 18),
                RowDefinitions = new RowDefinitions("Auto,*"),
            };
            Grid.SetColumn(titleRow, 0);
            Grid.SetColumnSpan(titleRow, 2);
            Grid.SetRow(titleRow, 0);
            columns.Children.Add(titleRow);

            var scroller = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            Grid.SetRow(scroller, 1);
            columns.Children.Add(scroller);

            Grid.SetRow(side, 1);
            Grid.SetColumn(side, 1);
            columns.Children.Add(side);

            Root = columns;
        }

        public string NavKey { get; }

        public Control Root { get; }

        /// <summary>重新读一遍注册表 —— 这就是「重置」的语义：放弃没应用的改动，回到系统现状。</summary>
        public void Reload()
        {
            _live = ReadLiveSelection(_settings, _groups);
            foreach (var (id, box) in _boxes)
            {
                box.IsChecked = _live.Contains(id);
            }

            ShowShadowedDefaults(ReadShadowedDefaults(_live));
            UpdateApplyState();
        }

        /// <summary>
        /// 把「勾上了但双击还不是 Andzip」这件事显示出来。
        /// 一个红色的说明加一个「设为默认…」按钮 —— 只弹提示不给出路是用户明确反对过的做法。
        /// </summary>
        /// <param name="shadowed">不带点的扩展名，与 <see cref="OpenWindowsDefaultAppSettings"/> 的入参一致。</param>
        private void ShowShadowedDefaults(IReadOnlyList<string> shadowed)
        {
            _shadowed = shadowed.ToList();
            if (_shadowed.Count == 0)
            {
                _shadowHint.IsVisible = false;
                _setDefaultButton.IsVisible = false;
                return;
            }

            _shadowHint.Text = _lang.Format(
                "TEXT_SETTINGS_DEFAULT_STILL",
                ("list", string.Join("、", _shadowed.Select(x => "." + x))));
            _shadowHint.IsVisible = true;
            _setDefaultButton.IsVisible = true;
        }

        private IReadOnlyList<string> ReadShadowedDefaults(HashSet<string> registered)
        {
            if (!OperatingSystem.IsWindows())
            {
                return [];
            }

            // 写成循环而不是 LINQ：平台兼容性分析器不会把 IsWindows() 的判断带进 lambda 里。
            var service = new FileAssociationService();
            var shadowed = new List<string>();
            foreach (var id in registered)
            {
                shadowed.AddRange(service.ShadowedDefaults(FileAssociationCatalog.Find(id)!));
            }

            shadowed.Sort(StringComparer.Ordinal);
            return shadowed;
        }

        public void Commit()
        {
            if (_applyButton.IsEnabled)
            {
                Apply();
            }
        }

        private void SetAll(bool value)
        {
            foreach (var box in _boxes.Values)
            {
                box.IsChecked = value;
            }
        }

        private HashSet<string> CurrentSelection()
            => _boxes.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

        private void UpdateApplyState() => _applyButton.IsEnabled = !CurrentSelection().SetEquals(_live);

        private void Apply()
        {
            var desired = CurrentSelection();
            if (desired.SetEquals(_live))
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                Message(_lang.Label("TEXT_TITLE_SETTINGS"), _lang.Label("TEXT_SETTINGS_ASSOC_UNSUPPORTED"));
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                Message(_lang.Label("TEXT_TITLE_ERROR"), _lang.Label("TEXT_SETTINGS_ASSOC_APPLY_FAILED") + "process path unavailable");
                return;
            }

            var blocked = new List<string>();
            try
            {
                var service = new FileAssociationService();

                // 新登记的格式要跟「设置 → 上下文菜单」里的解压开关保持一致，否则菜单会"一半有一半没有"。
                var labels = ShellVerbLabels.Extraction(
                    _lang,
                    _settings.GetBool(SettingKeys.ContextMenuExtractVerbs, fallback: true));

                foreach (var id in desired.Except(_live))
                {
                    var change = service.Register(FileAssociationCatalog.Find(id)!, executable, labels);
                    blocked.AddRange(change.BlockedExtensions);
                    foreach (var original in change.CapturedDefaults)
                    {
                        // 已经记过的不覆盖：重复登记时服务对“本来就是我们”的扩展名返回空串，
                        // 拿它盖掉第一次记下的真实原值就把撤销路径写坏了。
                        if (!_originals.ContainsKey(original.Extension))
                        {
                            _originals[original.Extension] = original.ProgId;
                        }
                    }
                }

                var restore = _originals.Select(pair => new AssociationOriginal(pair.Key, pair.Value)).ToList();
                foreach (var id in _live.Except(desired))
                {
                    var association = FileAssociationCatalog.Find(id)!;
                    service.Unregister(association, restore);
                    foreach (var extension in association.Extensions)
                    {
                        _originals.Remove(extension);
                    }
                }

                service.NotifyChanged();

                var originals = AssociationOriginals.Encode(
                    _originals.Select(pair => new AssociationOriginal(pair.Key, pair.Value)));
                Persist(_settings, _settingsPath, fresh =>
                {
                    fresh.SetStringList(AssociationSettingKey, desired);
                    fresh.SetString(AssociationOriginals.SettingKey, originals);
                });
            }
            catch (Exception ex)
            {
                Message(_lang.Label("TEXT_TITLE_ERROR"), _lang.Label("TEXT_SETTINGS_ASSOC_APPLY_FAILED") + ex.Message);
                return;
            }

            Reload();

            if (blocked.Count > 0)
            {
                // 改不动就别只弹一句“失败了”：直接把系统设置里对应扩展名的默认应用页开起来。
                OpenWindowsDefaultAppSettings(blocked[0]);
            }
        }

        private void Message(string title, string text) => _ = new MessageWindow(_lang, title, text).ShowDialog(_owner);

        private static HashSet<string> ReadLiveSelection(AppSettings settings, IReadOnlyList<AssociationGroup> groups)
        {
            var offered = groups.SelectMany(g => g.Items).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

            if (!OperatingSystem.IsWindows())
            {
                return settings.GetStringList(AssociationSettingKey).Where(offered.Contains).ToHashSet(StringComparer.Ordinal);
            }

            // 写成循环而不是 LINQ：平台兼容性分析器不会把 IsWindows() 的判断带进 lambda 里。
            var service = new FileAssociationService();
            var registered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in offered)
            {
                if (service.IsRegistered(FileAssociationCatalog.Find(id)!))
                {
                    registered.Add(id);
                }
            }

            return registered;
        }

        private static Control GroupHeader(string text) => new Border
        {
            Margin = new Thickness(0, 8, 0, 2),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Design.Brand.Line,
            Child = new TextBlock
            {
                Text = text,
                Foreground = LinkBlue,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            },
        };

        /// <param name="extension">要直接跳到的扩展名；null 表示打开默认应用总览页。</param>
        private static void OpenWindowsDefaultAppSettings(string? extension)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            // Windows 11 认 fileExtension 参数，Windows 10 不认 —— 打不开就退回总览页。
            var targeted = extension is null
                ? "ms-settings:defaultapps"
                : "ms-settings:defaultapps?fileExtension=." + extension;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = targeted, UseShellExecute = true });
            }
            catch (Exception)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = "ms-settings:defaultapps", UseShellExecute = true });
                }
                catch (Exception)
                {
                    // 没有 shell 设置页可跳（精简系统 / 无桌面会话）时静默失败即可。
                }
            }
        }
    }

    /// <summary>
    /// 上下文菜单页：右键「压缩为」（文件 / 文件夹两个范围）与右键「解压」三个开关。
    /// <para>
    /// 两个「压缩」开关写在全局动词键（<c>*</c> 与 <c>Directory</c>）下，与文件关联无关，
    /// 状态读自注册表 —— 这里要显示的是资源管理器现在真的有哪几项，不是上一次写成功了什么。
    /// </para>
    /// <para>
    /// 「解压」开关不一样：它住在各个 <c>Andzip.*</c> ProgID 里，而 ProgID 会随新关联不断增多，
    /// 所以它记的是<strong>策略</strong>（<see cref="SettingKeys.ContextMenuExtractVerbs"/>），
    /// 每次登记关联与启动自愈时都按它补写一遍。
    /// </para>
    /// </summary>
    private sealed class ContextMenuPage : ISettingsPage
    {
        private readonly SettingsWindow _owner;
        private readonly LangCatalog _lang;
        private readonly AppSettings _settings;
        private readonly string _settingsPath;
        private readonly SettingToggle _forFiles;
        private readonly SettingToggle _forFolders;
        private readonly SettingToggle _forExtract;
        private readonly Button _applyButton;

        private (bool Files, bool Folders, bool Extract) _live;

        public ContextMenuPage(SettingsWindow owner, LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_CONTEXT_MENU";
            _owner = owner;
            _lang = lang;
            _settings = settings;
            _settingsPath = settingsPath;

            _forFiles = Check(lang.Label("TEXT_SETTINGS_CTX_COMPRESS_FILES"), UpdateApplyState);
            _forFolders = Check(lang.Label("TEXT_SETTINGS_CTX_COMPRESS_FOLDERS"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_CTX_COMPRESS_HINT"));
            _forExtract = Check(lang.Label("TEXT_SETTINGS_CTX_EXTRACT"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_CTX_EXTRACT_RULES") + "\n" + lang.Label("TEXT_SETTINGS_CTX_EXTRACT_HINT"));

            _applyButton = new Button
            {
                Content = lang.Label("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, Design.Space.X2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Ghost();
            _applyButton.Click += (_, _) => Apply();

            var panel = new StackPanel();
            panel.Children.Add(SectionTitle(lang.Label("TEXT_SETTINGS_CONTEXT_MENU_TITLE")));
            panel.Children.Add(new TextBlock
            {
                Text = lang.Label("TEXT_SETTINGS_CTX_NOTE"),
                Foreground = HintText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, Design.Space.X1, 0, Design.Space.X2),
            });
            panel.Children.Add(_forFiles);
            panel.Children.Add(_forFolders);
            panel.Children.Add(_forExtract);
            panel.Children.Add(_applyButton);

            Root = panel;
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            _live = ReadLive();
            (_forFiles.IsChecked, _forFolders.IsChecked, _forExtract.IsChecked) = (_live.Files, _live.Folders, _live.Extract);
            UpdateApplyState();
        }

        public void Commit()
        {
            if (_applyButton.IsEnabled)
            {
                Apply();
            }
        }

        private (bool Files, bool Folders, bool Extract) Selection()
            => (_forFiles.IsChecked == true, _forFolders.IsChecked == true, _forExtract.IsChecked == true);

        private void UpdateApplyState() => _applyButton.IsEnabled = !Selection().Equals(_live);

        private (bool Files, bool Folders, bool Extract) ReadLive()
        {
            if (!OperatingSystem.IsWindows())
            {
                return (false, false, false);
            }

            // 写成循环而不是 LINQ：平台兼容性分析器不会把外层的 IsWindows() 判断带进 lambda 里。
            var contextMenu = new ContextMenuService();
            return (
                contextMenu.IsInstalled(ContextMenuService.FilesScope),
                contextMenu.IsInstalled(ContextMenuService.FoldersScope),
                _settings.GetBool(SettingKeys.ContextMenuExtractVerbs, fallback: true));
        }

        private void Apply()
        {
            var desired = Selection();
            if (desired.Equals(_live))
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                Message(_lang.Label("TEXT_SETTINGS_CTX_UNSUPPORTED"));
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                Message(_lang.Label("TEXT_SETTINGS_ASSOC_APPLY_FAILED") + "process path unavailable");
                return;
            }

            try
            {
                var contextMenu = new ContextMenuService();
                if (desired.Files || desired.Folders)
                {
                    contextMenu.Install(ShellVerbLabels.Compression(_lang), executable, desired.Files, desired.Folders);
                }
                else
                {
                    contextMenu.Remove();
                }

                contextMenu.NotifyChanged();

                if (desired.Extract != _live.Extract)
                {
                    Persist(_settings, _settingsPath,
                        fresh => fresh.SetBool(SettingKeys.ContextMenuExtractVerbs, desired.Extract));

                    // 解压动词住在每个 Andzip.* ProgID 里，所以"改开关"就是把自己命名空间下的
                    // 动词全部重写一遍 —— 关掉的那几项是真删掉，不是留着键改文字。
                    var associations = new FileAssociationService();
                    associations.RepairCommands(executable, ShellVerbLabels.Extraction(_lang, desired.Extract));
                    associations.NotifyChanged();
                }
            }
            catch (Exception ex)
            {
                Message(_lang.Label("TEXT_SETTINGS_CTX_FAILED") + ex.Message);
                return;
            }

            Reload();
        }

        private void Message(string text) => _ = new MessageWindow(_lang, _lang.Label("TEXT_TITLE_SETTINGS"), text).ShowDialog(_owner);
    }

    /// <summary>
    /// 解压页：双击怎么办 + 三个开关。
    /// <para>
    /// 「双击压缩包」这一项原本住在常规页，1.6.0 搬到这里：它选出的「直接解压」用的正是本页
    /// 第一条落点规则，拆在两处会让用户在一个页面改完、在另一个页面找不到自己刚改的东西。
    /// </para>
    /// <para>
    /// 没有放参考软件上的「一次性解压 TGZ/TBZ」「自动检测 Macintosh 代码页」「并行解压」等项 ——
    /// 前者引擎本来就会把嵌套归档展平（不是可关的行为），中者需要引擎侧的代码页猜测，
    /// 后者与压缩共用一个进程级线程数、放在这里会以为有两个旋钮。做不到的不摆假控件。
    /// </para>
    /// </summary>
    private sealed class ExtractionPage : ISettingsPage
    {
        private readonly string _settingsPath;
        private readonly ComboBox _doubleClick;
        private readonly SettingToggle _singleFileToFolder;
        private readonly SettingToggle _openFolderAfter;
        private readonly SettingToggle _keepProgress;
        private readonly Button _applyButton;

        private ExtractionDefaults _live = ExtractionDefaults.Fallback;

        public ExtractionPage(LangCatalog lang, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_EXTRACT";
            _settingsPath = settingsPath;

            _doubleClick = new ComboBox
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = new[] { lang.Label("TEXT_SETTINGS_DOUBLE_CLICK_OPEN"), lang.Label("TEXT_SETTINGS_DOUBLE_CLICK_EXTRACT") },
            }.Picker();
            _doubleClick.SelectionChanged += (_, _) => UpdateApplyState();

            _singleFileToFolder = Check(lang.Label("TEXT_SETTINGS_EXTRACT_SINGLE_FILE_TO_FOLDER"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_EXTRACT_SINGLE_FILE_HINT"));
            _openFolderAfter = Check(lang.Label("TEXT_SETTINGS_EXTRACT_OPEN_FOLDER"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_EXTRACT_OPEN_FOLDER_HINT"));
            _keepProgress = Check(lang.Label("TEXT_SETTINGS_EXTRACT_KEEP_PROGRESS"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_EXTRACT_KEEP_PROGRESS_HINT"));

            _applyButton = new Button
            {
                Content = lang.Label("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, Design.Space.X2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Ghost();
            _applyButton.Click += (_, _) => Apply();

            var panel = new StackPanel();
            panel.Children.Add(SectionTitle(lang.Label("TEXT_SETTINGS_EXTRACT_TITLE")));
            panel.Children.Add(SettingRow.Labeled(lang.Label("TEXT_SETTINGS_DOUBLE_CLICK_LABEL"), _doubleClick,
                lang, "TEXT_SETTINGS_DOUBLE_CLICK_HINT"));
            panel.Children.Add(_singleFileToFolder);
            panel.Children.Add(_openFolderAfter);
            panel.Children.Add(_keepProgress);
            panel.Children.Add(_applyButton);

            Root = panel;
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            _live = ExtractionDefaults.Load(AppSettings.Load(_settingsPath));
            _doubleClick.SelectedIndex = _live.DoubleClick == ArchiveDoubleClick.ExtractDirectly ? 1 : 0;
            _singleFileToFolder.IsChecked = _live.SingleFileToFolder;
            _openFolderAfter.IsChecked = _live.OpenFolderAfterExtract;
            _keepProgress.IsChecked = _live.KeepProgressWindow;
            UpdateApplyState();
        }

        public void Commit()
        {
            if (_applyButton.IsEnabled)
            {
                Apply();
            }
        }

        private ExtractionDefaults Selection() => new(
            _singleFileToFolder.IsChecked == true,
            _openFolderAfter.IsChecked == true,
            _keepProgress.IsChecked == true,
            _doubleClick.SelectedIndex == 1
                ? ArchiveDoubleClick.ExtractDirectly
                : ArchiveDoubleClick.OpenInAndzip);

        private void UpdateApplyState() => _applyButton.IsEnabled = !Selection().Equals(_live);

        private void Apply()
        {
            var desired = Selection();
            if (desired.Equals(_live))
            {
                return;
            }

            var settings = AppSettings.Load(_settingsPath);
            desired.Save(settings);
            settings.Save(_settingsPath);
            Reload();

            // 刻意不碰注册表：双击走哪条路由启动时读设置决定，不是由哪个动词被调用决定 ——
            // 早先把默认动词改成 extract 会留下「动词名写了、命令键不存在」的半截状态，
            // Windows 找不到动词就退回 open，用户看到的就是"设了没反应"。
        }
    }

    /// <summary>
    /// 查看页：界面骨架露哪几块。
    /// <para>
    /// 只放真正接得上线的五块。参考软件这一页上的「列设置」「图像预览」「压缩文件注释」「置顶」
    /// 「颜色设置」「视图样式子菜单」都没做 —— 前四个对应的功能本身还不存在，
    /// 摆一个勾了什么都不发生的复选框比不做更糟。
    /// </para>
    /// </summary>
    private sealed class ViewPage : ISettingsPage
    {
        private readonly string _settingsPath;
        private readonly SettingToggle _toolBar;
        private readonly SettingToggle _statusBar;
        private readonly SettingToggle _tree;
        private readonly SettingToggle _iconDefault;
        private readonly SettingToggle _autoExpand;
        private readonly ComboBox _theme;
        private readonly Button _applyButton;

        /// <summary>图标配色那一节，只在 Windows 上存在（它做的是快捷方式与注册表）。</summary>
        private readonly IconStudioSection? _icons;

        private ViewDefaults _live = ViewDefaults.Fallback;

        public ViewPage(LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_VIEW";
            _settingsPath = settingsPath;

            _toolBar = Check(lang.Label("TEXT_SETTINGS_VIEW_TOOLBAR"), UpdateApplyState);
            _statusBar = Check(lang.Label("TEXT_SETTINGS_VIEW_STATUS"), UpdateApplyState);
            _tree = Check(lang.Label("TEXT_SETTINGS_VIEW_TREE"), UpdateApplyState);
            _iconDefault = Check(lang.Label("TEXT_SETTINGS_VIEW_ICON_DEFAULT"), UpdateApplyState);
            _autoExpand = Check(lang.Label("TEXT_SETTINGS_VIEW_AUTO_EXPAND"), UpdateApplyState);

            // 主题用下拉而不是三个单选钮：它是"多选一"而不是三个独立开关，
            // 而且"跟随系统"与"浅色/深色"是同一维度的三种取值。
            _theme = new ComboBox
            {
                Width = 260,
                ItemsSource = new[]
                {
                    lang.Label("TEXT_SETTINGS_VIEW_THEME_SYSTEM"),
                    lang.Label("TEXT_SETTINGS_VIEW_THEME_LIGHT"),
                    lang.Label("TEXT_SETTINGS_VIEW_THEME_DARK"),
                },
            }.Picker();
            _theme.SelectionChanged += (_, _) => UpdateApplyState();

            _applyButton = new Button
            {
                Content = lang.Label("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, Design.Space.X2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Ghost();
            _applyButton.Click += (_, _) => Apply();

            var panel = new StackPanel();
            panel.Children.Add(SectionTitle(lang.Label("TEXT_SETTINGS_VIEW_TITLE")));
            panel.Children.Add(_toolBar);
            panel.Children.Add(_statusBar);
            panel.Children.Add(_tree);
            panel.Children.Add(_iconDefault);
            panel.Children.Add(_autoExpand);
            panel.Children.Add(SettingRow.Labeled(lang.Label("TEXT_SETTINGS_VIEW_THEME"), _theme,
                lang, "TEXT_SETTINGS_VIEW_THEME_HINT"));
            panel.Children.Add(new TextBlock
            {
                Text = lang.Label("TEXT_SETTINGS_VIEW_APPLY_HINT"),
                Foreground = HintText,
                FontSize = Design.Type.Caption,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, Design.Space.X2, 0, 0),
            });
            panel.Children.Add(_applyButton);

            if (OperatingSystem.IsWindows())
            {
                _icons = new IconStudioSection(lang, settings, settingsPath);
                panel.Children.Add(_icons.Root);
            }

            // 加了图标配色这一节之后，这一页比窗口高（两个预览 + 五格颜色 + 一排按钮），
            // 所以整页要能滚 —— 与文件关联页同一件事：内容长就套 ScrollViewer，不撑窗口尺寸。
            // 少了这一层，真机上看到的是"图标配色"这个标题与一排预设色片，下面全在窗口外面。
            Root = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            _live = ViewDefaults.Load(AppSettings.Load(_settingsPath));
            _toolBar.IsChecked = _live.ShowToolBar;
            _statusBar.IsChecked = _live.ShowStatusBar;
            _tree.IsChecked = _live.ShowTree;
            _iconDefault.IsChecked = _live.IconViewByDefault;
            _autoExpand.IsChecked = _live.AutoExpandTree;
            _theme.SelectedIndex = (int)_live.Theme;

            // 判空挡不住平台分析器：它要的是"这一段只在 Windows 上跑"这句话本身。
            if (OperatingSystem.IsWindows())
            {
                _icons?.Reload();
            }

            UpdateApplyState();
        }

        public void Commit()
        {
            if (_applyButton.IsEnabled)
            {
                Apply();
            }
        }

        private ViewDefaults Selection() => new(
            _toolBar.IsChecked == true,
            _statusBar.IsChecked == true,
            _tree.IsChecked == true,
            _iconDefault.IsChecked == true,
            _autoExpand.IsChecked == true,
            // 下拉的顺序就是 ThemeMode 的声明顺序（System / Light / Dark），
            // 所以这里直接转 —— 加新值时要一起看这里。
            (ThemeMode)Math.Clamp(_theme.SelectedIndex, 0, 2));

        private void UpdateApplyState() => _applyButton.IsEnabled = !Selection().Equals(_live);

        private void Apply()
        {
            var desired = Selection();
            if (desired.Equals(_live))
            {
                return;
            }

            var settings = AppSettings.Load(_settingsPath);
            desired.Save(settings);
            settings.Save(_settingsPath);
            Reload();
        }
    }

    /// <summary>
    /// 「设置 → 查看」里的图标配色（2.5.0）：五段渐变颜色、四档方向、两种字形，
    /// 改一处立刻重画预览，能导出 SVG / PNG，也能设成这台机器上的程序图标。
    /// <para><strong>为什么这一节自己落盘、不等页脚的「应用」</strong>：预览要的就是即时反馈
    /// （参考工具的原文是"改色即时生效"），而那一颗「应用」管的是界面骨架的五个开关。
    /// 两套生效时机混在一起，只会让用户分不清哪一处要按哪一颗。</para>
    /// <para><strong>为什么颜色是"色块 + 十六进制框"而不是系统取色器</strong>：
    /// 取色器要 P/Invoke <c>comdlg32</c> 的 <c>ChooseColor</c>，而这一节的真正契约是
    /// "我能把颜色说清楚"——十六进制就是那个说法，它同时是导出的 SVG 里写的东西。</para>
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class IconStudioSection
    {
        private static readonly (string Key, string LabelKey)[] Fields =
        [
            (SettingKeys.IconFront0, "TEXT_ICON_FRONT_0"),
            (SettingKeys.IconFront1, "TEXT_ICON_FRONT_1"),
            (SettingKeys.IconFront2, "TEXT_ICON_FRONT_2"),
            (SettingKeys.IconBack0, "TEXT_ICON_BACK_0"),
            (SettingKeys.IconBack1, "TEXT_ICON_BACK_1"),
        ];

        /// <summary>四档方向的箭头。方向不是文案，不进气泡语言包。</summary>
        private static readonly string[] Arrows = ["↘", "↓", "→", "↙"];

        private readonly LangCatalog _lang;
        private readonly string _settingsPath;
        private readonly IconStudio _studio;
        private readonly InstallationService _installation;

        private readonly TextBox[] _hexes = new TextBox[Fields.Length];
        private readonly Border[] _swatches = new Border[Fields.Length];
        private readonly Image[] _previews = new Image[2];

        // 三排按钮收在 List 里而不是定长数组：那一排有几个是这一节自己建的时候决定的，
        // 写死长度等于同一个数在两个地方各写一遍。
        private readonly List<Button> _presets = [];
        private readonly List<Button> _directions = [];
        private readonly List<Button> _glyphButtons = [];
        private readonly TextBlock _status = new();

        private readonly AppSettings _shared;
        private IconPreferences _state = IconPreferences.Fallback;

        /// <summary>把值填回控件的那一小段时间里，不许触发重画与落盘。</summary>
        private bool _loading;

        public IconStudioSection(LangCatalog lang, AppSettings settings, string settingsPath)
        {
            _lang = lang;
            _settingsPath = settingsPath;
            _shared = settings;
            _studio = new IconStudio();
            _installation = new InstallationService(lang);

            var root = new StackPanel { Margin = new Thickness(0, Design.Space.X8, 0, 0) };

            root.Children.Add(SectionTitle(lang.Label("TEXT_ICON_TITLE")));
            root.Children.Add(Hint(lang.Label("TEXT_ICON_HINT")));

            root.Children.Add(ChipRow(lang.Label("TEXT_ICON_PRESETS"), IconPalette.Presets.Length, _presets, (chip, i) =>
            {
                chip.Width = 34;
                chip.Height = 24;
                chip.Padding = new Thickness(0);
                chip.CornerRadius = Design.Radius.Sm;
                chip.Background = GradientOf(IconPalette.Presets[i]);
                // 读屏与测试都靠这一句认片：色片上没有文字，否则念出来只是"按钮"。
                var preset = IconPalette.Presets[i];
                Avalonia.Automation.AutomationProperties.SetName(
                    chip, $"{preset.Front0} {preset.Front1} {preset.Front2}");
                chip.Click += (_, _) => PickPreset(i);
            }));

            root.Children.Add(GlyphRow());

            root.Children.Add(ChipRow(lang.Label("TEXT_ICON_DIRECTION"), Arrows.Length, _directions, (chip, i) =>
            {
                chip.Width = 34;
                chip.Height = 26;
                chip.Padding = new Thickness(0);
                chip.CornerRadius = Design.Radius.Sm;
                chip.Background = Design.Surface.Base;
                chip.BorderBrush = Design.Line.Strong;
                chip.BorderThickness = new Thickness(1);
                chip.Content = Arrows[i];
                chip.Click += (_, _) => PickDirection(i);
            }));

            // 五格颜色：两列排，跟参考工具一样"起 / 中 / 止 / 后层起 / 后层止"。
            var colors = new UniformGrid { Rows = 0, Columns = 2, Margin = new Thickness(0, Design.Space.X3, 0, 0) };
            for (var i = 0; i < Fields.Length; i++)
            {
                colors.Children.Add(ColorRow(i));
            }

            root.Children.Add(colors);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Design.Space.X2,
                Margin = new Thickness(0, Design.Space.X4, 0, 0),
            };

            var derive = new Button { Content = lang.Label("TEXT_ICON_DERIVE") }.Ghost();
            derive.Click += (_, _) => DeriveBack();

            var exportSvg = new Button { Content = lang.Label("TEXT_ICON_EXPORT_SVG") }.Ghost();
            exportSvg.Click += async (_, _) => await ExportAsync(svg: true);

            var exportPng = new Button { Content = lang.Label("TEXT_ICON_EXPORT_PNG") }.Ghost();
            exportPng.Click += async (_, _) => await ExportAsync(svg: false);

            var apply = new Button { Content = lang.Label("TEXT_ICON_APPLY") }.Accent();
            apply.Click += (_, _) => ApplyToDesktop();

            var restore = new Button { Content = lang.Label("TEXT_ICON_RESTORE") }.Ghost();
            restore.Click += (_, _) => RestoreDefault();

            actions.Children.Add(derive);
            actions.Children.Add(exportSvg);
            actions.Children.Add(exportPng);
            actions.Children.Add(apply);
            actions.Children.Add(restore);
            root.Children.Add(actions);

            _status.FontSize = Design.Type.Caption;
            _status.Foreground = HintText;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, Design.Space.X3, 0, 0);
            root.Children.Add(_status);

            Root = root;
            Reload();
        }

        public Control Root { get; }

        /// <summary>从磁盘重读这一节的状态（窗口打开时与「恢复默认」之后各走一次）。</summary>
        public void Reload()
        {
            _state = IconPreferences.Load(AppSettings.Load(_settingsPath));
            SyncControls();
            Repaint();
        }

        private void SyncControls()
        {
            _loading = true;

            var palette = _state.Palette;
            var values = new[] { palette.Front0, palette.Front1, palette.Front2, palette.Back0, palette.Back1 };
            for (var i = 0; i < values.Length; i++)
            {
                _hexes[i].Text = values[i];
            }

            for (var i = 0; i < _presets.Count; i++)
            {
                _presets[i].BorderThickness = new Thickness(IconPalette.Presets[i] == palette ? 2 : 0);
            }

            for (var i = 0; i < _directions.Count; i++)
            {
                _directions[i].BorderThickness = new Thickness(palette.Direction == i ? 2 : 1);
            }

            _loading = false;
        }

        /// <summary>重画两块预览与五个色块。颜色一改就走这里，所以它必须便宜。</summary>
        private void Repaint()
        {
            var current = new[]
            {
                _state.Palette.Front0, _state.Palette.Front1, _state.Palette.Front2,
                _state.Palette.Back0, _state.Palette.Back1,
            };

            for (var i = 0; i < _swatches.Length; i++)
            {
                // 那一格里正在打的字认得就用它（用户一边改一边看色块变），认不得退回已存的那份。
                var hex = IconPalette.Normalize(_hexes[i].Text) ?? current[i];
                var (r, g, b) = IconPalette.Rgb(hex);
                _swatches[i].Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            }

            for (var i = 0; i < _previews.Length; i++)
            {
                var glyph = i == 0 ? IconGlyph.Overflow : IconGlyph.Punched;
                var png = IconPainter.Png(glyph, _state.Palette, PreviewEdge);
                var bitmap = new System.IO.MemoryStream(png);
                _previews[i].Source = new Avalonia.Media.Imaging.Bitmap(bitmap);
                _glyphButtons[i].BorderThickness = new Thickness(_state.Glyph == glyph ? 2 : 1);
                _glyphButtons[i].BorderBrush = _state.Glyph == glyph ? Design.Ink.Primary : Design.Line.Strong;
            }
        }

        private const int PreviewEdge = 64;

        /// <summary>两种字形各一张预览，点哪张用哪张 —— 与参考工具并排放的两张卡片同一个形状。</summary>
        private Control GlyphRow()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Design.Space.X2,
                Margin = new Thickness(0, Design.Space.X3, 0, 0),
            };

            row.Children.Add(new TextBlock
            {
                Text = _lang.Label("TEXT_ICON_GLYPH"),
                Width = 76,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = HintText,
                FontSize = Design.Type.Caption,
            });

            var names = new[] { "TEXT_ICON_GLYPH_OVERFLOW", "TEXT_ICON_GLYPH_PUNCHED" };

            for (var i = 0; i < 2; i++)
            {
                var captured = i;
                var image = new Image { Width = PreviewEdge, Height = PreviewEdge };
                var plate = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Spacing = Design.Space.X1,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

                plate.Children.Add(image);
                plate.Children.Add(new TextBlock
                {
                    Text = _lang.Label(names[i]),
                    FontSize = Design.Type.Caption,
                    Foreground = HintText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });

                var button = new Button
                {
                    Content = plate,
                    Padding = new Thickness(Design.Space.X2),
                    CornerRadius = Design.Radius.Md,
                    Background = Design.Surface.Base,
                    BorderBrush = Design.Line.Strong,
                    BorderThickness = new Thickness(1),
                }.PinFace();

                button.Click += (_, _) => PickGlyph(captured == 0 ? IconGlyph.Overflow : IconGlyph.Punched);

                _previews[i] = image;
                _glyphButtons.Add(button);
                row.Children.Add(button);
            }

            return row;
        }

        private void PickPreset(int index)
        {
            var chosen = IconPalette.Presets[index];
            _state = _state with { Palette = chosen with { Direction = _state.Palette.Direction } };
            Commit();
        }

        private void PickDirection(int index)
        {
            _state = _state with { Palette = _state.Palette with { Direction = index } };
            Commit();
        }

        private void PickGlyph(IconGlyph glyph)
        {
            _state = _state with { Glyph = glyph };
            Commit();
        }

        private void DeriveBack()
        {
            _state = _state with { Palette = _state.Palette.WithBackFromFront() };
            Commit();
        }

        private void Commit()
        {
            if (_loading)
            {
                return;
            }

            Persist(settings => _state.Save(settings));

            // 已经设成桌面图标的人改颜色时，磁盘上那份 .ico 要跟着重画 ——
            // 不然预览是新颜色、桌面是旧颜色，用户会以为"改回去又生效了"。
            if (_state.Applied)
            {
                _studio.Apply(_state, IconStudio.ExistingShortcuts(_installation));
            }

            SyncControls();
            Repaint();
        }

        private void ApplyToDesktop()
        {
            _state = _state with { Applied = true };
            var outcome = _studio.Apply(_state, IconStudio.ExistingShortcuts(_installation));
            Persist(settings => _state.Save(settings));
            _status.Text = Describe(outcome);
        }

        private void RestoreDefault()
        {
            _state = IconPreferences.Fallback;
            var outcome = _studio.Restore(IconStudio.ExistingShortcuts(_installation));
            Persist(settings => _state.Save(settings));

            // 「恢复」也要说败。写死一句"已恢复"的话，桌面上还会留着用户自己挑的那一张。
            _status.Text = outcome.Failure is null
                ? _lang.Label("TEXT_ICON_RESTORED")
                : _lang.Format("TEXT_ICON_RESTORE_FAILED", ("reason", outcome.Failure));

            SyncControls();
            Repaint();
        }

        /// <summary>
        /// 照实说改了几处。<strong>绿色版没有快捷方式</strong>，那种情况下说"桌面图标已更新"
        /// 就是谎话（1.8.4 那句"桌面与开始菜单的快捷方式已创建"教的）。
        /// </summary>
        private string Describe(IconStudio.Outcome outcome)
        {
            if (outcome.Failure is not null)
            {
                return _lang.Format("TEXT_ICON_APPLY_FAILED", ("reason", outcome.Failure));
            }

            if (outcome.ShortcutsUpdated == 0)
            {
                return _lang.Label("TEXT_ICON_APPLIED_NO_SHORTCUT");
            }

            return outcome.ShortcutsBlocked
                ? _lang.Format("TEXT_ICON_APPLY_PARTIAL", ("n", outcome.ShortcutsUpdated.ToString()))
                : _lang.Format("TEXT_ICON_APPLIED",
                    ("n", outcome.ShortcutsUpdated.ToString()),
                    ("m", outcome.ProgIdsUpdated.ToString()));
        }

        private async Task ExportAsync(bool svg)
        {
            var provider = GetTopLevel(_status)?.StorageProvider;
            if (provider is null)
            {
                return;
            }

            var name = IconStudio.SuggestedName(_state.Glyph, svg ? ".svg" : ".png");
            var choice = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = _lang.Label(svg ? "TEXT_ICON_EXPORT_SVG" : "TEXT_ICON_EXPORT_PNG"),
                SuggestedFileName = name,
                DefaultExtension = svg ? "svg" : "png",
            });

            var target = choice?.TryGetLocalPath();
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            if (svg)
            {
                IconStudio.WriteSvg(target, IconArt.Svg(_state.Glyph, _state.Palette));
            }
            else
            {
                File.WriteAllBytes(target, IconPainter.Png(_state.Glyph, _state.Palette, 512));
            }

            _status.Text = _lang.Format("TEXT_ICON_EXPORTED", ("path", target));
        }

        /// <summary>存完要把主窗口那份快照同步回来 —— 它下次写盘是整文件覆盖，
        /// 拿旧快照写就把这一节的键抹掉了（0.13.0「设置不生效」就是这么来的）。
        /// </summary>
        private void Persist(Action<AppSettings> mutate)
        {
            var fresh = AppSettings.Load(_settingsPath);
            mutate(fresh);
            fresh.Save(_settingsPath);
            _shared.Reload(_settingsPath);
        }

        private Control ColorRow(int index)
        {
            var swatch = new Border
            {
                Width = 30,
                Height = 24,
                CornerRadius = Design.Radius.Sm,
                BorderBrush = Design.Line.Strong,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, Design.Space.X2, 0),
            };

            var hex = new TextBox
            {
                Width = 92,
                FontSize = Design.Type.Caption,
                Text = _state.Palette.Front0,
            }.Field();

            var captured = index;
            hex.LostFocus += (_, _) => CommitHex(captured, hex.Text);
            hex.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Return)
                {
                    CommitHex(captured, hex.Text);
                }
            };

            _swatches[index] = swatch;
            _hexes[index] = hex;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Design.Space.X2,
                Margin = new Thickness(0, Design.Space.X1, 0, Design.Space.X1),
            };

            row.Children.Add(new TextBlock
            {
                Text = _lang.Label(Fields[index].LabelKey),
                Width = 76,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = HintText,
                FontSize = Design.Type.Caption,
            });
            row.Children.Add(swatch);
            row.Children.Add(hex);
            return row;
        }

        /// <summary>
        /// 认得的写回去，认不得的<strong>保留原值并把那格照原样显示</strong>。
        /// 一个打错的十六进制如果被当成黑色，用户看到的是"图标突然黑了"，然后开始怀疑软件。
        /// </summary>
        private void CommitHex(int index, string? text)
        {
            if (_loading || IconPalette.Normalize(text) is not { } normalized)
            {
                SyncControls();
                return;
            }

            var p = _state.Palette;
            _state = _state with
            {
                Palette = index switch
                {
                    0 => p with { Front0 = normalized },
                    1 => p with { Front1 = normalized },
                    2 => p with { Front2 = normalized },
                    3 => p with { Back0 = normalized },
                    _ => p with { Back1 = normalized },
                },
            };

            Commit();
        }

        /// <summary>
        /// 一排色片：一个标签 + 几颗自己上色的按钮。<strong>建几颗由调用方说</strong>，
        /// 不是这一排自己数该有几个 —— 预设与方向两排的数量是两件不相干的事。
        /// </summary>
        private Control ChipRow(string label, int count, List<Button> buttons, Action<Button, int> configure)
        {

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Design.Space.X2,
                Margin = new Thickness(0, Design.Space.X3, 0, 0),
            };

            row.Children.Add(new TextBlock
            {
                Text = label,
                Width = 76,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = HintText,
                FontSize = Design.Type.Caption,
            });

            var chips = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Design.Space.X1,
            };

            for (var i = 0; i < count; i++)
            {
                var chip = new Button().PinFace();
                chip.BorderBrush = Design.Ink.Primary;
                chip.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                configure(chip, i);
                buttons.Add(chip);
                chips.Children.Add(chip);
            }

            row.Children.Add(chips);
            return row;
        }

        /// <summary>把一套配色画成一颗色片（预览与预设共用这一个画法，不许两处各写一遍）。</summary>
        private static IBrush GradientOf(IconPalette palette)
        {
            var (x1, y1, x2, y2) = palette.Anchors;
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(x1 / 100, y1 / 100, RelativeUnit.Relative),
                EndPoint = new RelativePoint(x2 / 100, y2 / 100, RelativeUnit.Relative),
            };

            brush.GradientStops.Add(Stop(palette.Front0, 0));
            brush.GradientStops.Add(Stop(palette.Front1, 0.52));
            brush.GradientStops.Add(Stop(palette.Front2, 1));
            return brush;

            static GradientStop Stop(string hex, double offset)
            {
                var (r, g, b) = IconPalette.Rgb(hex);
                return new GradientStop(Color.FromRgb(r, g, b), offset);
            }
        }
    }

    /// <summary>
    /// 语言页：从<strong>实际加载到的</strong>语言包里列出候选，而不是写死两个名字。
    /// <para>
    /// 这样程序目录 <c>langs/</c> 下多放一份 <c>Russian.lang</c>，这一页就会多一项，
    /// 而且选了确实能切 —— 不会出现"列表里有、切过去又回退"的假选项。
    /// </para>
    /// <para>
    /// 换语言要重启才全面生效：菜单、工具栏、标题这些文案是在控件构造时取一次就固化了的。
    /// 这一点如实写在页面上，而不是假装立刻生效。
    /// </para>
    /// </summary>
    private sealed class LanguagePage : ISettingsPage
    {
        private readonly LangCatalog _catalog;
        private readonly string _settingsPath;
        private readonly ComboBox _language;
        private readonly Button _applyButton;

        private string _live;

        public LanguagePage(LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_LANGUAGE";
            _catalog = lang;
            _settingsPath = settingsPath;
            _live = settings.GetString(SettingKeys.Language) is { Length: > 0 } stored
                ? stored : lang.CurrentLanguage;

            _language = new ComboBox
            {
                ItemsSource = lang.Languages
                    .OrderBy(code => code, StringComparer.Ordinal)
                    .Select(code => new DisplayItem(lang.GetLanguageDisplayName(code), code))
                    .ToList(),
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Picker();
            _language.SelectionChanged += (_, _) => UpdateApplyState();

            _applyButton = new Button
            {
                Content = lang.Label("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Ghost();
            _applyButton.Click += (_, _) => Apply();

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, 20, 0, 0),
            };
            row.Children.Add(new TextBlock
            {
                Text = lang.Label("TEXT_SETTINGS_LANGUAGE_LABEL"),
                Width = 170,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(_language);

            var panel = new StackPanel();
            panel.Children.Add(SectionTitle(lang.Label("TEXT_SETTINGS_LANGUAGE_TITLE")));
            panel.Children.Add(row);
            panel.Children.Add(Hint(lang.Label("TEXT_SETTINGS_LANGUAGE_HINT")));
            panel.Children.Add(Hint(lang.Label("TEXT_SETTINGS_LANGUAGE_RESTART")));
            panel.Children.Add(_applyButton);

            Root = panel;
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            _live = AppSettings.Load(_settingsPath).GetString(SettingKeys.Language) is { Length: > 0 } stored
                ? stored : _catalog.CurrentLanguage;
            Select(_live);
            UpdateApplyState();
        }

        public void Commit()
        {
            if (_applyButton.IsEnabled)
            {
                Apply();
            }
        }

        private void Select(string code)
        {
            // 设置里存了一个当前没有的语言包（换机器、删了 langs/）时别硬塞：
            // 停在列表第一项，用户看得见也能马上改，比一个空白框好。
            var match = _language.Items.OfType<DisplayItem>()
                .FirstOrDefault(item => string.Equals((string?)item.Value, code, StringComparison.OrdinalIgnoreCase));
            _language.SelectedItem = match ?? _language.Items.OfType<DisplayItem>().FirstOrDefault();
        }

        private string? Selection() => (_language.SelectedItem as DisplayItem)?.Value as string;

        private void UpdateApplyState()
            => _applyButton.IsEnabled = Selection() is { } chosen
               && !string.Equals(chosen, _live, StringComparison.OrdinalIgnoreCase);

        private void Apply()
        {
            if (Selection() is not { } chosen)
            {
                return;
            }

            var settings = AppSettings.Load(_settingsPath);
            settings.SetString(SettingKeys.Language, chosen);
            settings.Save(_settingsPath);

            // 当场把目录翻过去：之后新建的界面（对话框、下次打开的压缩包视图）立刻是新语言，
            // 已经画好的那些要等重启。两者都如实如此，不做"看起来全变了"的假动作。
            _catalog.SetLanguage(chosen);
            Reload();
        }
    }

    /// <summary>
    /// 高级页：标题栏、临时目录、导出设置。
    /// <para>
    /// 参考软件这一页上的其它项都没做，理由各自不同，写下来免得下次被当成遗漏：
    /// 「删除时移入回收站」「允许删除已打开的压缩包」依赖一个还不存在的删除功能；
    /// 「输出文件时预分配磁盘空间」「文件被占用时仍然压缩」是引擎侧的写入策略，
    /// 界面这边勾不动；「退出子进程时删除临时文件」—— 本程序不留子进程，
    /// 临时文件在 <c>SingleFileArchive.Dispose</c> 里就删了；「启动时显示开始画面」与
    /// 「压缩/解压使用模态框」描述的正是现在的行为，做成开关等于制造两个没人会去关的选项。
    /// </para>
    /// </summary>
    private sealed class AdvancedPage : ISettingsPage
    {
        private readonly SettingsWindow _owner;
        private readonly LangCatalog _lang;
        private readonly string _settingsPath;
        private readonly SettingToggle _fullPathTitle;
        private readonly TextBox _tempFolder;
        private readonly Button _applyButton;
        private readonly Button _exportButton;

        private (bool FullPath, string Temp) _live;

        public AdvancedPage(SettingsWindow owner, LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_ADVANCED";
            _owner = owner;
            _lang = lang;
            _settingsPath = settingsPath;

            _fullPathTitle = Check(lang.Label("TEXT_SETTINGS_TITLE_FULL_PATH"), UpdateApplyState);
            _tempFolder = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left }.Field();
            _tempFolder.TextChanged += (_, _) => UpdateApplyState();

            var browse = new Button { Content = lang.Label("TEXT_BTN_BROWSE_TEMP"), MinWidth = 110 }.Ghost();
            browse.Click += async (_, _) => await PickFolderAsync();

            _applyButton = new Button
            {
                Content = lang.Label("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Ghost();
            _applyButton.Click += (_, _) => Apply();

            var export = new Button
            {
                Content = lang.Label("TEXT_BTN_EXPORT_SETTINGS"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Ghost();
            export.Click += async (_, _) => await ExportAsync();

            var panel = new StackPanel();
            panel.Children.Add(SectionTitle(lang.Label("TEXT_SETTINGS_ADVANCED_TITLE")));
            panel.Children.Add(_fullPathTitle);
            panel.Children.Add(LabeledRow(lang.Label("TEXT_SETTINGS_TEMP_FOLDER"), _tempFolder, browse));
            panel.Children.Add(Hint(lang.Label("TEXT_SETTINGS_TEMP_FOLDER_HINT")));
            panel.Children.Add(_applyButton);
            panel.Children.Add(export);
            _exportButton = export;

            Root = panel;
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            var settings = AppSettings.Load(_settingsPath);
            _live = (
                settings.GetBool(SettingKeys.TitleBarFullPath, fallback: false),
                settings.GetString(SettingKeys.TempDirectory));

            _fullPathTitle.IsChecked = _live.FullPath;
            _tempFolder.Text = _live.Temp;
            // 导出的是磁盘上那份文件，所以它的可用性跟着文件走，不跟着未保存的改动走。
            _exportButton.IsEnabled = File.Exists(_settingsPath);
            UpdateApplyState();
        }

        public void Commit()
        {
            if (_applyButton.IsEnabled)
            {
                Apply();
            }
        }

        private (bool FullPath, string Temp) Selection()
            => (_fullPathTitle.IsChecked == true, _tempFolder.Text?.Trim() ?? string.Empty);

        private void UpdateApplyState() => _applyButton.IsEnabled = !Selection().Equals(_live);

        private void Apply()
        {
            var desired = Selection();
            if (desired.Equals(_live))
            {
                return;
            }

            var settings = AppSettings.Load(_settingsPath);
            settings.SetBool(SettingKeys.TitleBarFullPath, desired.FullPath);
            settings.SetString(SettingKeys.TempDirectory, desired.Temp.Length == 0 ? null : desired.Temp);
            settings.Save(_settingsPath);

            // 立刻生效，而不是等下次启动：正在解压分卷包时用不上新目录，
            // 但把标题栏改完要重开程序才能看到，只会让人觉得设置没保存。
            ArchiveEngine.TempDirectory = desired.Temp;
            Reload();
        }

        private async Task PickFolderAsync()
        {
            var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _lang.Label("TEXT_SETTINGS_TEMP_FOLDER"),
                AllowMultiple = false,
            });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                _tempFolder.Text = path;
            }
        }

        /// <summary>
        /// 把 <c>settings.ini</c> 原样复制到用户选的位置 —— 绿色版换机器时唯一要带的东西就是它
        /// （外加注册表里那些关联，而那部分导出成文件也没法导入）。
        /// </summary>
        private async Task ExportAsync()
        {
            var suggested = Path.GetFileName(_settingsPath);
            var target = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = _lang.Label("TEXT_BTN_EXPORT_SETTINGS"),
                SuggestedFileName = suggested,
                DefaultExtension = "ini",
                FileTypeChoices = [new FilePickerFileType(_lang.Label("TEXT_SETTINGS_EXPORT_FILTER")) { Patterns = ["*.ini"] }],
            });

            if (target?.TryGetLocalPath() is not { } path)
            {
                return;
            }

            try
            {
                File.Copy(_settingsPath, path, overwrite: true);
                Message(_lang.Format("TEXT_SETTINGS_EXPORT_DONE", ("path", path)));
            }
            catch (Exception ex)
            {
                Message(_lang.Label("TEXT_SETTINGS_EXPORT_FAILED") + ex.Message);
            }
        }

        private void Message(string text)
            => _ = new MessageWindow(_lang, _lang.Label("TEXT_TITLE_SETTINGS"), text).ShowDialog(_owner);
    }

    /// <summary>
    /// 安全页：本次运行的口令记忆。
    /// <para>页面上那句"为什么没有跨重启的口令管理器"是刻意向用户解释少做了什么，
    /// 而不是留一个空板块让人以为还没加载出来。</para>
    /// </summary>
    private sealed class SecurityPage : ISettingsPage
    {
        private readonly SettingsWindow _owner;
        private readonly LangCatalog _lang;
        private readonly string _settingsPath;
        private readonly SettingToggle _remember;
        private readonly TextBlock _remembered;
        private readonly Button _clearButton;
        private readonly Button _applyButton;

        private bool _live;

        public SecurityPage(SettingsWindow owner, LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_SECURITY";
            _owner = owner;
            _lang = lang;
            _settingsPath = settingsPath;

            _remember = Check(lang.Label("TEXT_SETTINGS_REMEMBER_PASSWORDS"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_REMEMBER_PASSWORDS_HINT"));
            _remembered = new TextBlock { Foreground = HintText, Margin = new Thickness(24, 4, 0, 0) };
            _clearButton = new Button
            {
                Content = lang.Label("TEXT_BTN_FORGET_PASSWORDS"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Ghost();
            _clearButton.Click += (_, _) => { Session.Passwords.Clear(); RefreshRemembered(); };

            _applyButton = new Button
            {
                Content = lang.Label("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Ghost();
            _applyButton.Click += (_, _) => Apply();

            var panel = new StackPanel();
            panel.Children.Add(SectionTitle(lang.Label("TEXT_SETTINGS_SECURITY_TITLE")));
            panel.Children.Add(_remember);
            panel.Children.Add(_remembered);
            panel.Children.Add(_clearButton);
            panel.Children.Add(Hint(lang.Label("TEXT_SETTINGS_PASSWORD_MANAGER_NOTE")));
            panel.Children.Add(_applyButton);

            Root = panel;
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            _live = AppSettings.Load(_settingsPath).GetBool(SettingKeys.RememberPasswordsThisSession, fallback: true);
            _remember.IsChecked = _live;
            RefreshRemembered();
            UpdateApplyState();
        }

        public void Commit()
        {
            if (_applyButton.IsEnabled)
            {
                Apply();
            }
        }

        private void RefreshRemembered()
        {
            var count = Session.Passwords.Count;
            _remembered.Text = count == 0
                ? _lang.Label("TEXT_SETTINGS_NO_PASSWORDS")
                : _lang.Format("TEXT_SETTINGS_PASSWORDS_REMEMBERED", ("count", count.ToString()));
            _clearButton.IsEnabled = count > 0;
        }

        private void UpdateApplyState() => _applyButton.IsEnabled = (_remember.IsChecked == true) != _live;

        private void Apply()
        {
            var desired = _remember.IsChecked == true;
            if (desired == _live)
            {
                return;
            }

            var settings = AppSettings.Load(_settingsPath);
            settings.SetBool(SettingKeys.RememberPasswordsThisSession, desired);
            settings.Save(_settingsPath);

            Session.Passwords.Enabled = desired;
            if (!desired)
            {
                // 关掉的同时把已经记住的清掉 —— 不然"关掉"只是不再新增，
                // 用户以为没了、其实还在内存里。
                Session.Passwords.Clear();
            }

            RefreshRemembered();
            Reload();
        }
    }

    /// <summary>
    /// 压缩页：「新建压缩文件」对话框打开时的那一套默认值。
    /// <para>
    /// 这一页只决定<strong>对话框长什么样</strong>，压的时候仍可以逐次改 —— 所以它不会去动
    /// 已经排好队的任务，也不需要像文件关联那样碰注册表。落盘就是一行 <c>settings.ini</c>。
    /// </para>
    /// <para>
    /// 刻意没有口令与分卷：那两项每次都不一样，记住反而危险（上次压的是加密包，
    /// 这次压的是要发给别人的公开目录，把口令带过去是事故）。
    /// </para>
    /// </summary>
    private sealed class CompressionPage : ISettingsPage
    {
        private readonly SettingsWindow _owner;
        private readonly LangCatalog _lang;
        private readonly string _settingsPath;
        private readonly ComboBox _level;
        private readonly SettingToggle _noRootFolder;
        private readonly SettingToggle _excludeThumbs;
        private readonly TextBox _excludePatterns;
        private readonly SettingToggle _testAfter;
        private readonly SettingToggle _keepProgress;
        private readonly SettingToggle _preallocate;
        private readonly SettingToggle _shareSources;
        private readonly RadioButton _outputLast;
        private readonly RadioButton _outputFixed;
        private readonly TextBox _outputFolder;
        private readonly Button _applyButton;

        private CompressionDefaults _live = CompressionDefaults.Fallback;

        public CompressionPage(SettingsWindow owner, LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_COMPRESS";
            _lang = lang;
            _owner = owner;
            _settingsPath = settingsPath;

            _level = new ComboBox
            {
                ItemsSource = CompressionLevelOption.All
                    .Select(o => new DisplayItem(lang.Label(o.TextKey), o))
                    .ToList(),
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Picker();

            _noRootFolder = Check(lang.Label("TEXT_SETTINGS_COMPRESS_NO_ROOT"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_COMPRESS_NO_ROOT_HINT"));
            _excludeThumbs = Check(lang.Label("TEXT_SETTINGS_COMPRESS_EXCLUDE_THUMBS"), UpdateApplyState);
            _testAfter = Check(lang.Label("TEXT_SETTINGS_COMPRESS_TEST_AFTER"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_COMPRESS_TEST_AFTER_HINT"));
            _keepProgress = Check(lang.Label("TEXT_SETTINGS_COMPRESS_KEEP_PROGRESS"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_COMPRESS_KEEP_PROGRESS_HINT"));
            _preallocate = Check(lang.Label("TEXT_SETTINGS_COMPRESS_PREALLOC"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_COMPRESS_PREALLOC_HINT"));
            _shareSources = Check(lang.Label("TEXT_SETTINGS_COMPRESS_SHARE_SOURCES"), UpdateApplyState,
                lang.Label("TEXT_SETTINGS_COMPRESS_SHARE_SOURCES_HINT"));

            _excludePatterns = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left }.Field();
            _excludePatterns.TextChanged += (_, _) => UpdateApplyState();

            _outputLast = new RadioButton { Content = lang.Label("TEXT_SETTINGS_COMPRESS_OUTPUT_LAST"), IsChecked = true };
            _outputFixed = new RadioButton { Content = lang.Label("TEXT_SETTINGS_COMPRESS_OUTPUT_FIXED") };
            _outputLast.IsCheckedChanged += (_, _) => { RefreshOutputFolderEnabled(); UpdateApplyState(); };
            _outputFixed.IsCheckedChanged += (_, _) => { RefreshOutputFolderEnabled(); UpdateApplyState(); };

            _outputFolder = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left }.Field();
            _outputFolder.TextChanged += (_, _) => UpdateApplyState();

            var pickFolder = new Button { Content = lang.Label("TEXT_BTN_BROWSE"), MinWidth = 110 }.Ghost();
            pickFolder.Click += async (_, _) => await PickFolderAsync();

            _applyButton = new Button
            {
                Content = lang.Label("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            }.Ghost();
            _applyButton.Click += (_, _) => Apply();

            var panel = new StackPanel();
            panel.Children.Add(SectionTitle(lang.Label("TEXT_SETTINGS_COMPRESS_TITLE")));
            panel.Children.Add(SettingRow.Labeled(lang.Label("TEXT_SETTINGS_COMPRESS_DEFAULT_LEVEL"), _level,
                lang, "TEXT_SETTINGS_COMPRESS_LEVEL_HINT"));
            panel.Children.Add(_noRootFolder);
            panel.Children.Add(_excludeThumbs);
            panel.Children.Add(SettingRow.Labeled(lang.Label("TEXT_SETTINGS_COMPRESS_EXCLUDE_TYPES"), _excludePatterns,
                lang, "TEXT_SETTINGS_COMPRESS_EXCLUDE_HINT"));
            panel.Children.Add(_testAfter);
            panel.Children.Add(_keepProgress);
            panel.Children.Add(_preallocate);
            panel.Children.Add(_shareSources);

            panel.Children.Add(new TextBlock
            {
                Text = lang.Label("TEXT_SETTINGS_COMPRESS_OUTPUT_DIR"),
                Margin = new Thickness(0, 20, 0, 4),
            });
            panel.Children.Add(_outputLast);
            panel.Children.Add(_outputFixed);
            panel.Children.Add(LabeledRow(string.Empty, _outputFolder, pickFolder));
            panel.Children.Add(Hint(lang.Label("TEXT_SETTINGS_COMPRESS_OUTPUT_FIXED_HINT")));
            panel.Children.Add(_applyButton);

            Root = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            _live = CompressionDefaults.Load(ReadSettings());

            _level.SelectedIndex = Math.Max(0, CompressionLevelOption.PositionOf(_live.Level));
            _noRootFolder.IsChecked = !_live.IncludeRootFolder;
            _excludeThumbs.IsChecked = _live.ExcludeThumbsDb;
            _excludePatterns.Text = _live.ExcludePatternText;
            _testAfter.IsChecked = _live.TestAfterCreation;
            _keepProgress.IsChecked = _live.KeepProgressWindow;
            _preallocate.IsChecked = _live.PreallocateDisk;
            _shareSources.IsChecked = _live.ShareSources;
            _outputLast.IsChecked = _live.OutputMode == SettingKeys.OutputFolderMode.LastUsed;
            _outputFixed.IsChecked = _live.OutputMode == SettingKeys.OutputFolderMode.Fixed;
            _outputFolder.Text = _live.FixedOutputFolder ?? string.Empty;

            RefreshOutputFolderEnabled();
            UpdateApplyState();
        }

        public void Commit()
        {
            if (_applyButton.IsEnabled)
            {
                Apply();
            }
        }

        private void RefreshOutputFolderEnabled()
        {
            _outputFolder.IsEnabled = _outputFixed.IsChecked == true;
            _outputFolder.Opacity = _outputFolder.IsEnabled ? 1.0 : 0.5;
        }

        private CompressionDefaults Selection() => new(
            _level.SelectedItem is DisplayItem { Value: CompressionLevelOption level }
                ? level : CompressionLevelOption.Default,
            IncludeRootFolder: _noRootFolder.IsChecked != true,
            ExcludeThumbsDb: _excludeThumbs.IsChecked == true,
            // 存原文而不是解析结果：用户在框里打的字就是设置里存的东西，别替他改写。
            ExcludePatternText: _excludePatterns.Text?.Trim() ?? string.Empty,
            TestAfterCreation: _testAfter.IsChecked == true,
            KeepProgressWindow: _keepProgress.IsChecked == true,
            OutputMode: _outputFixed.IsChecked == true
                ? SettingKeys.OutputFolderMode.Fixed
                : SettingKeys.OutputFolderMode.LastUsed,
            FixedOutputFolder: string.IsNullOrWhiteSpace(_outputFolder.Text) ? null : _outputFolder.Text.Trim(),
            PreallocateDisk: _preallocate.IsChecked == true,
            ShareSources: _shareSources.IsChecked == true);

        private void UpdateApplyState() => _applyButton.IsEnabled = !Selection().Equals(_live);

        private void Apply()
        {
            var desired = Selection();
            if (desired.Equals(_live))
            {
                return;
            }

            var settings = ReadSettings();
            // 只覆盖这一页管的那些键；Load 出来的 Fallback 里那些"上次输出目录"之类的记账项
            // 不在 CompressionDefaults 里，所以不会被这一页顺手抹掉。
            desired.Save(settings);
            settings.Save(_settingsPath);
            Reload();
        }

        private AppSettings ReadSettings() => AppSettings.Load(_settingsPath);

        private async Task PickFolderAsync()
        {
            var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = _lang.Label("TEXT_SETTINGS_COMPRESS_OUTPUT_FIXED"),
                AllowMultiple = false,
            });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                _outputFolder.Text = path;
            }
        }
    }
}
