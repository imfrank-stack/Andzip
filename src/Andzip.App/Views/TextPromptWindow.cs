using System.Text;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 一个「一个文本框 + 确定 / 取消」的对话框。两处用它：<c>编辑 → 重命名</c>（单行，预填原名字）
/// 与 <c>编辑 → 压缩文件注释</c>（多行，带字节计数与上限）。
/// <para>为什么合成一个而不是各写一份：两者的差别只有"几行"和"有没有上限"，
/// 而窗口外壳（无边框圆角面板、按钮那两档样式、错误提示那一行）每次重写一遍就会各自长歪 ——
/// 本项目已经因为"同一件事写两遍"摔过几次（见开发记录的菜单不变式与九个对话框那条）。</para>
/// </summary>
public sealed class TextPromptWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly TextBox _box;
    private readonly TextBlock _hint;
    private readonly TextBlock? _counter;
    private readonly bool _allowEmpty;
    private readonly int _maxBytes;
    private readonly Func<string, string?>? _validate;

    /// <summary>确认后的文字。仅在 <c>ShowDialog&lt;bool&gt;</c> 返回 true 后有效。</summary>
    public string Text { get; private set; } = string.Empty;

    /// <param name="hintKey">说明这一格是什么用的那一行（可以没有）。</param>
    /// <param name="multiline">注释那种要换行的用它；重命名留 false。</param>
    /// <param name="allowEmpty">注释允许留空（留空 = 删掉注释）；重命名不允许。</param>
    /// <param name="maxBytes">大于 0 时显示字节计数，并在超出时当场拒绝。</param>
    /// <param name="validate">额外的检查（例如"这个包里已经有同名的项了"），返回一句要显示的话或 null。</param>
    public TextPromptWindow(
        LangCatalog lang,
        string titleKey,
        string labelKey,
        string? initial = null,
        string? hintKey = null,
        bool multiline = false,
        bool allowEmpty = false,
        int maxBytes = 0,
        Func<string, string?>? validate = null)
    {
        _lang = lang;
        _allowEmpty = allowEmpty;
        _maxBytes = maxBytes;
        _validate = validate;

        Title = lang.Label(titleKey);
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.WindowSurface();

        var label = new TextBlock
        {
            Text = lang.Label(labelKey),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };

        _box = new TextBox
        {
            Text = initial ?? string.Empty,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 120 : 0,
        };
        _box.TextChanged += (_, _) => RefreshCounter();

        // 单行那一处（改名字）敲回车就是「确定」。
        // <para>不能只靠 <c>IsDefault</c>，也不能用 <c>_box.KeyDown += …</c>：
        // <c>TextBox</c> 自己的类处理器先把回车标成已处理，实例上的气泡处理器于是**不被调用**
        // （真机按下去什么都不发生；无头实测同一个回车：输入框上隧道 1 次、气泡 0 次）。
        // 所以注册成**隧道 + handledEventsToo**，先于任何人拿到这一下。
        // 多行那一处（写注释）故意不接：那里回车是换行。</para>
        _box.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) =>
            {
                if (!multiline && e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Confirm();
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        _hint = new TextBlock
        {
            Foreground = Design.Semantic.Danger,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _counter = maxBytes > 0
            ? new TextBlock
            {
                Foreground = Design.Ink.Secondary,
                FontSize = Design.Type.Caption,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0),
            }
            : null;

        var okButton = new Button { Content = lang.Label("TEXT_BTN_OK"), IsDefault = true, MinWidth = 96 }.Accent();
        okButton.Click += (_, _) => Confirm();
        var cancelButton = new Button { Content = lang.Label("TEXT_BTN_CANCEL"), IsCancel = true, MinWidth = 96 }.Ghost();
        cancelButton.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, Design.Space.X3, 0, 0),
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(label);
        stack.Children.Add(_box);

        if (hintKey is { } key)
        {
            stack.Children.Add(new TextBlock
            {
                Text = lang.Label(key),
                Foreground = Design.Ink.Secondary,
                FontSize = Design.Type.Caption,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }

        if (_counter is not null)
        {
            stack.Children.Add(_counter);
        }

        stack.Children.Add(_hint);
        stack.Children.Add(buttons);

        this.Chromeless(lang, stack);
        RefreshCounter();

        Opened += (_, _) =>
        {
            _box.Focus();

            // 单行那一处（改名字）把原文全选中：用户要么直接改，要么整段换掉，
            // 让光标停在名字中间反而要多按几次键。多行那处（写注释）不选 —— 那是要看内容再补一句的地方。
            if (!multiline)
            {
                _box.SelectAll();
            }
        };
    }

    private string CurrentText => _box.Text ?? string.Empty;

    /// <summary>字节数按 UTF-8 算：ZIP 的注释长度是<strong>字节</strong>，
    /// 而中文一个字通常占 3 个字节 —— 按字符数算会让用户在四万多字处突然写不进去。</summary>
    private int CurrentBytes => Encoding.UTF8.GetByteCount(CurrentText);

    private void RefreshCounter()
    {
        if (_counter is null)
        {
            return;
        }

        _counter.Text = _lang.Format("TEXT_COMMENT_COUNTER", ("bytes", CurrentBytes.ToString()));
        _counter.Foreground = CurrentBytes > _maxBytes ? Design.Semantic.Danger : Design.Ink.Secondary;
    }

    private void Confirm()
    {
        var text = CurrentText;

        if (!_allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            ShowError(_lang.Label("TEXT_ERR_RENAME_EMPTY"));
            return;
        }

        if (_maxBytes > 0 && CurrentBytes > _maxBytes)
        {
            ShowError(_lang.Label("TEXT_ERR_COMMENT_TOO_LONG"));
            return;
        }

        if (_validate?.Invoke(text) is { } problem)
        {
            ShowError(problem);
            return;
        }

        Text = text;
        Close(true);
    }

    private void ShowError(string message)
    {
        _hint.Text = message;
        _hint.IsVisible = true;
    }
}
