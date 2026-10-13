using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 设置页的一行：<strong>左边说明、右边控件、说明下面一行灰字补充</strong>。
/// <para>1.7.0 跟着参考风格把"勾选框 + 后面跟一串字"换成这个排布。理由不只是好看：
/// 原来的写法里说明文字跟着勾选框的宽度走，中英文一换行就断在不同位置；
/// 现在标签列固定占满、控件永远贴右，两种语言下都对齐在同一条竖线上。</para>
/// <para>行与行之间不画分隔线 —— 参考图里也没有。分层靠间距，画线只会让一屏出现十几条线。</para>
/// </summary>
internal static class SettingRow
{
    /// <summary>开关行。返回的控件本身就是那一行，布尔值读它的 <c>IsChecked</c>。</summary>
    public static SettingToggle Toggle(LangCatalog lang, string labelKey, string? hintKey, Action onToggled)
        => new(lang.Label(labelKey), hintKey is null ? null : lang.Label(hintKey), onToggled);

    /// <summary>左标签右控件的一行（下拉、输入框都用它）。</summary>
    public static Control Labeled(string label, Control control, LangCatalog lang, string? hintKey = null)
    {
        var left = new StackPanel { Spacing = Design.Space.X1 };
        left.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Design.Ink.Primary,
            FontSize = Design.Type.Body,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        if (hintKey is not null)
        {
            left.Children.Add(new TextBlock
            {
                Text = lang.Label(hintKey),
                Foreground = Design.Ink.Secondary,
                FontSize = Design.Type.Caption,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(Design.Space.X4, 0, 0, 0);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, Design.Space.X2, 0, Design.Space.X2),
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(left);
        grid.Children.Add(control);
        return grid;
    }
}

/// <summary>
/// 设置页的开关行。继承 <see cref="Border"/> 只是为了能直接塞进面板 ——
/// 它本身不画东西，卡片底由所在的那张卡负责。
/// <para><strong>为什么不直接用 <see cref="CheckBox"/> 改模板</strong>：本工程没有 XAML，
/// 改模板要手写一份 <c>ControlTheme</c>，那份东西一旦和 SimpleTheme 的内部主题打架，
/// 排查成本远高于在这里自己拼一行。而开关本体仍然用 <see cref="ToggleButton"/>，
/// 于是键盘（Tab 聚焦 + 空格切换）、读屏、<c>IsChecked</c> 语义全都是白拿的。</para>
/// </summary>
internal sealed class SettingToggle : Border
{
    private readonly ToggleButton _toggle = new();

    public SettingToggle(string label, string? hint, Action onToggled)
    {
        Label = label;

        var text = new StackPanel { Spacing = Design.Space.X1 };
        text.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Design.Ink.Primary,
            FontSize = Design.Type.Body,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (hint is not null)
        {
            text.Children.Add(new TextBlock
            {
                Text = hint,
                Foreground = Design.Ink.Secondary,
                FontSize = Design.Type.Caption,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        _toggle.ToggleSwitch(label);
        _toggle.IsCheckedChanged += (_, _) => onToggled();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, Design.Space.X3, 0, Design.Space.X3),
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(_toggle, 1);
        grid.Children.Add(text);
        grid.Children.Add(_toggle);

        Child = grid;
    }

    /// <summary>这一行说的是什么。界面上印的就是它，测试与读屏也按它找这一行。</summary>
    public string Label { get; }

    /// <summary>与 <see cref="ToggleButton.IsChecked"/> 同义 —— 设置页各页的字段读的就是它。</summary>
    public bool? IsChecked
    {
        get => _toggle.IsChecked;
        set => _toggle.IsChecked = value;
    }
}
