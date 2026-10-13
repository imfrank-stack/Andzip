using System.Diagnostics;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Andzip.App.Views;

/// <summary>
/// 进度窗口（FR-2.7 / FR-3）：确定进度条 + 百分比 + 已处理体积 + 预估剩余时间 + 当前条目 + 取消。
/// <para>
/// <see cref="Update"/> 只能从 UI 线程调用；交给 Core 的那条路一律用 <see cref="CreateSink"/>，
/// 它显式走 Dispatcher，不要换成裸 <c>Progress&lt;T&gt;</c>（原因写在那里）。
/// </para>
/// <para>引擎报不出总量时（例如未知体积的流式压缩包）自动退回不确定进度条，也不报剩余时间。</para>
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

    /// <summary>
    /// 从<strong>第一帧进度</strong>开始计时，不是从窗口构造开始。
    /// 构造到真正搬字节之间可能隔着一次口令询问 —— 把用户想口令的那几十秒算进速度里，
    /// 剩余时间会估成一个荒谬的大数。
    /// </summary>
    private readonly Stopwatch _clock = new();

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
        Title = lang.Label(textKey);
        Width = 520;
        Height = 168;
        CanResize = false;
        ShowInTaskbar = standalone;
        WindowStartupLocation = standalone ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        this.WindowSurface();

        _text = new TextBlock
        {
            Text = lang.Label(textKey),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            CornerRadius = Design.Radius.Pill,
            IsIndeterminate = true,
        };

        _cancel = new Button
        {
            Content = lang.Label("TEXT_BTN_CANCEL"),
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
        }.Ghost();
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

        this.Chromeless(lang, new StackPanel
        {
            Margin = new Thickness(Design.Space.X6),
            Spacing = Design.Space.X4,
            Children = { _text, _bar, _cancel },
        });
    }

    /// <summary>取消当前操作。按钮点一次就禁用，避免重复取消把已结束的令牌再 Cancel 一次。</summary>
    public void Cancel()
    {
        _cancel.IsEnabled = false;
        _cancellation.Cancel();
    }

    /// <summary>刷新一帧进度。只能从 UI 线程调用 —— 用 <see cref="CreateSink"/> 就自动满足。</summary>
    public void Update(ArchiveProgress progress)
    {
        if (!_clock.IsRunning)
        {
            _clock.Start();
        }

        var label = _lang.Label(_textKey);

        if (progress.Percent is { } percent)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = percent;
        }

        var line = label;

        if (progress.Percent is { } shown)
        {
            line += $"  {shown}%";
        }

        if (progress.TotalBytes > 0)
        {
            line += $"  {SizeText.Format(progress.DoneBytes)} / {SizeText.Format(progress.TotalBytes)}";
        }
        else if (progress.DoneBytes > 0)
        {
            line += $"  {SizeText.Format(progress.DoneBytes)}";
        }

        if (EstimateRemaining(progress) is { } left)
        {
            line += $"  ·  {_lang.Format("TEXT_PROGRESS_TIME_LEFT", ("time", left))}";
        }

        if (progress.CurrentEntry is { Length: > 0 } entry)
        {
            line += $"  ·  {entry}";
        }

        _text.Text = line;
    }

    /// <summary>
    /// 用「已用时间 / 已完成量」线性外推剩余时间，报成 <c>分:秒</c> 或 <c>时:分:秒</c>。
    /// <para>
    /// 刻意不用单位（秒 / 分 / 小时）：那要为一套时长文案做本地化，而 <c>03:12</c> 这种写法
    /// 不需要翻译，也和参考软件一致。
    /// </para>
    /// <para>
    /// 前 3% 不报：刚起步时样本太小，速度抖一下估算就能从 2 分钟跳到 40 分钟，
    /// 一个乱跳的数字比没有数字更糟。没有总量（流式包）或已经跑完时同样不报。
    /// </para>
    /// </summary>
    private string? EstimateRemaining(ArchiveProgress progress)
    {
        if (progress.TotalBytes <= 0 || progress.DoneBytes <= 0 || progress.DoneBytes >= progress.TotalBytes)
        {
            return null;
        }

        if (progress.Percent < 3)
        {
            return null;
        }

        var secondsPerByte = _clock.Elapsed.TotalSeconds / progress.DoneBytes;
        var remaining = TimeSpan.FromSeconds(secondsPerByte * (progress.TotalBytes - progress.DoneBytes));

        // 估出一天以上说明这帧数据不可信（多半是总量算错了），宁可不报。
        return remaining.TotalDays >= 1 ? null : Format(remaining);
    }

    private static string Format(TimeSpan span)
        => span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";

    /// <summary>
    /// 交给 Core 的进度汇。<strong>别自己 <c>new Progress&lt;T&gt;(window.Update)</c></strong>：
    /// 那个类捕获的是"构造那一刻"的 <c>SynchronizationContext</c>，环境里没人装过上下文时它会退回
    /// .NET 默认实现 —— 而默认实现的 <c>Post</c> 就是把回调丢进线程池，于是回调在池线程上直接改控件，
    /// 真机抛 <c>Call from invalid thread</c> 整个进程没了（0.9.11 的「测试压缩包」闪退就是这个）。
    /// 这里显式抓住 <c>Dispatcher.UIThread</c>，所以在哪儿构造、由哪个线程上报都无所谓。
    /// </summary>
    public IProgress<ArchiveProgress> CreateSink() => new DispatcherSink(this);

    private sealed class DispatcherSink(ProgressWindow window) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value) => Dispatcher.UIThread.Post(() => window.Update(value));
    }
}
