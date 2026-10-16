using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Andzip.App.Views;

/// <summary>
/// 列表行模板的<strong>唯一入口</strong>：把"Avalonia 会拿 <c>null</c> 再走一遍模板"这件事挡在这里。
/// <para><strong>为什么一定会走到 null</strong>（4.0.1，真机崩溃堆栈）：换 <c>ItemsSource</c> 时，
/// 掉出可视区的那些行要被回收，而回收容器的那一步会把行内容清空 ——
/// <c>VirtualizingStackPanel.RecycleElementOnItemRemoved</c> →
/// <c>ItemsControl.ClearContainerForItemOverride</c> →
/// <c>ContentPresenter.ContentChanged(null)</c> → <c>FuncDataTemplate.Build(null)</c>。
/// 也就是说模板函数<strong>注定</strong>会被 null 调一次，与数据里有没有 null 无关。</para>
/// <para>查找窗口就是这么整个崩掉的：它每敲一个字就换一次结果表，上一批多出来的行当场回收，
/// 于是 <c>Row(lang, null)</c> 抛 <c>NullReferenceException</c>，
/// 而这一下是从 <c>TextBox.TextChanged</c> 里冒出去的 —— 输入事件的路上没人接，进程就没了。
/// 主列表与「新建压缩文件」的输入表是同一形状（两个 <c>BuildRow</c> 也都直接解引用），
/// 只是它们换源的次数少，所以一直没撞上。</para>
/// <para>所以<strong>别再在调用点各写一句 <c>entry is null</c></strong>：三个列表都从这里过。
/// 无头用例量不到它 —— 样例清单只有 6 行，掉不出可视区就不会触发回收，
/// 这一条要量得拿几百行 + 一扇矮窗口（见 <c>docs/开发记录.md</c> 第七节第 83 条）。</para>
/// </summary>
internal static class RowTemplate
{
    /// <summary>包一层空值守卫：<c>null</c> 数据项给一块空面板占位，等真正有数据时再画行。</summary>
    public static FuncDataTemplate<T> Of<T>(Func<T, Control> build, bool recycle = false)
        where T : class
        => new((item, _) => item is null ? new Panel() : build(item), supportsRecycling: recycle);
}
