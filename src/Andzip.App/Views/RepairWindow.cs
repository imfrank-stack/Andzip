using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Andzip.App.Views;

/// <summary>
/// 「工具 → 修复压缩包」的模式选择框。三种模式是三件不同的事，所以这里不做一个"深度滑块"，
/// 而是各写清它会动到什么、以及它做不到什么 —— 选错了方向的用户比不修的用户更需要这句说明。
/// <para>
/// 输出路径默认是「原名.修复.zip」，且<strong>永远不等于输入</strong>：<see cref="ZipRepairer"/>
/// 会拒绝同路径，这里也不提供覆盖原包的选项。
/// </para>
/// </summary>
public sealed class RepairWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly string _suggestedOutput;
    private readonly RadioButton[] _modes;
    private readonly TextBlock _modeHint;
    private readonly TextBox _output;
    private readonly Button _okButton;

    /// <summary>选中的模式；仅在 ShowDialog 返回 true 后有。</summary>
    public ZipRepairMode Mode { get; private set; } = ZipRepairMode.RebuildCentralDirectory;

    /// <summary>输出文件路径。</summary>
    public string OutputPath { get; private set; } = string.Empty;

    public RepairWindow(LangCatalog lang, string archivePath, string suggestedOutput)
    {
        ArgumentNullException.ThrowIfNull(lang);
        _lang = lang;
        _suggestedOutput = suggestedOutput;

        Title = lang.Label("TEXT_TITLE_REPAIR");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontSize = 13;

        _modeHint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#555555")),
            Margin = new Thickness(20, 0, 20, 12),
        };

        _modes = [
            ModeRadio("TEXT_REPAIR_MODE_1"),
            ModeRadio("TEXT_REPAIR_MODE_2"),
            ModeRadio("TEXT_REPAIR_MODE_3"),
        ];

        // 一个提示行跟着三个单选钮转，比堆三段长文更好读 —— 用户只需要知道手上这一种会做什么。
        var hints = new (string Key, ZipRepairMode Mode)[]
        {
            ("TEXT_REPAIR_MODE_1_HINT", ZipRepairMode.RebuildCentralDirectory),
            ("TEXT_REPAIR_MODE_2_HINT", ZipRepairMode.JoinTruncatedParts),
            ("TEXT_REPAIR_MODE_3_HINT", ZipRepairMode.SalvageAndRepack),
        };

        // 每个单选钮都要抓住"自己"：闭包里直接用循环变量 i 会让三个处理程序都看到 i == 3，
        // 用户一点就 IndexOutOfRangeException（界面测试就是这么逮到的）。
        var hintsByIndex = _modes
            .Zip(hints, (radio, hint) => (radio, hint.Key, hint.Mode))
            .ToList();

        foreach (var (radio, hintKey, mode) in hintsByIndex)
        {
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    Mode = mode;
                    _modeHint.Text = lang.Label(hintKey);
                }
            };
        }

        _modes[0].IsChecked = true;

        _output = new TextBox
        {
            Text = suggestedOutput,
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var browse = new Button { Content = lang.Label("TEXT_BTN_BROWSE"), MinWidth = 96 };
        browse.Click += async (_, _) => await PickOutputAsync();

        _okButton = new Button
        {
            Content = lang.Label("TEXT_REPAIR_START"),
            IsDefault = true,
            MinWidth = 110,
        };
        _okButton.Click += (_, _) => Confirm();

        var cancelButton = new Button
        {
            Content = lang.Label("TEXT_BTN_CANCEL"),
            IsCancel = true,
            MinWidth = 96,
        };
        cancelButton.Click += (_, _) => Close(false);

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        panel.Children.Add(new TextBlock
        {
            Text = lang.Label("TEXT_REPAIR_INTRO"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        foreach (var radio in _modes)
        {
            panel.Children.Add(radio);
        }

        panel.Children.Add(_modeHint);

        panel.Children.Add(new TextBlock
        {
            Text = lang.Label("TEXT_REPAIR_OUTPUT"),
            Margin = new Thickness(0, 4, 0, 4),
        });
        var outputRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(browse, Dock.Right);
            outputRow.Children.Add(browse);
            outputRow.Children.Add(_output);
            panel.Children.Add(outputRow);

        panel.Children.Add(new TextBlock
        {
            // 光秃秃一个文件名会被当成"要修成这个名"；说清楚它是要被修的那个。
            Text = lang.Format("TEXT_REPAIR_SOURCE", ("file", Path.GetFileName(archivePath))),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse("#777777")),
        });

        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
            Children = { _okButton, cancelButton },
        });

        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        OutputPath = suggestedOutput;
    }

    private RadioButton ModeRadio(string key) => new()
    {
        Content = _lang.Label(key),
        Margin = new Thickness(0, 2, 0, 4),
        GroupName = "repairmode",
    };

    private void Confirm()
    {
        var path = _output.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            // 清空输入框当成"我没改主意"，回到建议路径 —— 而不是原地覆盖原包，那个语义我们不提供。
            path = _suggestedOutput;
        }

        OutputPath = path;
        Close(true);
    }

    private async Task PickOutputAsync()
    {
        var storage = StorageProvider;
        if (storage is null)
        {
            return;
        }

        var options = new FilePickerSaveOptions
        {
            Title = _lang.Label("TEXT_TITLE_REPAIR"),
            SuggestedFileName = Path.GetFileName(OutputPath),
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }],
        };

        if (await storage.SaveFilePickerAsync(options) is { } file)
        {
            _output.Text = file.Path.LocalPath;
        }
    }
}
