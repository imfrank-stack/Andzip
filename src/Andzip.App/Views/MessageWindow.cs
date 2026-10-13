using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 简单消息框（错误 / 提示）：标题 + 正文 + 确定。
/// <para>带 <c>cancelKey</c> 时它是两钮确认框 —— 删包这类不可撤销的动作必须用这个形式，
/// 只有一个「确定」的框等于没有给退路。</para>
/// </summary>
public sealed class MessageWindow : Window
{
    public MessageWindow(LangCatalog lang, string title, string message)
        : this(lang, title, message, "TEXT_BTN_OK", cancelKey: null)
    {
    }

    /// <summary>
    /// <c>cancelKey</c> 为 null 是单钮提示框，给了就是两钮确认框。
    /// <para>internal 而不是 private：这一层的"哪个按钮回 true"是删除类操作唯一的安全网，
    /// 得能在测试里真的按一次按钮看到结果，而不只是相信 <see cref="ConfirmAsync"/> 传对了参数。</para>
    /// </summary>
    internal MessageWindow(LangCatalog lang, string title, string message, string okKey, string? cancelKey)
    {
        // 标题与正文由调用方从语言包取好传进来，拿到的是原文（可能带 (&X)），
        // 所以在这个对话框的边界上统一剥一次，而不是要求每个调用点记得剥。
        Title = MnemonicText.Strip(title);
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Design.Surface.Base;
        FontFamily = Design.Font;
        FontSize = Design.Type.Body;

        // 错误与危险动作带一个图标：颜色之外再多一条通道，色弱用户也读得出"这条不是普通提示"
        // （规范第八节：不只靠颜色）。
        var dangerous = okKey == "TEXT_BTN_DELETE";
        var text = new TextBlock
        {
            Text = MnemonicText.Strip(message),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Design.Ink.Primary,
            LineHeight = Design.Type.BodyLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var body = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X3,
            Children = { Badge(dangerous), text },
        };

        var okButton = new Button
        {
            Content = lang.Label(okKey),
            IsDefault = true,
            // 只有单钮框才让 Esc 也算确认；两钮框里 Esc 必须是"取消"。
            IsCancel = cancelKey is null,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        okButton.Click += (_, _) => Close(true);
        if (dangerous)
        {
            okButton.Danger();
        }
        else
        {
            okButton.Accent();
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X3,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(okButton);

        if (cancelKey is not null)
        {
            var cancelButton = new Button
            {
                Content = lang.Label(cancelKey),
                IsCancel = true,
                MinWidth = 96,
            }.Ghost();
            cancelButton.Click += (_, _) => Close(false);
            buttons.Children.Add(cancelButton);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(Design.Space.X6),
            Spacing = Design.Space.X6,
            Children = { body, buttons },
        };
    }

    /// <summary>提示框左侧那枚圆形图标底。叫 Badge 而不是 Icon —— <c>Window.Icon</c> 已经被占了。</summary>
    private static Control Badge(bool dangerous) => new Border
    {
        Width = 40,
        Height = 40,
        CornerRadius = Design.Radius.Pill,
        Background = dangerous ? Design.Semantic.DangerTint : Design.Brand.Tint,
        Child = new Panel
        {
            Children =
            {
                Icons.Line(
                    dangerous ? Icons.Warning : Icons.Info,
                    Design.IconSize.Dialog,
                    dangerous ? Design.Semantic.Danger : Design.Brand.Base),
            },
        },
    };

    /// <summary>
    /// 两钮确认框：确定 → true，取消或关掉窗口 → false。
    /// <para>默认按钮是"取消"那一侧的风险由调用方承担 —— 所以 <c>okKey</c> 要传一个
    /// 说清楚后果的词（「删除」而不是「确定」）。</para>
    /// </summary>
    public static Task<bool> ConfirmAsync(
        Window owner,
        LangCatalog lang,
        string title,
        string message,
        string okKey)
        => new MessageWindow(lang, title, message, okKey, "TEXT_BTN_CANCEL").ShowDialog<bool>(owner);
}
