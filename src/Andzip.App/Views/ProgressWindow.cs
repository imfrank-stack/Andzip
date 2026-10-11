using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 进度窗口（FR-2.7 / FR-3）：确定进度条 + 当前条目 + 已处理体积 + 取消。
/// <para>
/// <see cref="Update"/> 由 <c>Progress&lt;ArchiveProgress&gt;</c> 投递，而 <c>Progress&lt;T&gt;</c>
/// 是在 UI 线程上构造的，所以它已经回到 UI 线程 —— 本类内部不再做任何编组。
/// </para>
/// <para>引擎报不出总量时（例如未知体积的流式压缩包）自动退回不确定进度条。</para>
/// <para>
/// <paramref name="cancelViaEsc"/> 对应「常规 → 解压过程中允许按 Esc 取消」：大文件解压到一半
/// 一次误触 Esc 就前功尽弃，所以这个开关得能关掉。
/// </para>
/// <para><paramref name="standalone"/> 用于「双击直接解压」那条路径 —— 它没有主窗口当宿主，
/// 因此要居中于屏幕并出现在任务栏上，否则用户看不到正在进行的解压。</para>
/// </summary>
public sealed class ProgressWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly string _textKey;
    private readonly TextBlock _text;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;
    private readonly CancellationTokenSource _cancellation;

    public ProgressWindow(
        LangCatalog lang,
        CancellationTokenSource cancellation,
        string textKey = "TEXT_STATUS_COMPRESSING",
        bool cancelViaEsc = true,
        bool standalone = false)
    {
        _lang = lang;
        _textKey = textKey;
        _cancellation = cancellation;
        Title = lang.Get(textKey);
        Width = 460;
        Height = 168;
        CanResize = false;
        ShowInTaskbar = standalone;
        WindowStartupLocation = standalone ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        FontSize = 14;

        _text = new TextBlock
        {
            Text = lang.Get(textKey),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 14,
            IsIndeterminate = true,
        };

        _cancel = new Button
        {
            Content = lang.Get("TEXT_BTN_CANCEL"),
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _cancel.Click += (_, _) => Cancel();

        if (cancelViaEsc)
        {
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Cancel();
                    e.Handled = true;
                }
            };
        }

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children = { _text, _bar, _cancel },
        };
    }

    /// <summary>取消当前操作。按钮点一次就禁用，避免重复取消把已结束的令牌再 Cancel 一次。</summary>
    public void Cancel()
    {
        _cancel.IsEnabled = false;
        _cancellation.Cancel();
    }

    /// <summary>刷新一帧进度。只能从 UI 线程调用（<c>Progress&lt;T&gt;</c> 已保证）。</summary>
    public void Update(ArchiveProgress progress)
    {
        var label = _lang.Get(_textKey);

        if (progress.Percent is { } percent)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = percent;
        }

        var line = label;
        if (progress.TotalBytes > 0)
        {
            line += $"  {SizeText.Format(progress.DoneBytes)} / {SizeText.Format(progress.TotalBytes)}";
        }
        else if (progress.DoneBytes > 0)
        {
            line += $"  {SizeText.Format(progress.DoneBytes)}";
        }

        if (progress.CurrentEntry is { Length: > 0 } entry)
        {
            line += $"  ·  {entry}";
        }

        _text.Text = line;
    }
}
