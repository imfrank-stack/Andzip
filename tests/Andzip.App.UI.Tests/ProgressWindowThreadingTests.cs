using System.IO.Compression;
using System.Threading;
using Andzip.App.Views;
using Andzip.Core.Archive;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

namespace Andzip.App.UiTests;

/// <summary>
/// 进度上报的线程契约。0.9.11 上线「测试压缩包」后用户一点就闪退。
/// <para>
/// 事件日志里的栈是决定性的：回调经 <c>System.Threading.SynchronizationContext.Post</c>
/// 的<strong>默认实现</strong>（丢进线程池）到达 <c>ProgressWindow.Update</c>，于是池线程直接改
/// <c>ProgressBar</c>，Avalonia 抛 <c>Call from invalid thread</c>，进程没了。
/// 也就是说 <c>Progress&lt;T&gt;</c> 当时捕获到的不是 Avalonia 的上下文，而是它自己兜底 new 的那个。
/// </para>
/// <para>
/// 结论：不能把"回到 UI 线程"这件事寄托在环境上下文上。<see cref="ProgressWindow.CreateSink"/>
/// 显式走 <c>Dispatcher.UIThread</c>，所以下面第一条在<strong>清空环境上下文</strong>的工作线程里
/// 构造也照样回到 UI 线程 —— 那才是真机上发生的条件。
/// </para>
/// <para>
/// 另外记一笔工具本身的局限：<strong>headless 平台不检查线程亲和性</strong>，从池线程改控件在这里
/// 不会抛。所以这些断言问的都是"回调落在哪个线程"（<c>CheckAccess()</c>），不是"有没有炸"。
/// 0.9.11 那 14 条界面测试全绿而真机闪退，差的就是这一层。
/// </para>
/// </summary>
[Collection("界面状态")]
public class ProgressWindowThreadingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-progress-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ArchiveProgress Tick(string entry)
        => new(ArchiveOperation.Test, 512, 1024, entry, 0, 1);

    [AvaloniaFact]
    public void SinkReachesTheWindowThroughTheDispatcherEvenWithNoAmbientContext()
    {
        var window = new ProgressWindow(TestLang.Chinese(), new CancellationTokenSource()).Shown();
        var contextWasMissing = false;

        // 生产环境里就是这个形状：引擎回调来自池线程，环境上下文指望不上。
        Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            contextWasMissing = SynchronizationContext.Current is null;
            window.CreateSink().Report(Tick("a.txt"));
        }).Wait();

        // 此刻窗口还不该动 —— 上报只是被排进队列。真机上要是它当场就改了控件，
        // 那就是 0.9.11 的闪退。
        Assert.DoesNotContain(WindowTexts(window), text => Contains(text, "a.txt"));

        Dispatcher.UIThread.RunJobs();

        Assert.True(contextWasMissing, "这个用例的前提是环境里没有上下文；有就说明测的不是那条路径");
        Assert.Contains(WindowTexts(window), text => Contains(text, "a.txt"));
    }

    private static IEnumerable<string?> WindowTexts(Window window)
        => VisualQuery.Of<TextBlock>(window).Select(t => t.Text);

    private static bool Contains(string? text, string needle)
        => text is not null && text.Contains(needle, StringComparison.Ordinal);

    // 曾经还有一条"裸 Progress<T> 在没有上下文时会投递到工作线程"的反面对照。删掉了：
    // 那条投递是异步的，断言只能靠轮询等它跑完，是个会随机变红的测试。
    // 那个机制本身已经写进 ProgressWindow.CreateSink 的注释，事件日志里的栈就是证据。

    [AvaloniaFact]
    public void RealIntegrityTestRunsBehindAProgressWindow()
    {
        // 端到端：真包、真引擎、真进度窗口、后台线程跑测试。
        var archive = BuildArchive();
        using var cts = new CancellationTokenSource();
        var window = new ProgressWindow(TestLang.Chinese(), cts).Shown();
        var sink = window.CreateSink();

        var escaped = Record.Exception(() =>
        {
            Task.Run(() => ArchiveTester.Test(archive, password: null, sink, cts.Token)).Wait();
            Dispatcher.UIThread.RunJobs();
        });

        Assert.Null(escaped);
    }

    /// <summary>
    /// 进度窗口没有「取消」按钮了（1.9.2）：那颗按钮被窗口下边界切掉一半，用户的处置是删掉它。
    /// 这里钉三件事：按钮确实不在、叉号真的取消操作、调用方随后再 <c>Close()</c> 一次不炸
    /// （<c>DirectExtract</c> / <c>DirectCompress</c> 的 <c>finally</c> 就是这么写的）。
    /// </summary>
    [AvaloniaFact]
    public void TheCrossCancelsTheJobAndThereIsNoCancelButton()
    {
        var lang = TestLang.Chinese();
        using var cts = new CancellationTokenSource();
        var window = new ProgressWindow(lang, cts, "TEXT_STATUS_TESTING").Shown();

        Assert.Equal(lang.Get("TEXT_STATUS_TESTING"), window.Title);
        Assert.DoesNotContain(VisualQuery.Of<Button>(window),
            b => Equals(VisualQuery.TextOf(b), lang.Get("TEXT_BTN_CANCEL")));

        Clicker.Invoke(CrossOf(window));

        Assert.True(cts.IsCancellationRequested, "右上角的叉号必须取消这件事，不是只把框藏起来");
        Assert.False(window.IsVisible, "取消之后这个框就该没了");

        var twice = Record.Exception(() => window.Close());
        Assert.Null(twice);
    }

    /// <summary>那项设置只管键盘；叉号不受它管。</summary>
    [AvaloniaFact]
    public void EscFollowsTheSettingWhileTheCrossAlwaysCancels()
    {
        var lang = TestLang.Chinese();
        using var allowed = new CancellationTokenSource();
        using var guarded = new CancellationTokenSource();
        var withEsc = new ProgressWindow(lang, allowed, "TEXT_STATUS_EXTRACTING").Shown();
        var withoutEsc = new ProgressWindow(lang, guarded, "TEXT_STATUS_EXTRACTING", cancelViaEsc: false).Shown();

        PressEscape(withEsc);
        PressEscape(withoutEsc);

        Assert.True(allowed.IsCancellationRequested, "开关开着时 Esc 必须能取消（那项设置的本来用途）");
        Assert.False(guarded.IsCancellationRequested, "开关关掉后 Esc 不该动正在跑的活儿 —— 误按会把大文件前功尽弃");

        Clicker.Invoke(CrossOf(withoutEsc));
        Assert.True(guarded.IsCancellationRequested, "Esc 被关掉的那扇窗，叉号照样得是「取消这件事」");
    }

    private static Button CrossOf(Window window) => VisualQuery.Of<Button>(window).First(b =>
        Avalonia.Automation.AutomationProperties.GetName(b).Contains("关闭", StringComparison.Ordinal));

    private static void PressEscape(Window window) => window.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = Key.Escape,
        Source = window,
    });

    private string BuildArchive()
    {
        var archive = Path.Combine(_root, "box.zip");
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("noise.bin");
        using var stream = entry.Open();
        stream.Write(new byte[400_000]);
        return archive;
    }
}
