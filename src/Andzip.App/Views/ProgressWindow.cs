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
/// 进度窗口（FR-2.7 / FR-3）：确定进度条 + 百分比 + 已处理体积 + 预估剩余时间 + 当前条目。
/// <para>
/// <see cref="Update"/> 只能从 UI 线程调用；交给 Core 的那条路一律用 <see cref="CreateSink"/>，
/// 它显式走 Dispatcher，不要换成裸 <c>Progress&lt;T&gt;</c>（原因写在那里）。
/// </para>
/// <para>引擎报不出总量时（例如未知体积的流式压缩包）自动退回不确定进度条，也不报剩余时间。</para>
/// <para><strong>取消没有按钮</strong>（1.9.2）：Esc 与右上角那枚叉号就是取消。
/// 原来右下角那颗「取消」被窗口下边界切掉一半（高度按"标题栏 + 一行字 + 进度条 + 按钮"排，
/// 而 168 装不下），用户的反馈是"有遮挡，删掉它"。叉号现在走 <c>onClose: Cancel</c> ——
/// 它<strong>取消这件事并关窗</strong>，不是把框藏起来让后台继续搬字节。</para>
/// <para>
/// <paramref name="cancelViaEsc"/> 对应「常规 → 解压过程中允许按 Esc 取消」：大文件解压到一半
/// 一次误触 Esc 就前功尽弃，所以这个开关得能关掉（叉号不受它管，永远能取消）。
/// </para>
/// <para><paramref name="standalone"/> 用于「双击直接解压」那条路径 —— 它没有主窗口当宿主，
/// 因此要居中于屏幕并出现在任务栏上，否则用户看不到正在进行的解压。</para>
/// </summary>
public sealed class ProgressWindow : Window
{
    private readonly LangCatalog _lang;
    private string _textKey;
    private readonly TextBlock _text;
    private readonly ProgressBar _bar;
    private readonly CancellationTokenSource _cancellation;

    /// <summary>按过一次就不再来：重复 Cancel 会把已结束的令牌再取消一次。</summary>
    private bool _cancelled;

    /// <summary>
    /// 这件事已经跑完，只是这一屏按「完成后不关闭进度窗口」留着。
    /// <para>立起这一枚之后 <see cref="Update"/> 不再画任何东西 —— 晚到的一帧会把
    /// 「解压完成」说回「正在解压」，而 <see cref="Cancel"/> 也不再碰那个令牌。</para>
    /// </summary>
    private bool _finished;

    /// <summary>最后一帧进度，<see cref="Finish"/> 用它把"解了多少"这半句留在屏幕上。</summary>
    private ArchiveProgress? _last;

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
        // 高度不写死：这颗框的内容是"一行进度说明 + 一根进度条"，写死过一次 168，
        // 那时下面还有一颗「取消」按钮 —— 装不下，按钮被卡片的下边界切掉一半，
        // 用户的说法是"这个进度条有遮挡"。让窗口跟着内容长，这类"改一处内容就切一刀"从此不可能。
        SizeToContent = SizeToContent.Height;
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
            Children = { _text, _bar },
        }, onClose: Cancel);
    }

    /// <summary>取消当前操作并收掉这个框。Esc 与右上角的叉号走的都是这里。</summary>
    public void Cancel()
    {
        if (_cancelled)
        {
            return;
        }

        _cancelled = true;

        // 跑完之后这一屏只是留着给人看结果：这时候的 X 与 Esc 关窗就行，
        // 再去碰那个已经结束的令牌只是让"取消"这个词说谎（它什么也取消不了）。
        if (!_finished)
        {
            _cancellation.Cancel();
        }

        Close();
    }

    /// <summary>
    /// 这件事跑完了，而这一屏要留着（「设置 → 解压 / 压缩 → 完成后不关闭进度窗口」）。
    /// <para>
    /// <strong>为什么必须有这一句</strong>：一根满的蓝条配一句「正在解压…」读起来是"卡住了"，
    /// 不是"完事了" —— 用户报的「进度条不消失」就是这个。开着这一档的人要的是"留着让我
    /// 看一眼跑了多少"，那就得把话说成完成，而不是把"正在"两个字永远停在屏幕上。
    /// </para>
    /// </summary>
    /// <param name="doneKey">说"…完成"的那条文案（解压与压缩各一条，不能共用一句"正在"的）。</param>
    public void Finish(string doneKey, long? outputBytes = null)
    {
        _finished = true;
        _textKey = doneKey;
        _bar.IsIndeterminate = false;
        _bar.Value = 100;

        var label = _lang.Label(doneKey);
        var line = $"{label}  100%";

        if (_last is { TotalBytes: > 0 } sized)
        {
            line += $"  {SizeText.Format(sized.DoneBytes)} / {SizeText.Format(sized.TotalBytes)}";
        }
        else if (_last is { DoneBytes: > 0 } unsized)
        {
            line += $"  {SizeText.Format(unsized.DoneBytes)}";
        }

        // 跑完了就不猜了：这两句是实测。输出大小只有压缩给得出（解压的"输出"是散开的
        // 一堆文件），所以它是可选参数，调用方没有就不说。
        if (outputBytes is { } written)
        {
            line += $"  ·  {_lang.Format("TEXT_PROGRESS_OUTPUT_SIZE", ("size", SizeText.Format(written)))}";
        }

        if (MeasuredSpeed() is { } average)
        {
            line += $"  ·  {_lang.Format("TEXT_PROGRESS_AVG_SPEED", ("speed", average))}";
        }

        Title = label;
        _text.Text = line;
    }

    /// <summary>
    /// 换掉"这一屏正在做哪件事"的说法。
    /// <para>修复完接着复查修好的包，进度框还写着「正在修复」就是在说假话 —— 而这件事值得
    /// 用同一个框继续报（新开一框会变成"点了两次才关掉两个框"）。下一帧进度一到，
    /// 标题与正文都会跟着换，所以这里只改键，不碰控件。</para>
    /// </summary>
    public void NextPhase(string textKey) => _textKey = textKey;

    /// <summary>刷新一帧进度。只能从 UI 线程调用 —— 用 <see cref="CreateSink"/> 就自动满足。</summary>
    public void Update(ArchiveProgress progress)
    {
        // 已经说过"完成了"，就别让晚到的那一帧把这句话改回"正在…"。
        // （交给 Core 的那条汇是先进先出的投递，正常情况下最后一帧本来就排在 Finish 之前；
        // 这里挡的是以后那种"跑完之后再报一次"的实现改动。）
        if (_finished)
        {
            return;
        }

        _last = progress;

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

        if (MeasuredSpeed() is { } speed)
        {
            line += $"  ·  {_lang.Format("TEXT_PROGRESS_SPEED", ("speed", speed))}";
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
        return remaining.TotalDays >= 1 ? null : DurationText.Format(remaining);
    }

    /// <summary>
    /// 实测吞吐：<strong>已处理的未压缩字节 / 已用时间</strong>，与 7-Zip 命令行那个
    /// <c>Speed</c> 同一个口径 —— 算的是"读进来压掉了多少"，不是"写出了多少"，
    /// 所以压缩比再高也不会把这个数说得忽快忽慢。
    /// <para>
    /// 起步半秒内不报：那会儿分母还是噪声，一个先跳一下再稳住的数字比没有数字更糟
    /// （和上面剩余时间"前 3% 不报"是同一条考虑）。跑完之后同一条式子就是平均速度。
    /// </para>
    /// </summary>
    private string? MeasuredSpeed()
    {
        if (_last is not { DoneBytes: > 0 } progress)
        {
            return null;
        }

        var seconds = _clock.Elapsed.TotalSeconds;
        return seconds < 0.5
            ? null
            : SizeText.Format((long)(progress.DoneBytes / seconds)) + "/s";
    }

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
