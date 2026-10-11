using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>「自定义大小」分卷对话框（FR-2.3）：输入每个分卷的 MB 值（整数，1 – 102400）。</summary>
public sealed class VolumeSizeWindow : Window
{
    private const int MaxSizeMb = 102400;

    private readonly LangCatalog _lang;
    private readonly TextBox _sizeBox;
    private readonly TextBlock _hint;

    /// <summary>确认后的分卷大小（MB）。仅在 ShowDialog 返回 true 后有效。</summary>
    public int SizeMb { get; private set; }

    public VolumeSizeWindow(LangCatalog lang, int initialSizeMb)
    {
        _lang = lang;

        Title = lang.Get("TEXT_TITLE_VOLUME_SIZE");
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontSize = 14;

        _sizeBox = new TextBox
        {
            Text = initialSizeMb.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
        };
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
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        };

        var label = new TextBlock
        {
            Text = lang.Get("TEXT_LABEL_VOLUME_SIZE"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        Grid.SetRow(label, 0);
        Grid.SetColumn(label, 0);
        Grid.SetRow(_sizeBox, 0);
        Grid.SetColumn(_sizeBox, 1);
        grid.Children.Add(label);
        grid.Children.Add(_sizeBox);

        Grid.SetRow(_hint, 1);
        Grid.SetColumnSpan(_hint, 2);
        _hint.Margin = new Thickness(0, 8, 0, 0);
        grid.Children.Add(_hint);

        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
        Opened += (_, _) => _sizeBox.Focus();
    }

    private void Confirm()
    {
        if (!int.TryParse(_sizeBox.Text?.Trim(), out var sizeMb) || sizeMb < 1 || sizeMb > MaxSizeMb)
        {
            _hint.Text = _lang.Get("TEXT_ERR_VOLUME_SIZE_INVALID");
            _hint.IsVisible = true;
            return;
        }

        SizeMb = sizeMb;
        Close(true);
    }
}