using System.Runtime.Versioning;
using Avalonia.Threading;

namespace Andzip.App.Views;

/// <summary>
/// 后台线程报进度、UI 线程改控件的那一层。
/// <para><strong>别换成裸 <c>Progress&lt;T&gt;</c></strong>：那个类捕获的是"构造那一刻"的
/// <c>SynchronizationContext</c>，环境里没人装过上下文时它会退回 .NET 默认实现 ——
/// 而默认实现的 <c>Post</c> 就是把回调丢进线程池，于是回调在池线程上直接改控件，
/// 真机抛 <c>Call from invalid thread</c>（0.9.11 的「测试压缩包」闪退、1.8.1 装不上都是这一类）。
/// 这里显式抓住 <see cref="Dispatcher.UIThread"/>，所以在哪儿构造、由哪个线程上报都无所谓。</para>
/// </summary>
/// <remarks>与 <c>ProgressWindow.CreateSink</c> 同一个道理；那个是私有的、只服务 <c>ArchiveProgress</c>。</remarks>
[SupportedOSPlatform("windows")]
internal sealed class UiProgress<T>(Action<T> onUi) : IProgress<T>
{
    public void Report(T value) => Dispatcher.UIThread.Post(() => onUi(value));
}
