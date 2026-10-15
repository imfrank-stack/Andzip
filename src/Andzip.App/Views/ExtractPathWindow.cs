using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
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
/// 目标路径（可编辑下拉）+ 浏览（系统文件夹选择对话框）；
/// 「解压缩:」全部文件 / 选定的文件（无选中时禁用）+ 新建文件夹；
/// 复选项：解压到同名文件夹（默认勾选）/ 解压后删除压缩文件 / 记住设置；确定 / 取消。
/// <para>
/// 0.x 这里还有一行「桌面 / 我的文档 / 我的图片 / 我的电脑」快捷按钮，3.0.1 删掉了：
/// 四个目标里三个是系统已知文件夹，而路径框本来就是可编辑下拉，「浏览」又直接开系统的
/// 文件夹选择器 —— 那一行只是把系统里已经有的东西抄一份到对话框里占一行。
/// </para>
/// <para>
/// 这里<strong>没有</strong>自己画的目录树了。0.11.0 之前右边那块是一棵手写的
/// <c>TreeView</c>，而且它根本展不开（<c>ItemTemplate</c> 用的是扁平模板，没把
/// <c>FolderNode.Children</c> 绑成子项来源，连展开三角都不出现）。参考软件那块是系统
/// 的 shell 树（<c>SysTreeView32</c>），带图标、能一路展开到任意深度 —— 那种控件嵌不进
/// Avalonia 的窗口，所以浏览一律交给 <see cref="IStorageProvider.OpenFolderPickerAsync"/>：
/// 它就是系统组件，比参考软件那个还新（现代常见项对话框）。
/// </para>
/// </summary>
public sealed class ExtractPathWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly string _archivePath;
    private readonly ComboBox _pathCombo;
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
        Width = 700;
        // 高度跟着内容走，不是写死一个数：这一扇排到第 5 行时才 292 DIP 装得下按钮，
        // 少一行、换一句翻译就会把「确定」顶到窗口边上（实测贴到只剩 2 物理像素）。
        SizeToContent = SizeToContent.Height;
        MinWidth = 560;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.WindowSurface();

        // ---- 目标路径（可编辑下拉 + 系统文件夹选择）----
        var initialDestination = _remembered?.DestinationPath
                                 ?? Path.GetDirectoryName(Path.GetFullPath(archivePath))
                                 ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        _pathCombo = new ComboBox
        {
            IsEditable = true,
            ItemsSource = new List<string>(),
            Text = initialDestination,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        }.Picker();

        var browseButton = new Button { Content = lang.Label("TEXT_BTN_BROWSE"), MinWidth = 110 }.Ghost();
        browseButton.Click += async (_, _) => await BrowseAsync();

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
        var newFolderButton = new Button { Content = lang.Label("TEXT_BTN_NEW_FOLDER"), MinWidth = 120 }.Ghost();
        newFolderButton.Click += (_, _) => CreateFolder();

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
            Foreground = Design.Semantic.Danger,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        // ---- 确定 / 取消 ----
        var okButton = new Button { Content = lang.Label("TEXT_BTN_OK"), IsDefault = true, MinWidth = 96 }.Accent();
        okButton.Click += (_, _) => Confirm();
        var cancelButton = new Button { Content = lang.Label("TEXT_BTN_CANCEL"), IsCancel = true, MinWidth = 96 }.Ghost();
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
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
        };
        AddRow(layout, 0, BuildFieldRow(lang.Label("TEXT_LABEL_DEST_PATH"), _pathCombo, browseButton));
        AddRow(layout, 1, scopeRow);
        AddRow(layout, 2, new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            Children = { _intoFolderCheck, _deleteArchiveCheck, _rememberCheck },
        });
        AddRow(layout, 3, _hint);
        AddRow(layout, 4, buttons);

        this.Chromeless(lang, layout);
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

    /// <summary>
    /// 打开<strong>系统</strong>的文件夹选择对话框。给它 <c>SuggestedStartLocation</c>
    /// 是当前目标路径 —— 这样"换个子目录"是在原地往下走一层，而不是从我的文档重新开始翻。
    /// </summary>
    private async Task BrowseAsync()
    {
        var current = _pathCombo.Text?.Trim();
        IStorageFolder? startAt = null;
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            startAt = await StorageProvider.TryGetFolderFromPathAsync(current);
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = _lang.Label("TEXT_LABEL_DEST_PATH"),
            AllowMultiple = false,
            SuggestedStartLocation = startAt,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            _pathCombo.Text = path;
        }
    }

    /// <summary>在当前目标路径下创建「新建文件夹」（重名自动加序号），并把目标路径切换到新文件夹。</summary>
    private void CreateFolder()
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
}