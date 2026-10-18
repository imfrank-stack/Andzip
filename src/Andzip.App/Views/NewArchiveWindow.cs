using System.Collections.ObjectModel;
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
    private readonly TextBlock _advancedTitle;
    private readonly ComboBox _methodCombo;
    private readonly TextBlock _methodNote;
    private readonly ComboBox _encryptionCombo;
    private readonly ComboBox _threadsCombo;
    private readonly CheckBox _symlinkCheck;
    private readonly CheckBox _fileCommentCheck;
    private readonly TextBox _commentBox;
    private readonly TextBlock _estimateValue;
    private readonly TextBlock _methodEstimates;
    private readonly StackPanel _formatAdvice;
    private ArchiveFormatOption _currentFormat = ArchiveFormatOption.Default;
    private string? _password;
    private int? _customVolumeMb;
    private int _lastVolumeIndex;
    private bool _suppressVolumeChange;

    /// <summary>
    /// 取样预估的取消源。<strong>只 Cancel、不 Dispose</strong>：上一份估算可能正跑在
    /// 线程池里、手里握着这个 token，提前 Dispose 会把"用户改了选择"变成一场
    /// ObjectDisposedException。没挂计时器也没做链接的 CTS 交给终结器收就够了，
    /// 而这里同一时刻最多攒着一两个。
    /// </summary>
    private CancellationTokenSource? _estimateCts;

    /// <summary>第几代估算。回来的结果只对得上最新那一代，旧的一律丢掉。</summary>
    private int _estimateGeneration;

    /// <summary>最新一次取样压出来的那份预估；换分卷时只重画它，不重压样本。</summary>
    private CompressionEstimator.Prediction? _lastPrediction;

    /// <summary>
    /// 「新建压缩文件」对话框（FR-2）。
    /// </summary>
    /// <param name="defaults">来自「设置 → 压缩」的默认值；null 表示用不带设置的那一套。
    /// 它只决定<strong>打开时长什么样</strong>，控件本身仍可逐次改。</param>
    /// <param name="seedPaths">右键「压缩到…」或拖进主窗口的那批文件 / 文件夹：预先填进输入列表，
    /// 并按它们决定「文件名」—— 只有一个就以它命名、放在它旁边；多个才退回「新建压缩」。
    /// null / 空表示从主菜单新建，一切从空开始。</param>
    /// <param name="lastOutputFolder">上次成功新建时用的目录，仅在输出目录策略是「沿用上次」时生效。</param>
    /// <param name="rememberOutputFolder">压成之后把输出目录记下来；null 表示不记账。
    /// 用回调而不是把 <c>AppSettings</c> 传进来：对话框不该知道设置存在哪里、什么时候落盘。</param>
    public NewArchiveWindow(
        LangCatalog lang,
        CompressionDefaults? defaults = null,
        IReadOnlyList<string>? seedPaths = null,
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
        this.WindowSurface();

        // ---- 列表与按钮 ----
        _deleteButton = new Button { Content = lang.Label("TEXT_BTN_DELETE"), IsEnabled = false, MinWidth = 96 }.Ghost();
        _deleteButton.Click += (_, _) => RemoveSelected();

        var addButton = new Button { Content = lang.Label("TEXT_BTN_ADD"), MinWidth = 96 }.Ghost();
        var addFilesItem = new MenuItem { Header = lang.Label("TEXT_MENU_ADD_FILES") }.MenuGrayHover();
        addFilesItem.Click += async (_, _) => await AddFilesAsync();
        var addFolderItem = new MenuItem { Header = lang.Label("TEXT_MENU_ADD_FOLDER") }.MenuGrayHover();
        addFolderItem.Click += async (_, _) => await AddFolderAsync();
        var addFlyout = new MenuFlyout();
        addFlyout.Items.Add(addFilesItem);
        addFlyout.Items.Add(addFolderItem);
        addButton.Flyout = addFlyout;

        _list = new ListBox
        {
            ItemsSource = _inputs,
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = RowTemplate.Of<ArchiveInputItem>(BuildRow, recycle: true),
            BorderThickness = new Thickness(1),
            BorderBrush = Design.Line.Strong,
            CornerRadius = Design.Radius.Sm,
        };
        _list.SelectionChanged += (_, _) => _deleteButton.IsEnabled = _list.SelectedItems?.Count > 0;

        // ---- 压缩文件设置 ----
        _fileNameBox = new TextBox { Text = DefaultOutputPath(), VerticalAlignment = VerticalAlignment.Center }.Field();
        _fileNameBox.TextChanged += (_, _) => RefreshState();

        var browseButton = new Button { Content = lang.Label("TEXT_BTN_BROWSE"), MinWidth = 110 }.Ghost();
        browseButton.Click += async (_, _) => await BrowseAsync();

        _formatCombo = new ComboBox
        {
            ItemsSource = ArchiveFormatOption.Creatable.Select(f => new DisplayItem(f.Id, f)).ToList(),
            SelectedIndex = 0,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        }.Picker();
        _formatCombo.SelectionChanged += (_, _) => OnFormatChanged();

        _passwordButton = new Button { Content = lang.Label("TEXT_BTN_SET_PASSWORD"), MinWidth = 150 }.Ghost();
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
        }.Picker();
        // 级别改的是压缩比本身，预估必须跟着重算 —— 只重算方法那一格会留下
        // "屏幕上是 41%、按 0-仅存储 压出来是 100%"这种自相矛盾。
        _levelCombo.SelectionChanged += (_, _) => RefreshState();

        _volumeCombo = new ComboBox
        {
            ItemsSource = VolumeOption.All
                .Select(o => new DisplayItem(lang.Label(o.TextKey), o))
                .ToList(),
            SelectedIndex = 0,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        }.Picker();
        _volumeCombo.SelectionChanged += async (_, _) => await OnVolumeChangedAsync();

        _hint = new TextBlock { Foreground = Design.Semantic.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        _startButton = new Button { Content = lang.Label("TEXT_BTN_START"), MinWidth = 96, IsDefault = true }.Accent();
        _startButton.Click += async (_, _) => await StartAsync();

        var cancelButton = new Button { Content = lang.Label("TEXT_BTN_CANCEL"), MinWidth = 96, IsCancel = true }.Ghost();
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
        }.Picker();

        // 选了扩展方法才说一句"这种包原版 7-Zip 打不开"。不做成一行常驻小字：
        // 默认那几档（LZMA2 / LZMA / PPMd…）没有这个问题，一直写着反而变成噪音。
        _methodNote = new TextBlock
        {
            Text = lang.Label("TEXT_METHOD_EXTENSION_NOTE"),
            Foreground = Design.Ink.Secondary,
            FontSize = Design.Type.Caption,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _methodCombo.SelectionChanged += (_, _) =>
        {
            RefreshMethodNote();
            RefreshEstimate();
        };

        // 面板标题跟着格式走：这一格里的方法表本来就是按格式给的（2.2.0 起 7z 有 11 项），
        // 顶着一句「ZIP 压缩设置」挑 LZ4 是自我矛盾 —— 真机截图里就是这样露出来的。
        _advancedTitle = SectionTitle(_lang.Format("TEXT_ADVANCED_TITLE", ("format", _currentFormat.Id)));

        _encryptionCombo = new ComboBox
        {
            ItemsSource = EncryptionMethodOption.All
                .Select(o => new DisplayItem(lang.Label(o.TextKey), o))
                .ToList(),
            SelectedIndex = EncryptionMethodOption.Default.Id,
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        }.Picker();

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
        }.Picker();

        _symlinkCheck = new CheckBox { Content = lang.Label("TEXT_CHECK_STORE_SYMLINKS"), IsEnabled = false };
        _fileCommentCheck = new CheckBox { Content = lang.Label("TEXT_CHECK_FILE_COMMENT") };
        _commentBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            IsEnabled = false,
        };

        // 压缩后的体积是"要不要压、值不值得等"这一判断的全部依据，所以它必须在这里说清楚，
        // 而不是等压完才知道。数字来自 CompressionEstimator：拿输入里按比例取的一段样本，
        // 用同一个编码器真的压一遍再外推 —— 不是查表猜的比例。
        // 界面上始终写"预估"：它是量出来的推断，不是实测。
        _estimateValue = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Design.Ink.Secondary,
            TextWrapping = TextWrapping.Wrap,
        };

        // 单个条目特别大时的一句"换 7z 压得更小"。判断在 Core（LargeEntryAdvice），
        // 这里只负责说出来和给一键。它是建议不是拦截：按下"开始"照样按 ZIP 压。
        var switchToSevenZip = new Button { Content = lang.Label("TEXT_HINT_SWITCH_TO_7Z"), MinWidth = 90 }.Ghost();
        _formatAdvice = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X2,
            IsVisible = false,
            Children =
            {
                new TextBlock
                {
                    Text = lang.Label("TEXT_HINT_ZIP_LARGE_ENTRY"),
                    Foreground = Design.Ink.Secondary,
                    FontSize = Design.Type.Caption,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                switchToSevenZip,
            },
        };
        switchToSevenZip.Click += (_, _) => SelectSevenZipFormat();

        // 每种方法各压一小段（表预算比单格小得多，图的是这份排序而不是第三位小数）。
        // 放在高级面板里：这一格本来就是"在方法之间挑一个"的地方。
        _methodEstimates = new TextBlock
        {
            Foreground = Design.Ink.Secondary,
            FontSize = Design.Type.Caption,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _fileCommentCheck.IsCheckedChanged += (_, _) =>
            _commentBox.IsEnabled = _fileCommentCheck.IsChecked == true;

        _advancedButton = new Button
        {
            Content = lang.Label("TEXT_BTN_ADVANCED_SETTINGS"),
            Width = 170,
            HorizontalAlignment = HorizontalAlignment.Left,
        }.Ghost();

        _advancedPanel = new Border
        {
            IsVisible = false,
            Width = 320,
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = Design.Line.Subtle,
            Margin = new Thickness(8, 16, 16, 16),
            Padding = new Thickness(14, 0, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 10,
                Children =
                {
                    _advancedTitle,
                    BuildFieldRow(lang.Label("TEXT_LABEL_COMPRESSION_METHOD"), _methodCombo),
                    _methodNote,
                    _methodEstimates,
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
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };

        AddRow(grid, 0, SectionTitle(lang.Label("TEXT_SECTION_ADD_FILES")), 3);
        AddRow(grid, 1, BuildColumnHeader(), 3);
        AddRow(grid, 2, _list, 3);
        AddRow(grid, 3, RightAligned(addButton, _deleteButton), 3);
        AddRow(grid, 4, new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Design.Line.Subtle, Margin = new Thickness(0, Design.Space.X3, 0, Design.Space.X3) }, 3);
        AddRow(grid, 5, SectionTitle(lang.Label("TEXT_SECTION_ARCHIVE_SETTINGS")), 3);
        AddRow(grid, 6, BuildFieldRow(lang.Label("TEXT_LABEL_FILE_NAME"), _fileNameBox, browseButton));
        AddRow(grid, 7, BuildFieldRow(lang.Label("TEXT_LABEL_FORMAT"), _formatCombo, _passwordButton));
        AddRow(grid, 8, moreOptionsPanel, 3);
        AddRow(grid, 9, BuildFieldRow(lang.Label("TEXT_LABEL_ESTIMATE"), _estimateValue), 3);
        AddRow(grid, 10, _hint, 3);
        AddRow(grid, 11, _formatAdvice, 3);
        AddRow(grid, 12, BuildBottomRow(moreOptionsCheck, cancelButton), 3);

        // 右侧「高级设置」面板与主区域并排（展开时窗口加宽，FR-2.5）
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(grid, 0);
        Grid.SetColumn(_advancedPanel, 1);
        root.Children.Add(grid);
        root.Children.Add(_advancedPanel);

        this.Chromeless(lang, root);

        if (seedPaths is { Count: > 0 } seeds)
        {
            SeedFrom(seeds);
        }

        // 关窗之后没有人再要这一行字，而估算可能正压着一份样本。
        Closed += (_, _) => _estimateCts?.Cancel();

        RefreshState();
    }

    /// <summary>
    /// 预填：把选中的路径放进输入列表，并按它们决定「文件名」。
    /// <para>
    /// 命名走 <see cref="ShellTasks.SuggestedArchivePath"/> 而不是在这里再拼一遍 ——
    /// 单个输入以它命名（文件夹名里的点不能当扩展名剥掉，<c>my.app</c> 要得到
    /// <c>my.app.zip</c>），多个才退回「新建压缩」，这条规则已经测过了，不该有两份。
    /// </para>
    /// <para>刻意<strong>不</strong>做重名避让：这里是给用户看的建议名，他自己会改；
    /// 真到落盘时 <c>ArchiveCreationSettings.Overwrite</c> 那条路径负责不悄悄覆盖。</para>
    /// </summary>
    private void SeedFrom(IReadOnlyList<string> seedPaths)
    {
        foreach (var path in seedPaths)
        {
            AddInput(path);
        }

        _fileNameBox.Text = ShellTasks.SuggestedArchivePath(
            seedPaths, SelectedFormat().Extension, FallbackOutputFolder(), _lang.Label("TEXT_DEFAULT_ARCHIVE_NAME"));
    }

    /// <summary>展开 / 收起右侧高级设置面板（按钮文案同步为「« 高级设置」，窗口宽度相应变化）。</summary>
    private void SetAdvancedPanelVisible(bool visible)
    {
        _advancedPanel.IsVisible = visible;
        _advancedButton.Content = _lang.Label(visible ? "TEXT_BTN_ADVANCED_SETTINGS_COLLAPSE" : "TEXT_BTN_ADVANCED_SETTINGS");
        Width = visible ? 1292 : 972;

        // 「每种方法各能压到多大」这张表只在面板展开时才算：它是这一格里那几个方法的对比，
        // 面板收着的时候没人看，而它是真压一遍样本，不该白算。
        if (visible)
        {
            RefreshEstimate();
        }
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
        var path = new TextBlock { Text = item.FullPath, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Design.Ink.Secondary };
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

    private string DefaultOutputPath()
        => Path.Combine(FallbackOutputFolder(), _lang.Label("TEXT_DEFAULT_ARCHIVE_NAME") + _currentFormat.Extension);

    /// <summary>
    /// 没有任何输入（或输入分散在好几个目录）时，「当前位置」的兜底：
    /// 按「设置 → 压缩 → 输出目录」选，两处都不成立就用文档目录。
    /// <para>
    /// 「固定目录」里填了不存在的路径时<strong>不</strong>当场创建：这个值只是填进文本框的建议，
    /// 用户可能马上改掉；真到压的时候 <c>ArchiveCreateValidator.Prepare</c> 会建目录，
    /// 那时失败才有意义（也会给出错误框而不是静默建一堆目录）。
    /// </para>
    /// </summary>
    private string FallbackOutputFolder()
    {
        var directory = _defaults.OutputMode == SettingKeys.OutputFolderMode.Fixed
            ? FirstDirectory(_defaults.FixedOutputFolder, _lastOutputFolder)
            : FirstDirectory(_lastOutputFolder, _defaults.FixedOutputFolder);

        return directory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
    private string MethodName(CompressionMethodOption option)
        => option.TextKey is { } key ? _lang.Label(key) : option.DisplayName;

    private List<DisplayItem> MethodItems(ArchiveFormatOption format)
        => [.. format.Methods.Select(option => new DisplayItem(MethodName(option), option))];

    /// <summary>换格式后重建方法列表；保持用户已选的方法（如果新格式也支持）。</summary>
    private void RefreshMethodOptions(ArchiveFormatOption format)
    {
        var previous = (_methodCombo.SelectedItem as DisplayItem)?.Value as CompressionMethodOption;
        var items = MethodItems(format);
        _methodCombo.ItemsSource = items;
        var keep = previous is null ? -1 : items.FindIndex(item => Equals(item.Value, previous));
        _methodCombo.SelectedIndex = keep >= 0 ? keep : 0;
        _advancedTitle.Text = _lang.Format("TEXT_ADVANCED_TITLE", ("format", format.Id));
        RefreshMethodNote();
    }

    /// <summary>只有选了 7z 的扩展方法才露出那一句说明（换格式、换方法都走这里）。</summary>
    private void RefreshMethodNote()
        => _methodNote.IsVisible = CompressionMethodOption.IsExtension(SelectedMethod());

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
            ReRenderEstimate();
            return;
        }

        var dialog = new VolumeSizeWindow(_lang, _customVolumeMb ?? 5);
        if (await dialog.ShowDialog<bool>(this))
        {
            _customVolumeMb = dialog.SizeMb;
            _lastVolumeIndex = _volumeCombo.SelectedIndex;
            UpdateCustomVolumeText(dialog.SizeMb);
            ReRenderEstimate();
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

    private CompressionLevelOption SelectedLevel()
        => _levelCombo.SelectedItem is DisplayItem { Value: CompressionLevelOption option }
            ? option
            : CompressionLevelOption.Default;

    /// <summary>
    /// 把这一对话框的控件状态拼成 Core 的创建设置。<strong>「开始」与取样预估共用这一份</strong>：
    /// 分成两处写就允许出现"屏幕上说 1.24 GB、按下去压出 3 GB" —— 而那个数是用户决定
    /// 值不值得等的依据，不能各说各话。
    /// <see cref="ArchiveCreationSettings.Progress"/> 由调用方自己接（预估不装回调）。
    /// </summary>
    private ArchiveCreationSettings BuildSettings(string output, CompressionLevelOption? level = null)
    {
        var format = SelectedFormat();
        return new ArchiveCreationSettings(
            output,
            level ?? SelectedLevel(),
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
    }

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

        // 每一个会改变"压完多大"的入口都汇到这里：加减文件、换格式、换级别、换方法、
        // 改分卷、甚至改文件名。RefreshEstimate 里有一道合并窗口，所以打字不会被拖累。
        RefreshFormatAdvice();
        RefreshEstimate();
    }

    /// <summary>
    /// 重开一份取样估算。旧的先取消，新的等 350ms 没有新改动才真压样本。
    /// <para>
    /// 只 Cancel、不 Dispose 上一个 CTS：还在跑的那一份手里握着它的 token。
    /// </para>
    /// </summary>
    private void RefreshEstimate()
    {
        _estimateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _estimateCts = cts;
        _ = EstimateAsync(cts.Token);
    }

    private async Task EstimateAsync(CancellationToken cancellationToken)
    {
        var generation = ++_estimateGeneration;
        var format = SelectedFormat();
        var inputs = _inputs.ToList();

        if (inputs.Count == 0 || !format.CanCreate)
        {
            _lastPrediction = null;
            _estimateValue.Text = string.Empty;
            _methodEstimates.IsVisible = false;
            RefreshFormatAdvice();
            return;
        }

        // 与"开始"用的是同一个 BuildSettings：屏幕上那句预估说的就是按下去会发生的这次压缩。
        var settings = BuildSettings(string.IsNullOrWhiteSpace(_fileNameBox.Text)
            ? DefaultOutputPath()
            : _fileNameBox.Text!.Trim());

        _estimateValue.Text = _lang.Label("TEXT_ESTIMATE_WORKING");
        var wantTable = _advancedPanel.IsVisible;
        if (wantTable)
        {
            _methodEstimates.Text = _lang.Label("TEXT_ESTIMATE_WORKING");
            _methodEstimates.IsVisible = true;
        }

        try
        {
            // 取样是真的压一遍（Core 里那 4 MiB 样本），所以这里要有合并窗口：连着点两次
            // 下拉框不该压两遍。代价是明知过期的那一份还是要跑完（最长几秒）—— 换来的是
            // 这一格永远在算最新的那个选择，而不是一堆互相覆盖的半截结果。
            await Task.Delay(350, cancellationToken);

            var prediction = await Task.Run(
                () => CompressionEstimator.Estimate(inputs, settings), cancellationToken);
            if (generation != _estimateGeneration)
            {
                return;
            }

            _lastPrediction = prediction;
            RenderEstimate();
            RefreshFormatAdvice();

            if (wantTable)
            {
                var rows = await Task.Run(
                    () => CompressionEstimator.EstimateMethods(inputs, settings), cancellationToken);
                if (generation != _estimateGeneration)
                {
                    return;
                }

                _methodEstimates.Text = _lang.Format("TEXT_ESTIMATE_METHODS", ("list", FormatMethodTable(rows)));
            }
        }
        catch (OperationCanceledException)
        {
            // 用户已经改了这一项，那一句不该再落到屏幕上
        }
    }

    /// <summary>
    /// 把最新一份预估写进那一行。换分卷也走这里：卷的大小不改变压完的总量，
    /// 只改变它被分成几个文件 —— 为这个重压一遍样本是浪费。
    /// </summary>
    private void RenderEstimate()
    {
        if (_lastPrediction is not { } prediction)
        {
            // 取样压不出数（引擎没装、这个格式写不了、输入全都读不动）时说的是"无法预估"，
            // 而不是留一个看起来像 0 的空格。
            _estimateValue.Text = _lang.Label("TEXT_ESTIMATE_UNAVAILABLE");
            return;
        }

        var text = _lang.Format("TEXT_ESTIMATE_DETAIL",
            ("size", SizeText.Format(prediction.OutputBytes)),
            ("percent", $"{(int)Math.Round(prediction.Ratio * 100.0)}%"),
            ("time", DurationText.Format(prediction.Duration)));

        if (VolumeCount(prediction.OutputBytes) is { } volumes)
        {
            text += $"  ·  {_lang.Format("TEXT_ESTIMATE_VOLUMES", ("count", volumes.ToString()))}";
        }

        _estimateValue.Text = text;
    }

    /// <summary>
    /// 换分卷时重画这一行。卷大小不改变压完的总量，只改变它被分成几个文件 ——
    /// 为这个重压一遍样本是浪费，所以这里不叫 RefreshEstimate。
    /// </summary>
    private void ReRenderEstimate()
    {
        if (_lastPrediction is not null)
        {
            RenderEstimate();
        }
    }

    /// <summary>这一包会被切成几个文件？没设分卷或装得下时给 null（那句就不说）。</summary>
    private int? VolumeCount(long outputBytes)    {
        if (SelectedVolumeSizeBytes() is not { } size || size <= 0 || outputBytes <= size)
        {
            return null;
        }

        return (int)((outputBytes + size - 1) / size);
    }

    /// <summary>
    /// 那句「换成 7z 更小」该不该说。依据是刚回来的那份预估顺手量到的最大单条目
    /// （<see cref="LargeEntryAdvice"/>）—— 不为了这一句话把目录再走一遍。
    /// </summary>
    private void RefreshFormatAdvice()
        => _formatAdvice.IsVisible = _lastPrediction is { } prediction
            && LargeEntryAdvice.SuggestSevenZip(SelectedFormat(), prediction.LargestEntryBytes)
            && SevenZipFormatIndex() is not null;

    /// <summary>
    /// 7z 在格式下拉里排第几。引擎此刻写不了 7z 时它是 null —— 那时候这句建议没处可去，
    /// 不该摆一颗点了没反应的按钮。
    /// </summary>
    private int? SevenZipFormatIndex()
    {
        if (_formatCombo.ItemsSource is not IReadOnlyList<DisplayItem> items)
        {
            return null;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Value is ArchiveFormatOption { IsSevenZip: true })
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>一键切到 7z：改的是下拉框的选择，所以扩展名同步、方法表重建、预估重算都自动跟着发生。</summary>
    private void SelectSevenZipFormat()
    {
        if (SevenZipFormatIndex() is { } index)
        {
            _formatCombo.SelectedIndex = index;
        }
    }

    /// <summary>每种方法各压一小段之后的总大小，按格式自己的方法表顺序排。要的是这一排数的对比。</summary>
    private string FormatMethodTable(
        IReadOnlyList<(CompressionMethodOption Method, CompressionEstimator.Prediction? Prediction)> rows)
        => string.Join("  ·  ", rows.Select(row => row.Prediction is { } prediction
            ? $"{MethodName(row.Method)} ≈{SizeText.Format(prediction.OutputBytes)}"
            : $"{MethodName(row.Method)} —"));

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

        var settings = BuildSettings(output, level);
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

        // 真的要压了，就把手里那份取样估算停掉：它和这次压缩抢的是同一批 CPU 核，
        // 而屏幕上这一行已经改成实测的进度了。
        _estimateCts?.Cancel();

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

            // 「设置 → 压缩 → 压缩完成后不关闭进度窗口」：留着时这一屏要说"压缩完成"，
            // 对话框也不关，用户看完自己关。它是 ShowDialog 出来的模态窗，所以先关它才回到对话框。
            if (!_defaults.KeepProgressWindow)
            {
                progress.Close();
                Close();
            }
            else
            {
                progress.Finish("TEXT_STATUS_COMPRESS_DONE", result.OutputBytes);
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
        catch (ArchiveFailureException ex) when (ex.Reason == ArchiveFailureReason.TargetLocked)
        {
            // 目标被占着（或只读）。引擎只会说 cannot create '<路径>'，既不说为什么也不说该怎么办，
            // 而这一种失败用户自己就能解开 —— 所以这句话要给出去路，不是给一个错误码。
            progress.Close();
            ShowHint(_lang.Format("TEXT_ERR_TARGET_LOCKED", ("path", ex.Message)));
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