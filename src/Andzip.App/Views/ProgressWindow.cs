using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 进度窗口（FR-2.7 / FR-3）：确定进度条 + 当前条目 + 已处理体积 + 取消。
/// <para>
/// <see cref="Update"/> 由 <c>Progress&lt;ArchiveProgress&gt;</c> 投递，而 <c>Progress&lt;T&gt;</c>
/// 是在 UI 线程上构造的，所以它已经回到 UI 线程 —— 本类内部不再做任何编组。
/// </para>
/// <para>引擎报不出总量时（例如未知体积的流式压缩包）自动退回不确定进度条。</para>
/// </summary>
public sealed class ProgressWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly string _textKey;
    private readonly TextBlock _text;
    private readonly ProgressBar _bar;

    public ProgressWindow(LangCatalog lang, CancellationTokenSource cancellation, string textKey = "TEXT_STATUS_COMPRESSING")
    {
        _lang = lang;
        _textKey = textKey;
        Title = lang.Get(textKey);
        Width = 460;
        Height = 168;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
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

        var cancel = new Button
        {
            Content = lang.Get("TEXT_BTN_CANCEL"),
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        cancel.Click += (_, _) =>
        {
            cancel.IsEnabled = false;
            cancellation.Cancel();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children = { _text, _bar, cancel },
        };
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
