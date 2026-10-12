using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>简单消息框（错误 / 提示）：标题 + 正文 + 确定。</summary>
public sealed class MessageWindow : Window
{
    public MessageWindow(LangCatalog lang, string title, string message)
    {
        // 标题与正文由调用方从语言包取好传进来，拿到的是原文（可能带 (&X)），
        // 所以在这个对话框的边界上统一剥一次，而不是要求每个调用点记得剥。
        Title = MnemonicText.Strip(title);
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontSize = 14;

        var text = new TextBlock
        {
            Text = MnemonicText.Strip(message),
            TextWrapping = TextWrapping.Wrap,
        };

        var okButton = new Button
        {
            Content = lang.Label("TEXT_BTN_OK"),
            IsDefault = true,
            IsCancel = true,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        okButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children = { text, okButton },
        };
    }
}