using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 「要解压的文件」重名询问对话框（FR-3.7，语言包 IDD_ASK_DUPEFILE）：
/// 显示冲突文件名，提供 重命名 / 覆盖 / 跳过 三种处理。
/// </summary>
public sealed class DuplicateFileWindow : Window
{
    /// <summary>用户选择（默认「重命名」）。仅在 ShowDialog 返回 true 后有效。</summary>
    public DuplicateDecision Decision { get; private set; } = DuplicateDecision.Rename;

    public DuplicateFileWindow(LangCatalog lang, string fileName)
    {
        Title = lang.Label("TEXT_TITLE_ASK_DUPEFILE");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.WindowSurface();

        var message = new TextBlock
        {
            Text = lang.Label("TEXT_MSG_FILE_EXISTS"),
            TextWrapping = TextWrapping.Wrap,
        };
        var name = new TextBlock
        {
            Text = fileName,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var renameButton = new Button { Content = lang.Label("TEXT_BTN_RENAME"), MinWidth = 96, IsDefault = true }.Accent();
        renameButton.Click += (_, _) => Choose(DuplicateDecision.Rename);
        var overwriteButton = new Button { Content = lang.Label("TEXT_BTN_OVERWRITE"), MinWidth = 96 }.Ghost();
        overwriteButton.Click += (_, _) => Choose(DuplicateDecision.Overwrite);
        var skipButton = new Button { Content = lang.Label("TEXT_BTN_SKIP"), MinWidth = 96, IsCancel = true }.Ghost();
        skipButton.Click += (_, _) => Choose(DuplicateDecision.Skip);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(renameButton);
        buttons.Children.Add(overwriteButton);
        buttons.Children.Add(skipButton);

        this.Chromeless(lang, new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children = { message, name, buttons },
        });
    }

    private void Choose(DuplicateDecision decision)
    {
        Decision = decision;
        Close(true);
    }
}