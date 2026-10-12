using System.Collections.ObjectModel;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Andzip.App.Views;

/// <summary>下拉框显示项（显示本地化文案，值为领域对象）。</summary>
internal sealed record DisplayItem(string Text, object Value)
{
    public override string ToString() => Text;
}

/// <summary>
/// 「新建压缩文件」对话框（FR-2 / 参考截图 05–07）。
/// 已实现：添加文件 / 文件夹、列表、文件名 + 浏览（跟随所选格式）、格式选择、
/// 设置密码（FR-2.6）、更多选项（分卷 / 压缩级别 / 高级设置面板 / 三个复选项）、
/// 开始 / 取消，以及格式切换时自动同步输出扩展名。
/// 高级设置面板（FR-2.5）：压缩方法、加密算法、CPU 线程数（占位：待多线程引擎）、
/// 存储符号链接（占位）、文件说明 + 注释输入框。
/// </summary>
public sealed class NewArchiveWindow : Window
{
    private readonly LangCatalog _lang;

    /// <summary>「设置 → 压缩」带来的默认值；决定打开时的初始状态，不锁住控件。</summary>
    private readonly CompressionDefaults _defaults;

    private readonly string? _lastOutputFolder;

    private readonly Action<string>? _rememberOutputFolder;
    private readonly ObservableCollection<ArchiveInputItem> _inputs = [];
    private readonly ListBox _list;
    private readonly Button _deleteButton;
    private readonly TextBox _fileNameBox;
    private readonly ComboBox _formatCombo;
    private readonly ComboBox _levelCombo;
    private readonly ComboBox _volumeCombo;
    private readonly TextBlock _hint;
    private readonly Button _startButton;
    private readonly Button _passwordButton;
    private readonly CheckBox _testAfterCheck;
    private readonly CheckBox _deleteSourceCheck;
    private readonly CheckBox _separateCheck;
    private readonly Border _advancedPanel;
    private readonly Button _advancedButton;
    private readonly ComboBox _methodCombo;
    private readonly ComboBox _encryptionCombo;
    private readonly ComboBox _threadsCombo;
    private readonly CheckBox _symlinkCheck;
    private readonly CheckBox _fileCommentCheck;
    private readonly TextBox _commentBox;
    private ArchiveFormatOption _currentFormat = ArchiveFormatOption.Default;
    private string? _password;
    private int? _customVolumeMb;
    private int _lastVolumeIndex;
    private bool _suppressVolumeChange;

    /// <summary>
    /// 「新建压缩文件」对话框（FR-2）。
    /// </summary>
    /// <param name="defaults">来自「设置 → 压缩」的默认值；null 表示用不带设置的那一套。
    /// 它只决定<strong>打开时长什么样</strong>，控件本身仍可逐次改。</param>
    /// <param name="seedPath">右键「压缩到…」带进来的文件或文件夹：预先填进输入列表，
    /// 并把「文件名」预填成同名的兄弟位置。null 表示从主菜单新建，一切从空开始。</param>
    /// <param name="lastOutputFolder">上次成功新建时用的目录，仅在输出目录策略是「沿用上次」时生效。</param>
    /// <param name="rememberOutputFolder">压成之后把输出目录记下来；null 表示不记账。
    /// 用回调而不是把 <c>AppSettings</c> 传进来：对话框不该知道设置存在哪里、什么时候落盘。</param>
    public NewArchiveWindow(
        LangCatalog lang,
        CompressionDefaults? defaults = null,
        string? seedPath = null,
        string? lastOutputFolder = null,
        Action<string>? rememberOutputFolder = null)
    {
        _lang = lang;
        _defaults = defaults ?? CompressionDefaults.Fallback;
        _lastOutputFolder = lastOutputFolder;
        _rememberOutputFolder = rememberOutputFolder;

        Title = lang.Label("TEXT_TITLE_NEW_ARCHIVE");
        Width = 972;
        Height = 710;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontSize = 14;

        // ---- 列表与按钮 ----
        _deleteButton = new Button { Content = lang.Label("TEXT_BTN_DELETE"), IsEnabled = false, MinWidth = 96 };
        _deleteButton.Click += (_, _) => RemoveSelected();

        var addButton = new Button { Content = lang.Label("TEXT_BTN_ADD"), MinWidth = 96 };
        var addFilesItem = new MenuItem { Header = lang.Label("TEXT_MENU_ADD_FILES") };
        addFilesItem.Click += async (_, _) => await AddFilesAsync();
        var addFolderItem = new MenuItem { Header = lang.Label("TEXT_MENU_ADD_FOLDER") };
        addFolderItem.Click += async (_, _) => await AddFolderAsync();
        var addFlyout = new MenuFlyout();
        addFlyout.Items.Add(addFilesItem);
        addFlyout.Items.Add(addFolderItem);
        addButton.Flyout = addFlyout;

        _list = new ListBox
        {
            ItemsSource = _inputs,
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<ArchiveInputItem>((item, _) => BuildRow(item!), supportsRecycling: true),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#C8C8C8")),
        };
        _list.SelectionChanged += (_, _) => _deleteButton.IsEnabled = _list.SelectedItems?.Count > 0;

        // ---- 压缩文件设置 ----
        _fileNameBox = new TextBox { Text = DefaultOutputPath(), VerticalAlignment = VerticalAlignment.Center };
        _fileNameBox.TextChanged += (_, _) => RefreshState();

        var browseButton = new Button { Content = lang.Label("TEXT_BTN_BROWSE"), MinWidth = 110 };
        browseButton.Click += async (_, _) => await BrowseAsync();

        _formatCombo = new ComboBox
        {
            ItemsSource = ArchiveFormatOption.All.Select(f => new DisplayItem(f.Id, f)).ToList(),
            SelectedIndex = 0,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _formatCombo.SelectionChanged += (_, _) => OnFormatChanged();

        _passwordButton = new Button { Content = lang.Label("TEXT_BTN_SET_PASSWORD"), MinWidth = 150 };
        _passwordButton.Click += async (_, _) => await SetPasswordAsync();

        _levelCombo = new ComboBox
        {
            ItemsSource = CompressionLevelOption.All
                .Select(o => new DisplayItem(lang.Label(o.TextKey), o))
                .ToList(),
            // 按位置选，不按 Id 选：Id 是存进设置文件的稳定标识，将来插入新档位时两者就会错位。
            SelectedIndex = Math.Max(0, CompressionLevelOption.PositionOf(_defaults.Level)),
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _volumeCombo = new ComboBox
        {
            ItemsSource = VolumeOption.All
                .Select(o => new DisplayItem(lang.Label(o.TextKey), o))
                .ToList(),
            SelectedIndex = 0,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _volumeCombo.SelectionChanged += async (_, _) => await OnVolumeChangedAsync();

        _hint = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#C42B1C")), TextWrapping = TextWrapping.Wrap, IsVisible = false };

        _startButton = new Button { Content = lang.Label("TEXT_BTN_START"), MinWidth = 96, IsDefault = true };
        _startButton.Click += async (_, _) => await StartAsync();

        var cancelButton = new Button { Content = lang.Label("TEXT_BTN_CANCEL"), MinWidth = 96, IsCancel = true };
        cancelButton.Click += (_, _) => Close();

        _testAfterCheck = new CheckBox
        {
            Content = lang.Label("TEXT_CHECK_TEST_AFTER_COMPRESSION"),
            IsChecked = _defaults.TestAfterCreation,
        };
        _deleteSourceCheck = new CheckBox { Content = lang.Label("TEXT_CHECK_DELETE_SOURCE") };
        _separateCheck = new CheckBox { Content = lang.Label("TEXT_CHECK_SEPARATE_ARCHIVES") };

        // ---- 高级设置面板（FR-2.5，右侧展开）----
        // 方法列表随格式变化（引擎按格式决定可写方法），所以初始化为默认格式的那一组，
        // 并在 OnFormatChanged 里重建。
        _methodCombo = new ComboBox
        {
            ItemsSource = MethodItems(ArchiveFormatOption.Default),
            SelectedIndex = 0,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _encryptionCombo = new ComboBox
        {
            ItemsSource = EncryptionMethodOption.All
                .Select(o => new DisplayItem(lang.Label(o.TextKey), o))
                .ToList(),
            SelectedIndex = EncryptionMethodOption.Default.Id,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // 线程数现在真的生效：native 引擎的 LZMA2/BZip2/XZ/Zstd 都是多线程编码器。
        _threadsCombo = new ComboBox
        {
            ItemsSource = new List<DisplayItem>
            {
                new(lang.Label("TEXT_THREADS_AUTO"), 0u),
                new("1", 1u),
                new("2", 2u),
                new("4", 4u),
                new("8", 8u),
                new("16", 16u),
            },
            SelectedIndex = 0,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _symlinkCheck = new CheckBox { Content = lang.Label("TEXT_CHECK_STORE_SYMLINKS"), IsEnabled = false };
        _fileCommentCheck = new CheckBox { Content = lang.Label("TEXT_CHECK_FILE_COMMENT") };
        _commentBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            IsEnabled = false,
        };
        _fileCommentCheck.IsCheckedChanged += (_, _) =>
            _commentBox.IsEnabled = _fileCommentCheck.IsChecked == true;

        _advancedButton = new Button
        {
            Content = lang.Label("TEXT_BTN_ADVANCED_SETTINGS"),
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        _advancedPanel = new Border
        {
            IsVisible = false,
            Width = 320,
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#E0E0E0")),
            Margin = new Thickness(8, 16, 16, 16),
            Padding = new Thickness(14, 0, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 10,
                Children =
                {
                    SectionTitle(lang.Label("TEXT_ADVANCED_TITLE_ZIP")),
                    BuildFieldRow(lang.Label("TEXT_LABEL_COMPRESSION_METHOD"), _methodCombo),
                    BuildFieldRow(lang.Label("TEXT_LABEL_ENCRYPTION_METHOD"), _encryptionCombo),
                    BuildFieldRow(lang.Label("TEXT_LABEL_CPU_THREADS"), _threadsCombo),
                    _symlinkCheck,
                    _fileCommentCheck,
                    _commentBox,
                },
            },
        };

        var moreOptionsCheck = new CheckBox { Content = lang.Label("TEXT_CHECK_MORE_OPTIONS") };
        var moreOptionsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 10,
            IsVisible = false,
            Children =
            {
                BuildFieldRow(lang.Label("TEXT_LABEL_VOLUME"), _volumeCombo),
                BuildFieldRow(lang.Label("TEXT_LABEL_COMPRESSION_LEVEL"), _levelCombo),
                _advancedButton,
                BuildOptionChecks(),
            },
        };

        _advancedButton.Click += (_, _) => SetAdvancedPanelVisible(!_advancedPanel.IsVisible);
        moreOptionsCheck.IsCheckedChanged += (_, _) => moreOptionsPanel.IsVisible = moreOptionsCheck.IsChecked == true;

        // ---- 布局 ----
        var grid = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };

        AddRow(grid, 0, SectionTitle(lang.Label("TEXT_SECTION_ADD_FILES")), 3);
        AddRow(grid, 1, BuildColumnHeader(), 3);
        AddRow(grid, 2, _list, 3);
        AddRow(grid, 3, RightAligned(addButton, _deleteButton), 3);
        AddRow(grid, 4, new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.Parse("#E0E0E0")), Margin = new Thickness(0, 10, 0, 10) }, 3);
        AddRow(grid, 5, SectionTitle(lang.Label("TEXT_SECTION_ARCHIVE_SETTINGS")), 3);
        AddRow(grid, 6, BuildFieldRow(lang.Label("TEXT_LABEL_FILE_NAME"), _fileNameBox, browseButton));
        AddRow(grid, 7, BuildFieldRow(lang.Label("TEXT_LABEL_FORMAT"), _formatCombo, _passwordButton));
        AddRow(grid, 8, moreOptionsPanel, 3);
        AddRow(grid, 9, _hint, 3);
        AddRow(grid, 10, BuildBottomRow(moreOptionsCheck, cancelButton), 3);

        // 右侧「高级设置」面板与主区域并排（展开时窗口加宽，FR-2.5）
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(grid, 0);
        Grid.SetColumn(_advancedPanel, 1);
        root.Children.Add(grid);
        root.Children.Add(_advancedPanel);

        Content = root;

        if (seedPath is not null)
        {
            SeedFrom(seedPath);
        }

        RefreshState();
    }

    /// <summary>
    /// 右键「压缩到…」的预填：把选中的路径放进输入列表，输出名取同名兄弟位置。
    /// <para>
    /// 走 <see cref="ShellTasks.ArchivePathFor"/> 而不是在这里再拼一遍名字 —— 文件夹名里的点
    /// 不能当扩展名剥掉（<c>my.app</c> 要得到 <c>my.app.zip</c>），这条规则已经测过了，不该有两份。
    /// </para>
    /// <para>刻意<strong>不</strong>做重名避让：这里是给用户看的建议名，他自己会改；
    /// 真到落盘时 <c>ArchiveCreationSettings.Overwrite</c> 那条路径负责不悄悄覆盖。</para>
    /// </summary>
    private void SeedFrom(string seedPath)
    {
        AddInput(seedPath);
        _fileNameBox.Text = ShellTasks.ArchivePathFor(
            seedPath, SelectedFormat().Extension, Directory.Exists(seedPath));
    }

    /// <summary>展开 / 收起右侧高级设置面板（按钮文案同步为「« 高级设置」，窗口宽度相应变化）。</summary>
    private void SetAdvancedPanelVisible(bool visible)
    {
        _advancedPanel.IsVisible = visible;
        _advancedButton.Content = _lang.Label(visible ? "TEXT_BTN_ADVANCED_SETTINGS_COLLAPSE" : "TEXT_BTN_ADVANCED_SETTINGS");
        Width = visible ? 1292 : 972;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 6, 0, 8),
    };

    private Control BuildColumnHeader()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,120,280") };
        var name = new TextBlock { Text = _lang.Label("TEXT_COLUMN_NAME"), HorizontalAlignment = HorizontalAlignment.Left };
        var size = new TextBlock { Text = _lang.Label("TEXT_COLUMN_SIZE"), HorizontalAlignment = HorizontalAlignment.Left };
        var path = new TextBlock { Text = _lang.Label("TEXT_COLUMN_FILE_PATH"), HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(size, 1);
        Grid.SetColumn(path, 2);
        header.Children.Add(name);
        header.Children.Add(size);
        header.Children.Add(path);
        header.Margin = new Thickness(6, 0, 12, 4);
        return header;
    }

    private static Control BuildRow(ArchiveInputItem item)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,120,280") };
        var name = new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        var size = new TextBlock { Text = FormatSize(item.Size) };
        var path = new TextBlock { Text = item.FullPath, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Color.Parse("#6B6B6B")) };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(size, 1);
        Grid.SetColumn(path, 2);
        row.Children.Add(name);
        row.Children.Add(size);
        row.Children.Add(path);
        row.Margin = new Thickness(6, 2, 12, 2);
        return row;
    }

    private static Control BuildFieldRow(string label, Control field, Control? trailing = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 3, 0, 3) };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(field, 1);
        row.Children.Add(text);
        row.Children.Add(field);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);
        }

        return row;
    }

    private Control BuildOptionChecks()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 20,
            Children = { _testAfterCheck, _deleteSourceCheck, _separateCheck },
        };
        return panel;
    }

    private Control BuildBottomRow(CheckBox moreOptions, Button cancelButton)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetColumn(moreOptions, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _startButton, cancelButton } };
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(moreOptions);
        grid.Children.Add(buttons);
        return grid;
    }

    private static Control RightAligned(params Control[] children)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private static void AddRow(Grid grid, int row, Control control, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        if (columnSpan > 1)
        {
            Grid.SetColumnSpan(control, columnSpan);
        }

        grid.Children.Add(control);
    }

    /// <summary>
    /// 「文件名」框打开时的默认落点，按「设置 → 压缩 → 输出目录」决定。
    /// <para>
    /// 「固定目录」里填了不存在的路径时<strong>不</strong>当场创建：这个值只是填进文本框的建议，
    /// 用户可能马上改掉；真到压的时候 <c>ArchiveCreateValidator.Prepare</c> 会建目录，
    /// 那时失败才有意义（也会给出错误框而不是静默建一堆目录）。
    /// </para>
    /// </summary>
    private string DefaultOutputPath()
    {
        var directory = _defaults.OutputMode == SettingKeys.OutputFolderMode.Fixed
            ? FirstDirectory(_defaults.FixedOutputFolder, _lastOutputFolder)
            : FirstDirectory(_lastOutputFolder, _defaults.FixedOutputFolder);

        directory ??= Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return Path.Combine(directory, _lang.Label("TEXT_DEFAULT_ARCHIVE_NAME") + ".zip");
    }

    /// <summary>第一个"非空且真的存在"的目录；都没有就返回 null。</summary>
    private static string? FirstDirectory(params string?[] candidates)
        => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c));

    private static string FormatSize(long? bytes) => bytes switch
    {
        null => string.Empty,
        < 1024 => bytes.Value + " B",
        < 1024 * 1024 => (bytes.Value / 1024.0).ToString("0.#") + " KB",
        _ => (bytes.Value / (1024.0 * 1024)).ToString("0.#") + " MB",
    };

    private async Task AddFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _lang.Label("TEXT_MENU_ADD_FILES"),
            AllowMultiple = true,
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null)
            {
                AddInput(path);
            }
        }
    }

    private async Task AddFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _lang.Label("TEXT_MENU_ADD_FOLDER"),
            AllowMultiple = true,
        });

        foreach (var folder in folders)
        {
            var path = folder.TryGetLocalPath();
            if (path is not null)
            {
                AddInput(path);
            }
        }
    }

    private void AddInput(string path)
    {
        try
        {
            var item = ArchiveInputItem.FromPath(path);
            if (_inputs.Any(i => string.Equals(i.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _inputs.Add(item);
            RefreshState();
        }
        catch (Exception ex)
        {
            ShowHint(ex.Message);
        }
    }

    private void RemoveSelected()
    {
        var selected = _list.SelectedItems?.OfType<ArchiveInputItem>().ToList() ?? [];
        foreach (var item in selected)
        {
            _inputs.Remove(item);
        }

        RefreshState();
    }

    private async Task BrowseAsync()
    {
        var format = SelectedFormat();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _lang.Label("TEXT_LABEL_FILE_NAME"),
            SuggestedFileName = Path.GetFileName(_fileNameBox.Text ?? string.Empty),
            DefaultExtension = format.Extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(format.Id) { Patterns = ["*" + format.Extension] }],
        });

        var path = file?.TryGetLocalPath();
        if (path is not null)
        {
            _fileNameBox.Text = path;
        }
    }

    /// <summary>格式切换：同步输出扩展名、更新密码按钮可用状态与开始按钮状态。</summary>
    private void OnFormatChanged()
    {
        var format = SelectedFormat();
        SyncOutputExtension(_currentFormat, format);
        _currentFormat = format;
        RefreshMethodOptions(format);
        RefreshState();
    }

    /// <summary>方法下拉的显示文本：有语言键就翻译，否则直接显示引擎方法名（专有名词不翻译）。</summary>
    private List<DisplayItem> MethodItems(ArchiveFormatOption format)
        =>
        [
            .. format.Methods.Select(o => new DisplayItem(
                o.TextKey is { } key ? _lang.Label(key) : o.DisplayName, o)),
        ];

    /// <summary>换格式后重建方法列表；保持用户已选的方法（如果新格式也支持）。</summary>
    private void RefreshMethodOptions(ArchiveFormatOption format)
    {
        var previous = (_methodCombo.SelectedItem as DisplayItem)?.Value as CompressionMethodOption;
        var items = MethodItems(format);
        _methodCombo.ItemsSource = items;
        var keep = previous is null ? -1 : items.FindIndex(item => Equals(item.Value, previous));
        _methodCombo.SelectedIndex = keep >= 0 ? keep : 0;
    }

    /// <summary>输出文件名以旧格式扩展名结尾时，替换为新格式扩展名（对齐参考软件的建议文件名行为）。</summary>
    private void SyncOutputExtension(ArchiveFormatOption oldFormat, ArchiveFormatOption newFormat)
    {
        if (string.Equals(oldFormat.Extension, newFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var text = _fileNameBox.Text;
        if (string.IsNullOrWhiteSpace(text) || !text.EndsWith(oldFormat.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _fileNameBox.Text = text[..^oldFormat.Extension.Length] + newFormat.Extension;
    }

    private async Task SetPasswordAsync()
    {
        var dialog = new PasswordWindow(_lang, _password);
        if (await dialog.ShowDialog<bool>(this))
        {
            _password = dialog.Password;
        }
    }

    /// <summary>分卷下拉变化：选中「自定义大小」时弹对话框输入 MB 值；取消则回退到上一个选择。</summary>
    private async Task OnVolumeChangedAsync()
    {
        if (_suppressVolumeChange)
        {
            return;
        }

        if (_volumeCombo.SelectedItem is not DisplayItem { Value: VolumeOption option })
        {
            return;
        }

        if (!option.IsCustom)
        {
            _lastVolumeIndex = _volumeCombo.SelectedIndex;
            return;
        }

        var dialog = new VolumeSizeWindow(_lang, _customVolumeMb ?? 5);
        if (await dialog.ShowDialog<bool>(this))
        {
            _customVolumeMb = dialog.SizeMb;
            _lastVolumeIndex = _volumeCombo.SelectedIndex;
            UpdateCustomVolumeText(dialog.SizeMb);
        }
        else
        {
            _suppressVolumeChange = true;
            _volumeCombo.SelectedIndex = _lastVolumeIndex;
            _suppressVolumeChange = false;
        }
    }

    /// <summary>把「自定义大小」项显示为「自定义大小 (N MB)」（重建列表以刷新显示）。</summary>
    private void UpdateCustomVolumeText(int sizeMb)
    {
        var items = (List<DisplayItem>)_volumeCombo.ItemsSource!;
        var index = _volumeCombo.SelectedIndex;
        items[index] = items[index] with { Text = $"{_lang.Label("TEXT_VOLUME_CUSTOM")} ({sizeMb} MB)" };

        _suppressVolumeChange = true;
        _volumeCombo.ItemsSource = null;
        _volumeCombo.ItemsSource = items;
        _volumeCombo.SelectedIndex = index;
        _suppressVolumeChange = false;
    }

    /// <summary>当前选中的分卷大小（字节）；不分卷或格式不支持分卷时为 null。</summary>
    private long? SelectedVolumeSizeBytes()
    {
        if (!SelectedFormat().SupportsVolume)
        {
            return null;
        }

        return _volumeCombo.SelectedItem switch
        {
            DisplayItem { Value: VolumeOption { IsCustom: true } } =>
                _customVolumeMb is { } sizeMb ? sizeMb * 1024L * 1024L : null,
            DisplayItem { Value: VolumeOption option } => option.SizeBytes,
            _ => null,
        };
    }

    private CompressionMethodOption SelectedMethod()
        => _methodCombo.SelectedItem is DisplayItem { Value: CompressionMethodOption method }
            ? method
            : CompressionMethodOption.Default;

    private EncryptionMethodOption SelectedEncryption()
        => _encryptionCombo.SelectedItem is DisplayItem { Value: EncryptionMethodOption encryption }
            ? encryption
            : EncryptionMethodOption.Default;

    private void RefreshState()
    {
        var format = SelectedFormat();
        var hasOutput = !string.IsNullOrWhiteSpace(_fileNameBox.Text);
        _startButton.IsEnabled = _inputs.Count > 0 && format.CanCreate && hasOutput;
        _passwordButton.IsEnabled = format.CanCreate && format.SupportsPassword;
        _volumeCombo.IsEnabled = format.CanCreate && format.SupportsVolume;
        _advancedButton.IsEnabled = format.CanCreate && format.SupportsAdvancedSettings;
        if (!_advancedButton.IsEnabled)
        {
            SetAdvancedPanelVisible(false);
        }

        if (!format.CanCreate)
        {
            ShowHint(_lang.Label("TEXT_STATUS_NOT_IMPLEMENTED"));
        }
        else
        {
            HideHint();
        }
    }

    private ArchiveFormatOption SelectedFormat()
        => _formatCombo.SelectedItem is DisplayItem { Value: ArchiveFormatOption format }
            ? format
            : ArchiveFormatOption.Default;

    private void ShowHint(string text)
    {
        _hint.Text = text;
        _hint.IsVisible = true;
    }

    private void HideHint() => _hint.IsVisible = false;

    private async Task StartAsync()
    {
        var format = SelectedFormat();
        if (_inputs.Count == 0)
        {
            ShowHint(_lang.Label("TEXT_ERR_NO_INPUT"));
            return;
        }

        if (!format.CanCreate)
        {
            ShowHint(_lang.Label("TEXT_STATUS_NOT_IMPLEMENTED"));
            return;
        }

        var output = _fileNameBox.Text?.Trim() ?? string.Empty;
        if (output.Length == 0)
        {
            ShowHint(_lang.Label("TEXT_ERR_OUTPUT_NOT_SET"));
            return;
        }

        var level = _levelCombo.SelectedItem is DisplayItem { Value: CompressionLevelOption option }
            ? option
            : CompressionLevelOption.Default;

        var settings = new ArchiveCreationSettings(
            output,
            level,
            Format: format,
            // 排除规则没有对应的对话框控件，所以它完全来自「设置 → 压缩」——
            // 用户在这里看不到勾，但包里确实不会混进 *.bak。
            ExcludeThumbsDb: _defaults.ExcludeThumbsDb,
            ExcludePatterns: _defaults.ParsedExcludePatterns,
            IncludeRootFolder: _defaults.IncludeRootFolder,
            Preallocate: _defaults.PreallocateDisk,
            ShareSources: _defaults.ShareSources,
            TestAfterCreation: _testAfterCheck.IsChecked == true,
            DeleteSourceAfterCreation: _deleteSourceCheck.IsChecked == true,
            SeparateArchives: _separateCheck.IsChecked == true,
            Password: format.SupportsPassword ? _password : null,
            VolumeSizeBytes: SelectedVolumeSizeBytes(),
            Method: format.SupportsAdvancedSettings ? SelectedMethod() : null,
            Encryption: format.SupportsAdvancedSettings ? SelectedEncryption() : null,
            EntryComment: format.SupportsAdvancedSettings && _fileCommentCheck.IsChecked == true
                ? _commentBox.Text?.Trim()
                : null);
        var inputs = _inputs.ToList();
        var cts = new CancellationTokenSource();
        var progress = new ProgressWindow(_lang, cts);

        settings = settings with { Progress = progress.CreateSink() };

        // 线程数必须在打开句柄之前设置才生效。它是引擎的**进程级**设置，所以本次对话框
        // 的选择不能留在原地影响下一次压缩 —— 记下来，finally 里还原。
        var previousThreads = ArchiveEngine.NumThreads;
        if (_threadsCombo.SelectedItem is DisplayItem { Value: uint threads })
        {
            ArchiveEngine.NumThreads = threads;
        }

        _ = progress.ShowDialog(this);

        try
        {
            var result = await Task.Run(() => ArchiveCreator.Create(inputs, settings, cts.Token), cts.Token);

            // 记账用的是真正写出来的那个文件所在的目录，而不是文本框里原本写的路径 ——
            // 分卷时末卷可能换了名字，但目录不会变；失败与取消都不记，否则一次拼错的路径
            // 会污染下一次的默认值。
            _rememberOutputFolder?.Invoke(
                Path.GetDirectoryName(Path.GetFullPath(result.OutputPath)) ?? string.Empty);

            // 引擎回传的“设置被忽略”提示不能静默丢弃。
            if (result.Warnings is { Count: > 0 } warnings)
            {
                ShowHint(string.Join(Environment.NewLine, warnings));
            }

            // 「设置 → 压缩 → 压缩完成后不关闭进度窗口」：留着时进度窗停在 100%，
            // 对话框也不关，用户看完自己关。它是 ShowDialog 出来的模态窗，所以先关它才回到对话框。
            if (!_defaults.KeepProgressWindow)
            {
                progress.Close();
                Close();
            }

            return;
        }
        catch (OperationCanceledException)
        {
            progress.Close();
            return;
        }
        catch (ArchiveConstraintException ex)
        {
            progress.Close();
            ShowHint(ex.Reason switch
            {
                ArchiveConstraintReason.GzRequiresSingleFile => _lang.Label("TEXT_ERR_GZ_SINGLE_FILE"),
                ArchiveConstraintReason.NonAsciiPasswordNotSupported => _lang.Label("TEXT_ERR_PASSWORD_ASCII"),
                _ => _lang.Label("TEXT_STATUS_NOT_IMPLEMENTED"),
            });
            return;
        }
        catch (ArchiveFailureException ex) when (ex.Reason == ArchiveFailureReason.EngineMissing)
        {
            // 引擎没加载上：这是安装问题，文案要能看出是缺 andzip_core 而不是“压缩失败”。
            progress.Close();
            ShowHint(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            progress.Close();
            ShowHint(ex is EmptyArchiveInputException ? _lang.Label("TEXT_ERR_NO_INPUT") : ex.Message);
            return;
        }
        finally
        {
            ArchiveEngine.NumThreads = previousThreads;
        }
    }
}