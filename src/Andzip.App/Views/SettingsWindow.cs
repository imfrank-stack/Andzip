using System.Diagnostics;
using Andzip.App.Services;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Andzip.App.Views;

/// <summary>
/// 设置窗口（选项 → 设置，F5）：左侧蓝色导航 + 右侧页面 + 底部「重置 / 确定」。
/// <para>
/// 已实装「常规」与「文件关联」两页，其余按参考软件的导航顺序列出并显示占位文案 ——
/// 空着的导航项比假装能用的控件诚实。
/// </para>
/// </summary>
public sealed class SettingsWindow : Window
{
    /// <summary>设置里保存「已勾选哪些关联」的键名。</summary>
    public const string AssociationSettingKey = "file_associations";

    private static readonly IBrush NavBlue = new SolidColorBrush(Color.Parse("#208CFB"));
    private static readonly IBrush NavBlueSelected = new SolidColorBrush(Color.Parse("#1668C8"));
    private static readonly IBrush PageText = new SolidColorBrush(Color.Parse("#1F1F1F"));
    private static readonly IBrush LinkBlue = new SolidColorBrush(Color.Parse("#1A6FD4"));
    private static readonly IBrush HintText = new SolidColorBrush(Color.Parse("#6B6B6B"));

    private readonly ContentControl _pageHost = new();
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.Ordinal);
    private readonly ISettingsPage[] _pages;
    private ISettingsPage? _currentPage;

    public SettingsWindow(LangCatalog lang, AppSettings settings, string settingsPath)
    {
        Title = lang.Get("TEXT_TITLE_SETTINGS");
        Width = 1000;
        Height = 660;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#F0F0F0"));
        FontSize = 14;

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
            new PlaceholderPage(lang, "TEXT_SETTINGS_VIEW"),
            new PlaceholderPage(lang, "TEXT_SETTINGS_SECURITY"),
            new PlaceholderPage(lang, "TEXT_SETTINGS_ADVANCED"),
            new PlaceholderPage(lang, "TEXT_SETTINGS_LANGUAGE"),
        ];

        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(0, 14, 0, 0) };
        for (var i = 0; i < navKeys.Length; i++)
        {
            var index = i;
            var button = NavButton(lang.Get(navKeys[i]), () => ShowPage(index));
            _navButtons[navKeys[i]] = button;
            nav.Children.Add(button);
        }

        var navPane = new Border { Background = NavBlue, Child = nav, Width = 190 };
        var contentPane = new Border
        {
            Background = Brushes.White,
            Margin = new Thickness(0, 14, 14, 0),
            Child = _pageHost,
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(navPane, 0);
        Grid.SetColumn(contentPane, 1);
        body.Children.Add(navPane);
        body.Children.Add(contentPane);

        var resetButton = new Button { Content = lang.Get("TEXT_BTN_RESET"), MinWidth = 120 };
        resetButton.Click += (_, _) => _currentPage?.Reload();
        var okButton = new Button { Content = lang.Get("TEXT_BTN_OK"), MinWidth = 120, IsDefault = true };
        okButton.Click += (_, _) => AcceptAndClose();

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(14, 10, 0, 12),
            Children = { resetButton, okButton },
        };

        var root = new DockPanel();
        var footerBar = new Border { Background = new SolidColorBrush(Color.Parse("#F0F0F0")), Child = footer };
        DockPanel.SetDock(footerBar, Dock.Bottom);
        root.Children.Add(footerBar);
        root.Children.Add(body);

        Content = root;
        ShowPage(0);
    }

    private void ShowPage(int index)
    {
        var page = _pages[index];
        _currentPage = page;

        foreach (var (key, button) in _navButtons)
        {
            button.Background = key == page.NavKey ? NavBlueSelected : NavBlue;
        }

        page.Reload();
        _pageHost.Content = page.Root;
    }

    private Button NavButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Foreground = Brushes.White,
            Background = NavBlue,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(22, 11, 10, 11),
            FontSize = 15,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void AcceptAndClose()
    {
        // 「确定」= 让当前页把没应用的改动落盘再关窗；右上角的 X 才是放弃改动。
        _currentPage?.Commit();
        Close();
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 17,
        FontWeight = FontWeight.Bold,
        Foreground = PageText,
    };

    /// <summary>控件下方那行灰色小字说明。</summary>
    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        Foreground = HintText,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(24, 4, 0, 0),
    };

    /// <summary>带"改动就重新算要不要允许应用"回调的复选框。好几个页面都要这个形状。</summary>
    private static CheckBox Check(string label, Action onToggled)
    {
        var box = new CheckBox { Content = label, Margin = new Thickness(0, 6, 0, 0) };
        box.IsCheckedChanged += (_, _) => onToggled();
        return box;
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
            panel.Children.Add(SectionTitle(lang.Get(navKey)));
            panel.Children.Add(new TextBlock
            {
                Text = lang.Get("TEXT_SETTINGS_NOT_IMPLEMENTED"),
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
    /// 常规页：双击行为 + 进度窗口能否按 Esc 取消。
    /// <para>
    /// 双击那一项不是界面偏好开关：程序启动时读它决定「开主窗口」还是「直接解压」，
    /// 所以对已经关联出去的所有扩展名立即生效，不需要回头改注册表。
    /// </para>
    /// </summary>
    private sealed class GeneralPage : ISettingsPage
    {
        private readonly AppSettings _settings;
        private readonly string _settingsPath;
        private readonly ComboBox _doubleClick;
        private readonly CheckBox _escCancel;

        public GeneralPage(SettingsWindow owner, LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_GENERAL";
            _settings = settings;
            _settingsPath = settingsPath;

            _doubleClick = new ComboBox
            {
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = new[] { lang.Get("TEXT_SETTINGS_DOUBLE_CLICK_OPEN"), lang.Get("TEXT_SETTINGS_DOUBLE_CLICK_EXTRACT") },
            };

            _escCancel = new CheckBox
            {
                Content = lang.Get("TEXT_SETTINGS_ESC_CANCEL"),
                Margin = new Thickness(0, 20, 0, 0),
            };

            var doubleClickRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, 20, 0, 0),
            };
            doubleClickRow.Children.Add(new TextBlock
            {
                Text = lang.Get("TEXT_SETTINGS_DOUBLE_CLICK_LABEL"),
                Width = 170,
                VerticalAlignment = VerticalAlignment.Center,
            });
            doubleClickRow.Children.Add(_doubleClick);

            var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
            panel.Children.Add(SectionTitle(lang.Get("TEXT_SETTINGS_GENERAL_TITLE")));
            panel.Children.Add(doubleClickRow);
            panel.Children.Add(new TextBlock
            {
                Text = lang.Get("TEXT_SETTINGS_DOUBLE_CLICK_HINT"),
                Foreground = HintText,
                Margin = new Thickness(182, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(_escCancel);
            Root = panel;
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            _doubleClick.SelectedIndex = SettingKeys.ParseDoubleClick(_settings.GetString(SettingKeys.DoubleClickAction))
                == ArchiveDoubleClick.ExtractDirectly ? 1 : 0;
            _escCancel.IsChecked = _settings.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true);
        }

        public void Commit()
        {
            var mode = _doubleClick.SelectedIndex == 1
                ? ArchiveDoubleClick.ExtractDirectly
                : ArchiveDoubleClick.OpenInAndzip;

            _settings.SetString(SettingKeys.DoubleClickAction, mode.ToStorage());
            _settings.SetBool(SettingKeys.AllowEscCancelProgress, _escCancel.IsChecked == true);
            _settings.Save(_settingsPath);

            // 这里刻意不碰注册表：双击行为是启动时读设置决定的，不是由哪个动词被调用决定的。
            // 早先把默认动词改成 extract 的做法会留下「动词名写了、命令键不存在」的半截状态，
            // Windows 找不到动词就退回 open，用户看到的就是“设了没反应”。
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
            var title = SectionTitle(lang.Get("TEXT_SETTINGS_FILE_ASSOCIATION"));
            Grid.SetColumn(title, 0);
            titleRow.Children.Add(title);

            var defaultAppsLink = new Button
            {
                Content = lang.Get("TEXT_SETTINGS_WINDOWS_DEFAULT_APPS"),
                Foreground = LinkBlue,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(6, 0, 0, 0),
            };
            defaultAppsLink.Click += (_, _) => OpenWindowsDefaultAppSettings(null);
            Grid.SetColumn(defaultAppsLink, 1);
            titleRow.Children.Add(defaultAppsLink);

            var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
            list.Children.Add(new TextBlock
            {
                Text = lang.Get("TEXT_SETTINGS_ASSOC_HINT"),
                Foreground = HintText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            _shadowHint = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.Parse("#C42B1C")),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false,
            };
            _setDefaultButton = new Button
            {
                Content = lang.Get("TEXT_BTN_SET_DEFAULT"),
                MinWidth = 120,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false,
            };
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
                list.Children.Add(GroupHeader(lang.Get(group.TextKey)));
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

            var selectAll = new Button { Content = lang.Get("TEXT_BTN_SELECT_ALL"), MinWidth = 150, Margin = new Thickness(0, 0, 0, 8) };
            selectAll.Click += (_, _) => SetAll(true);
            var deselectAll = new Button { Content = lang.Get("TEXT_BTN_DESELECT_ALL"), MinWidth = 150, Margin = new Thickness(0, 0, 0, 8) };
            deselectAll.Click += (_, _) => SetAll(false);

            _applyButton = new Button
            {
                Content = lang.Get("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
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
                Message(_lang.Get("TEXT_TITLE_SETTINGS"), _lang.Get("TEXT_SETTINGS_ASSOC_UNSUPPORTED"));
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                Message(_lang.Get("TEXT_TITLE_ERROR"), _lang.Get("TEXT_SETTINGS_ASSOC_APPLY_FAILED") + "process path unavailable");
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

                _settings.SetStringList(AssociationSettingKey, desired);
                _settings.SetString(
                    AssociationOriginals.SettingKey,
                    AssociationOriginals.Encode(_originals.Select(pair => new AssociationOriginal(pair.Key, pair.Value))));
                _settings.Save(_settingsPath);
            }
            catch (Exception ex)
            {
                Message(_lang.Get("TEXT_TITLE_ERROR"), _lang.Get("TEXT_SETTINGS_ASSOC_APPLY_FAILED") + ex.Message);
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
            BorderBrush = new SolidColorBrush(Color.Parse("#C8DCF0")),
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
        private readonly CheckBox _forFiles;
        private readonly CheckBox _forFolders;
        private readonly CheckBox _forExtract;
        private readonly Button _applyButton;

        private (bool Files, bool Folders, bool Extract) _live;

        public ContextMenuPage(SettingsWindow owner, LangCatalog lang, AppSettings settings, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_CONTEXT_MENU";
            _owner = owner;
            _lang = lang;
            _settings = settings;
            _settingsPath = settingsPath;

            _forFiles = Check(lang.Get("TEXT_SETTINGS_CTX_COMPRESS_FILES"), UpdateApplyState);
            _forFolders = Check(lang.Get("TEXT_SETTINGS_CTX_COMPRESS_FOLDERS"), UpdateApplyState);
            _forExtract = Check(lang.Get("TEXT_SETTINGS_CTX_EXTRACT"), UpdateApplyState);

            _applyButton = new Button
            {
                Content = lang.Get("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, 22, 0, 0),
            };
            _applyButton.Click += (_, _) => Apply();

            var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
            panel.Children.Add(SectionTitle(lang.Get("TEXT_SETTINGS_CONTEXT_MENU_TITLE")));
            panel.Children.Add(new TextBlock
            {
                Text = lang.Get("TEXT_SETTINGS_CTX_NOTE"),
                Foreground = HintText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 16),
            });
            panel.Children.Add(_forFiles);
            panel.Children.Add(_forFolders);
            panel.Children.Add(_forExtract);
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_CTX_AUTO_EXTRACT_HINT")));
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_CTX_EXTRACT_HINT")));
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
                Message(_lang.Get("TEXT_SETTINGS_CTX_UNSUPPORTED"));
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                Message(_lang.Get("TEXT_SETTINGS_ASSOC_APPLY_FAILED") + "process path unavailable");
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
                    _settings.SetBool(SettingKeys.ContextMenuExtractVerbs, desired.Extract);
                    _settings.Save(_settingsPath);

                    // 解压动词住在每个 Andzip.* ProgID 里，所以"改开关"就是把自己命名空间下的
                    // 动词全部重写一遍 —— 关掉的那几项是真删掉，不是留着键改文字。
                    var associations = new FileAssociationService();
                    associations.RepairCommands(executable, ShellVerbLabels.Extraction(_lang, desired.Extract));
                    associations.NotifyChanged();
                }
            }
            catch (Exception ex)
            {
                Message(_lang.Get("TEXT_SETTINGS_CTX_FAILED") + ex.Message);
                return;
            }

            Reload();
        }

        private void Message(string text) => _ = new MessageWindow(_lang, _lang.Get("TEXT_TITLE_SETTINGS"), text).ShowDialog(_owner);
    }

    /// <summary>
    /// 解压页：三个开关，都只影响"东西铺在哪儿 / 窗口留不留"。
    /// <para>
    /// 没有放参考软件上的「一次性解压 TGZ/TBZ」「自动检测 Macintosh 代码页」「并行解压」等项 ——
    /// 前者引擎本来就会把嵌套归档展平（不是可关的行为），中者需要引擎侧的代码页猜测，
    /// 后者与压缩共用一个进程级线程数、放在这里会以为有两个旋钮。做不到的不摆假控件。
    /// </para>
    /// </summary>
    private sealed class ExtractionPage : ISettingsPage
    {
        private readonly string _settingsPath;
        private readonly CheckBox _singleFileToFolder;
        private readonly CheckBox _openFolderAfter;
        private readonly CheckBox _keepProgress;
        private readonly Button _applyButton;

        private ExtractionDefaults _live = ExtractionDefaults.Fallback;

        public ExtractionPage(LangCatalog lang, string settingsPath)
        {
            NavKey = "TEXT_SETTINGS_EXTRACT";
            _settingsPath = settingsPath;

            _singleFileToFolder = Check(lang.Get("TEXT_SETTINGS_EXTRACT_SINGLE_FILE_TO_FOLDER"), UpdateApplyState);
            _openFolderAfter = Check(lang.Get("TEXT_SETTINGS_EXTRACT_OPEN_FOLDER"), UpdateApplyState);
            _keepProgress = Check(lang.Get("TEXT_SETTINGS_EXTRACT_KEEP_PROGRESS"), UpdateApplyState);

            _applyButton = new Button
            {
                Content = lang.Get("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _applyButton.Click += (_, _) => Apply();

            var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
            panel.Children.Add(SectionTitle(lang.Get("TEXT_SETTINGS_EXTRACT_TITLE")));
            panel.Children.Add(_singleFileToFolder);
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_EXTRACT_SINGLE_FILE_HINT")));
            panel.Children.Add(_openFolderAfter);
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_EXTRACT_OPEN_FOLDER_HINT")));
            panel.Children.Add(_keepProgress);
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_EXTRACT_KEEP_PROGRESS_HINT")));
            panel.Children.Add(_applyButton);

            Root = panel;
        }

        public string NavKey { get; }

        public Control Root { get; }

        public void Reload()
        {
            _live = ExtractionDefaults.Load(AppSettings.Load(_settingsPath));
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
            _keepProgress.IsChecked == true);

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
        private readonly CheckBox _noRootFolder;
        private readonly CheckBox _excludeThumbs;
        private readonly TextBox _excludePatterns;
        private readonly CheckBox _testAfter;
        private readonly CheckBox _keepProgress;
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
                    .Select(o => new DisplayItem(lang.Get(o.TextKey), o))
                    .ToList(),
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            _noRootFolder = Check(lang.Get("TEXT_SETTINGS_COMPRESS_NO_ROOT"), UpdateApplyState);
            _excludeThumbs = Check(lang.Get("TEXT_SETTINGS_COMPRESS_EXCLUDE_THUMBS"), UpdateApplyState);
            _testAfter = Check(lang.Get("TEXT_SETTINGS_COMPRESS_TEST_AFTER"), UpdateApplyState);
            _keepProgress = Check(lang.Get("TEXT_SETTINGS_COMPRESS_KEEP_PROGRESS"), UpdateApplyState);

            _excludePatterns = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            _excludePatterns.TextChanged += (_, _) => UpdateApplyState();

            _outputLast = new RadioButton { Content = lang.Get("TEXT_SETTINGS_COMPRESS_OUTPUT_LAST"), IsChecked = true };
            _outputFixed = new RadioButton { Content = lang.Get("TEXT_SETTINGS_COMPRESS_OUTPUT_FIXED") };
            _outputLast.IsCheckedChanged += (_, _) => { RefreshOutputFolderEnabled(); UpdateApplyState(); };
            _outputFixed.IsCheckedChanged += (_, _) => { RefreshOutputFolderEnabled(); UpdateApplyState(); };

            _outputFolder = new TextBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            _outputFolder.TextChanged += (_, _) => UpdateApplyState();

            var pickFolder = new Button { Content = lang.Get("TEXT_BTN_BROWSE"), MinWidth = 110 };
            pickFolder.Click += async (_, _) => await PickFolderAsync();

            _applyButton = new Button
            {
                Content = lang.Get("TEXT_BTN_APPLY"),
                MinWidth = 150,
                IsEnabled = false,
                Margin = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _applyButton.Click += (_, _) => Apply();

            var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 20) };
            panel.Children.Add(SectionTitle(lang.Get("TEXT_SETTINGS_COMPRESS_TITLE")));
            panel.Children.Add(LabeledRow(lang.Get("TEXT_SETTINGS_COMPRESS_DEFAULT_LEVEL"), _level));
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_COMPRESS_LEVEL_HINT")));
            panel.Children.Add(_noRootFolder);
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_COMPRESS_NO_ROOT_HINT")));
            panel.Children.Add(_excludeThumbs);
            panel.Children.Add(LabeledRow(lang.Get("TEXT_SETTINGS_COMPRESS_EXCLUDE_TYPES"), _excludePatterns));
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_COMPRESS_EXCLUDE_HINT")));
            panel.Children.Add(_testAfter);
            panel.Children.Add(_keepProgress);
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_COMPRESS_KEEP_PROGRESS_HINT")));

            panel.Children.Add(new TextBlock
            {
                Text = lang.Get("TEXT_SETTINGS_COMPRESS_OUTPUT_DIR"),
                Margin = new Thickness(0, 20, 0, 4),
            });
            panel.Children.Add(_outputLast);
            panel.Children.Add(_outputFixed);
            panel.Children.Add(LabeledRow(string.Empty, _outputFolder, pickFolder));
            panel.Children.Add(Hint(lang.Get("TEXT_SETTINGS_COMPRESS_OUTPUT_FIXED_HINT")));
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
            FixedOutputFolder: string.IsNullOrWhiteSpace(_outputFolder.Text) ? null : _outputFolder.Text.Trim());

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
                Title = _lang.Get("TEXT_SETTINGS_COMPRESS_OUTPUT_FIXED"),
                AllowMultiple = false,
            });

            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                _outputFolder.Text = path;
            }
        }

        /// <summary>左标签右控件的一行；标签给空串就是只占缩进的单独控件行。</summary>
        private static Control LabeledRow(string label, Control control, Control? trailing = null)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, 14, 0, 0),
            };
            if (label.Length > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = label,
                    Width = 170,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else
            {
                row.Children.Add(new TextBlock { Width = 170 });
            }

            row.Children.Add(control);
            if (trailing is not null)
            {
                row.Children.Add(trailing);
            }

            return row;
        }
    }
}
