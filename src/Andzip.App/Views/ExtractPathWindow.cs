using System.Collections.ObjectModel;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Andzip.App.Views;

/// <summary>「选择解压路径」对话框的选择结果（FR-3.1）。</summary>
public sealed record ExtractPathChoice(
    string DestinationPath,
    bool ExtractIntoArchiveNamedFolder,
    bool DeleteArchiveAfterExtraction,
    bool RememberSettings);

/// <summary>
/// 「选择解压路径」对话框（FR-3.1 / 参考截图 04）：
/// 目标路径（可编辑下拉）+ 文件夹选择；左侧快捷位置、右侧文件夹树；
/// 「解压缩:」全部文件 / 选定的文件（无选中时禁用）+ 新建文件夹；
/// 复选项：解压到同名文件夹（默认勾选）/ 解压后删除压缩文件 / 记住设置；确定 / 取消。
/// </summary>
public sealed class ExtractPathWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly string _archivePath;
    private readonly ComboBox _pathCombo;
    private readonly TreeView _tree;
    private readonly CheckBox _intoFolderCheck;
    private readonly CheckBox _deleteArchiveCheck;
    private readonly CheckBox _rememberCheck;
    private readonly TextBlock _hint;

    /// <summary>「记住设置」的会话内记忆（跨重启持久化随设置页 M4 实现）。</summary>
    private static ExtractPathChoice? _remembered;

    /// <summary>确认后的选择结果。仅在 ShowDialog 返回 true 后有效。</summary>
    public ExtractPathChoice? Choice { get; private set; }

    public ExtractPathWindow(LangCatalog lang, string archivePath)
    {
        _lang = lang;
        _archivePath = archivePath;

        Title = lang.Label("TEXT_TITLE_EXTRACT_PATH");
        Width = 760;
        Height = 560;
        MinWidth = 640;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontSize = 14;

        // ---- 目标路径（可编辑下拉 + 文件夹选择）----
        var initialDestination = _remembered?.DestinationPath
                                 ?? Path.GetDirectoryName(Path.GetFullPath(archivePath))
                                 ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        _pathCombo = new ComboBox
        {
            IsEditable = true,
            ItemsSource = new List<string>(),
            Text = initialDestination,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var browseButton = new Button { Content = lang.Label("TEXT_BTN_BROWSE"), MinWidth = 110 };
        browseButton.Click += async (_, _) => await BrowseAsync();

        // ---- 左侧快捷位置 + 右侧文件夹树 ----
        var quickPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6, Width = 150 };
        quickPanel.Children.Add(QuickLocation(lang.Label("TEXT_QUICK_DESKTOP"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
        quickPanel.Children.Add(QuickLocation(lang.Label("TEXT_QUICK_MY_DOCUMENTS"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        quickPanel.Children.Add(QuickLocation(lang.Label("TEXT_QUICK_MY_PICTURES"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
        quickPanel.Children.Add(QuickLocation(lang.Label("TEXT_QUICK_MY_COMPUTER"), Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"));

        _tree = new TreeView
        {
            ItemsSource = LoadRoots(),
            ItemTemplate = new FuncDataTemplate<FolderNode>((node, _) => new TextBlock
            {
                Text = node!.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
            }),
        };
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedItem is FolderNode node)
            {
                EnsureChildrenLoaded(node);
                _pathCombo.Text = node.Path;
            }
        };

        var treeBox = new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#C8C8C8")),
            Child = _tree,
        };

        var contentGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("*"),
        };
        Grid.SetColumn(quickPanel, 0);
        Grid.SetColumn(treeBox, 1);
        quickPanel.Margin = new Thickness(0, 0, 10, 0);
        contentGrid.Children.Add(quickPanel);
        contentGrid.Children.Add(treeBox);

        // ---- 解压缩: 全部 / 选定 + 新建文件夹 ----
        var allFiles = new RadioButton
        {
            Content = lang.Label("TEXT_RADIO_ALL_FILES"),
            IsChecked = true,
            GroupName = "extract-scope",
        };
        var selectedFiles = new RadioButton
        {
            Content = lang.Label("TEXT_RADIO_SELECTED_FILES"),
            IsEnabled = false, // 压缩包视图（M3）提供选中上下文后启用
            GroupName = "extract-scope",
        };
        var newFolderButton = new Button { Content = lang.Label("TEXT_BTN_NEW_FOLDER"), MinWidth = 120 };
        newFolderButton.Click += async (_, _) => await CreateFolderAsync();

        var scopeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*,Auto") };
        var scopeLabel = new TextBlock
        {
            Text = lang.Label("TEXT_SECTION_EXTRACT"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(scopeLabel, 0);
        Grid.SetColumn(allFiles, 1);
        Grid.SetColumn(selectedFiles, 2);
        Grid.SetColumn(newFolderButton, 4);
        scopeRow.Children.Add(scopeLabel);
        scopeRow.Children.Add(allFiles);
        scopeRow.Children.Add(selectedFiles);
        scopeRow.Children.Add(newFolderButton);

        // ---- 复选项 ----
        _intoFolderCheck = new CheckBox
        {
            Content = lang.Label("TEXT_CHECK_EXTRACT_TO_SUBFOLDER"),
            IsChecked = _remembered?.ExtractIntoArchiveNamedFolder ?? true,
        };
        _deleteArchiveCheck = new CheckBox
        {
            Content = lang.Label("TEXT_CHECK_DELETE_ARCHIVE_AFTER"),
            IsChecked = _remembered?.DeleteArchiveAfterExtraction ?? false,
        };
        _rememberCheck = new CheckBox
        {
            Content = lang.Label("TEXT_CHECK_REMEMBER_SETTINGS"),
            IsChecked = _remembered?.RememberSettings ?? false,
        };

        _hint = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C42B1C")),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        // ---- 确定 / 取消 ----
        var okButton = new Button { Content = lang.Label("TEXT_BTN_OK"), IsDefault = true, MinWidth = 96 };
        okButton.Click += (_, _) => Confirm();
        var cancelButton = new Button { Content = lang.Label("TEXT_BTN_CANCEL"), IsCancel = true, MinWidth = 96 };
        cancelButton.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var layout = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto,Auto"),
        };
        AddRow(layout, 0, BuildFieldRow(lang.Label("TEXT_LABEL_DEST_PATH"), _pathCombo, browseButton));
        AddRow(layout, 1, contentGrid);
        AddRow(layout, 2, scopeRow);
        AddRow(layout, 3, new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Children = { _intoFolderCheck, _deleteArchiveCheck, _rememberCheck },
        });
        AddRow(layout, 4, _hint);
        AddRow(layout, 5, buttons);

        Content = layout;
    }

    private static void AddRow(Grid grid, int row, Control control)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private static Control BuildFieldRow(string label, Control field, Control trailing)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 12),
        };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(field, 1);
        Grid.SetColumn(trailing, 2);
        row.Children.Add(text);
        row.Children.Add(field);
        row.Children.Add(trailing);
        return row;
    }

    private Button QuickLocation(string text, string path)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(path))
            {
                _pathCombo.Text = path;
                SelectPathInTree(path);
            }
        };
        return button;
    }

    private void SelectPathInTree(string path)
    {
        foreach (var node in Flatten(_tree.ItemsSource as IEnumerable<FolderNode> ?? []))
        {
            if (string.Equals(node.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                _tree.SelectedItem = node;
                return;
            }
        }
    }

    private static IEnumerable<FolderNode> Flatten(IEnumerable<FolderNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    private ObservableCollection<FolderNode> LoadRoots()
    {
        var roots = new ObservableCollection<FolderNode>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }

            roots.Add(new FolderNode(drive.RootDirectory.FullName, drive.Name));
        }

        return roots;
    }

    private static void EnsureChildrenLoaded(FolderNode node)
    {
        if (node.Loaded)
        {
            return;
        }

        node.Loaded = true;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(node.Path))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                node.Children.Add(new FolderNode(directory, name));
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private async Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _lang.Label("TEXT_LABEL_DEST_PATH"),
            AllowMultiple = false,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            _pathCombo.Text = path;
            SelectPathInTree(path);
        }
    }

    /// <summary>在当前目标路径下创建「新建文件夹」（重名自动加序号），并把目标路径切换到新文件夹。</summary>
    private async Task CreateFolderAsync()
    {
        var parent = _pathCombo.Text?.Trim();
        if (string.IsNullOrEmpty(parent))
        {
            parent = Path.GetDirectoryName(Path.GetFullPath(_archivePath));
        }

        if (string.IsNullOrEmpty(parent))
        {
            ShowHint(_lang.Label("TEXT_ERR_DEST_NOT_SET"));
            return;
        }

        try
        {
            Directory.CreateDirectory(parent);
            var name = _lang.Label("TEXT_NEW_FOLDER_DEFAULT");
            var target = Path.Combine(parent, name);
            for (var index = 2; Directory.Exists(target); index++)
            {
                target = Path.Combine(parent, $"{name} ({index})");
            }

            Directory.CreateDirectory(target);
            _pathCombo.Text = target;
        }
        catch (Exception ex)
        {
            ShowHint(ex.Message);
        }

        await Task.CompletedTask;
    }

    private void Confirm()
    {
        var destination = _pathCombo.Text?.Trim() ?? string.Empty;
        if (destination.Length == 0)
        {
            ShowHint(_lang.Label("TEXT_ERR_DEST_NOT_SET"));
            return;
        }

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            ShowHint(ex.Message);
            return;
        }

        Choice = new ExtractPathChoice(
            Path.GetFullPath(destination),
            _intoFolderCheck.IsChecked == true,
            _deleteArchiveCheck.IsChecked == true,
            _rememberCheck.IsChecked == true);

        _remembered = Choice.RememberSettings ? Choice : null;
        Close(true);
    }

    private void ShowHint(string text)
    {
        _hint.Text = text;
        _hint.IsVisible = true;
    }

    /// <summary>文件夹树节点（展开时按需加载子目录）。</summary>
    private sealed class FolderNode(string path, string displayName)
    {
        public string Path { get; } = path;

        public string DisplayName { get; } = displayName;

        public ObservableCollection<FolderNode> Children { get; } = [];

        public bool Loaded { get; set; }
    }
}