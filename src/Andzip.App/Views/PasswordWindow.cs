using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 密码输入对话框（FR-2.6 设置密码 / §3.2 对话框 #7 解压时输入密码，结构与参考软件 IDD_PASSWORD_INPUT 对齐）：
/// 设置模式为 密码 + 确认密码（不一致时本地化提示，留空确定 = 清除密码）；
/// 输入模式（requireConfirmation: false）仅密码一项，留空确定时提示必填。
/// </summary>
public sealed class PasswordWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly TextBox _passwordBox;
    private readonly TextBox? _confirmBox;
    private readonly TextBlock _hint;
    private readonly bool _requireConfirmation;

    /// <summary>确认后的密码；设置模式下 null 表示不加密。仅在 ShowDialog 返回 true 后有效。</summary>
    public string? Password { get; private set; }

    public PasswordWindow(LangCatalog lang, string? initialPassword = null, bool requireConfirmation = true)
    {
        _lang = lang;
        _requireConfirmation = requireConfirmation;

        Title = lang.Get("TEXT_TITLE_PASSWORD_INPUT");
        Width = 430;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontSize = 14;

        _passwordBox = new TextBox
        {
            PasswordChar = '●',
            Text = initialPassword ?? string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _confirmBox = requireConfirmation
            ? new TextBox
            {
                PasswordChar = '●',
                Text = initialPassword ?? string.Empty,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : null;
        _hint = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C42B1C")),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        var okButton = new Button { Content = lang.Get("TEXT_BTN_OK"), IsDefault = true, MinWidth = 96 };
        okButton.Click += (_, _) => Confirm();
        var cancelButton = new Button { Content = lang.Get("TEXT_BTN_CANCEL"), IsCancel = true, MinWidth = 96 };
        cancelButton.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var grid = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };

        var row = 0;
        AddRow(grid, row++, BuildLabel(lang.Get("TEXT_LABEL_PASSWORD")), _passwordBox);
        if (_confirmBox is not null)
        {
            AddRow(grid, row++, BuildLabel(lang.Get("TEXT_LABEL_PASSWORD_CONFIRM")), _confirmBox);
        }

        Grid.SetRow(_hint, row++);
        Grid.SetColumnSpan(_hint, 2);
        _hint.Margin = new Thickness(0, 8, 0, 0);
        grid.Children.Add(_hint);

        Grid.SetRow(buttons, row);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
        Opened += (_, _) => _passwordBox.Focus();
    }

    private static TextBlock BuildLabel(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 14, 6),
    };

    private static void AddRow(Grid grid, int row, Control label, Control field)
    {
        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(label);
        grid.Children.Add(field);
    }

    private void Confirm()
    {
        var password = _passwordBox.Text ?? string.Empty;

        if (!_requireConfirmation)
        {
            // 输入模式（解压加密包）：密码必填
            if (password.Length == 0)
            {
                _hint.Text = _lang.Get("TEXT_ERR_PASSWORD_EMPTY");
                _hint.IsVisible = true;
                return;
            }

            Password = password;
            Close(true);
            return;
        }

        var confirm = _confirmBox?.Text ?? string.Empty;

        if (password.Length > 0 && !string.Equals(password, confirm, StringComparison.Ordinal))
        {
            _hint.Text = _lang.Get("TEXT_ERR_PASSWORD_MISMATCH");
            _hint.IsVisible = true;
            return;
        }

        Password = password.Length == 0 ? null : password;
        Close(true);
    }
}