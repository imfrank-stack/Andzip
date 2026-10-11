using System.Diagnostics;
using Andzip.App.Services;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 设置窗口（选项 → 设置，F5）：左侧蓝色导航 + 右侧页面 + 底部「重置 / 确定」。
/// <para>
/// 目前只有「文件关联」是实装的页面，其余按参考软件的导航顺序列出并显示占位文案 ——
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

    private readonly LangCatalog _lang;
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly ContentControl _pageHost = new();
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.Ordinal);
    private readonly List<Func<Control>> _pages;
    private string _currentPageKey;

    public SettingsWindow(LangCatalog lang, AppSettings settings, string settingsPath)
    {
        _lang = lang;
        _settings = settings;
        _settingsPath = settingsPath;

        Title = lang.Get("TEXT_TITLE_SETTINGS");
        Width = 1000;
        Height = 660;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#F0F0F0"));
        FontSize = 14;

        // 导航顺序与参考软件一致；索引即页面工厂的下标。
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
            () => PlaceholderPage(),
            () => FileAssociationPage(),
            () => PlaceholderPage(),
            () => PlaceholderPage(),
            () => PlaceholderPage(),
            () => PlaceholderPage(),
            () => PlaceholderPage(),
            () => PlaceholderPage(),
            () => PlaceholderPage(),
        ];

        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(0, 14, 0, 0) };
        for (var i = 0; i < navKeys.Length; i++)
        {
            var key = navKeys[i];
            var index = i;
            var button = NavButton(lang.Get(key), () => ShowPage(key, index));
            _navButtons[key] = button;
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

        var resetButton = new Button { Content = lang.Get("TEXT_BTN_RESET"), MinWidth = 120, IsCancel = true };
        resetButton.Click += (_, _) => ResetCurrentPage();
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

        _currentPageKey = navKeys[1];
        ShowPage(navKeys[1], 1);
    }

    private void ShowPage(string key, int index)
    {
        _currentPageKey = key;
        foreach (var (navKey, button) in _navButtons)
        {
            button.Background = navKey == key ? NavBlueSelected : NavBlue;
        }

        _pageHost.Content = _pages[index]();
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

    private Control PlaceholderPage()
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 20),
            Spacing = 14,
        };
        panel.Children.Add(SectionTitle(_lang.Get(_currentPageKey)));
        panel.Children.Add(new TextBlock
        {
            Text = _lang.Get("TEXT_SETTINGS_NOT_IMPLEMENTED"),
            Foreground = new SolidColorBrush(Color.Parse("#6B6B6B")),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 17,
        FontWeight = FontWeight.Bold,
        Foreground = PageText,
    };

    // ---------- 文件关联页 ----------

    private FileAssociationTab? _associationPage;

    private Control FileAssociationPage()
    {
        _associationPage = new FileAssociationTab(this, _lang, _settings, _settingsPath);
        return _associationPage.Root;
    }

    private void ResetCurrentPage() => _associationPage?.Reset();

    private void AcceptAndClose()
    {
        // 「确定」= 有未应用的改动就顺手应用掉再关窗；右上角的 X 才是放弃改动。
        _associationPage?.ApplyIfDirty();
        Close();
    }

    /// <summary>
    /// 文件关联页：分组复选框 + 全选 / 取消全选 + 立即应用。
    /// <para>
    /// 候选项来自 <see cref="FileAssociationCatalog.Supported"/>，也就是引擎实际报上来的可读扩展名 ——
    /// 勾不上我们读不了的格式。应用只往 HKCU 的 OpenWithProgids 里挂，不改默认程序。
    /// </para>
    /// </summary>
    private sealed class FileAssociationTab
    {
        private readonly SettingsWindow _owner;
        private readonly LangCatalog _lang;
        private readonly AppSettings _settings;
        private readonly string _settingsPath;
        private readonly IReadOnlyList<AssociationGroup> _groups;
        private readonly Dictionary<string, CheckBox> _boxes = new(StringComparer.Ordinal);
        private readonly Button _applyButton;
        private HashSet<string> _saved;

        public FileAssociationTab(SettingsWindow owner, LangCatalog lang, AppSettings settings, string settingsPath)
        {
            _owner = owner;
            _lang = lang;
            _settings = settings;
            _settingsPath = settingsPath;
            _groups = FileAssociationCatalog.Supported(ArchiveFormatOption.OpenableExtensions);
            _saved = ReadSavedSelection(settings, _groups);

            var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var title = SectionTitle(lang.Get("TEXT_SETTINGS_FILE_ASSOCIATION"));
            title.Margin = new Thickness(0, 0, 0, 0);
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
            defaultAppsLink.Click += (_, _) => OpenWindowsDefaultAppSettings();
            Grid.SetColumn(defaultAppsLink, 1);
            titleRow.Children.Add(defaultAppsLink);

            var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
            list.Children.Add(new TextBlock
            {
                Text = lang.Get("TEXT_SETTINGS_ASSOC_HINT"),
                Foreground = new SolidColorBrush(Color.Parse("#6B6B6B")),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

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
                        IsChecked = _saved.Contains(association.Id),
                    };
                    box.PropertyChanged += (_, e) =>
                    {
                        if (e.Property == ToggleButton.IsCheckedProperty)
                        {
                            UpdateApplyState();
                        }
                    };
                    ToolTip.SetTip(box, string.Join(' ', association.Extensions.Select(x => "." + x)));
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
            UpdateApplyState();
        }

        public Control Root { get; }

        /// <summary>「重置」= 恢复出厂勾选（不是放弃未保存改动），与参考软件一致。</summary>
        public void Reset()
        {
            var defaults = FileAssociationCatalog.DefaultIds;
            foreach (var (id, box) in _boxes)
            {
                box.IsChecked = defaults.Contains(id);
            }

            UpdateApplyState();
        }

        public void ApplyIfDirty()
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

        private void UpdateApplyState() => _applyButton.IsEnabled = !CurrentSelection().SetEquals(_saved);

        private void Apply()
        {
            var desired = CurrentSelection();
            if (desired.SetEquals(_saved))
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                Message(_owner, _lang.Get("TEXT_TITLE_SETTINGS"), _lang.Get("TEXT_SETTINGS_ASSOC_UNSUPPORTED"));
                return;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                Message(_owner, _lang.Get("TEXT_TITLE_ERROR"), _lang.Get("TEXT_SETTINGS_ASSOC_APPLY_FAILED") + "process path unavailable");
                return;
            }

            try
            {
                var service = new FileAssociationService();
                foreach (var id in desired.Except(_saved))
                {
                    service.Register(FileAssociationCatalog.Find(id)!, executable);
                }

                foreach (var id in _saved.Except(desired))
                {
                    service.Unregister(FileAssociationCatalog.Find(id)!);
                }

                service.NotifyChanged();

                _settings.SetStringList(AssociationSettingKey, desired);
                _settings.Save(_settingsPath);
                _saved = desired;
                UpdateApplyState();
            }
            catch (Exception ex)
            {
                Message(_owner, _lang.Get("TEXT_TITLE_ERROR"), _lang.Get("TEXT_SETTINGS_ASSOC_APPLY_FAILED") + ex.Message);
            }
        }

        private void Message(Window owner, string title, string text)
            => _ = new MessageWindow(_lang, title, text).ShowDialog(owner);

        /// <summary>
        /// 已保存的勾选。没有这个键时取默认集合，而不是「全不勾」——
        /// 第一次打开设置就看到一屏空框，会让人以为功能坏了。
        /// </summary>
        private static HashSet<string> ReadSavedSelection(AppSettings settings, IReadOnlyList<AssociationGroup> groups)
        {
            var offered = groups.SelectMany(g => g.Items).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            var stored = settings.Has(AssociationSettingKey)
                ? settings.GetStringList(AssociationSettingKey)
                : FileAssociationCatalog.DefaultIds;

            // 目录里已经不再提供的 Id（引擎读不了了）顺手丢掉。
            return stored.Where(offered.Contains).ToHashSet(StringComparer.Ordinal);
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

        private static void OpenWindowsDefaultAppSettings()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                // ms-settings: URI 只在 Windows 上有意义；失败也不该打扰设置流程。
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:defaultapps",
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
            }
        }
    }
}
