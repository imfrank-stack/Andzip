using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;

// 块式命名空间而不是 file-scoped：assembly 特性和 file-scoped namespace 放一起，
// 编译器会把这行当成顶级语句（CS8956/CS8937），报的还不是真正的原因。
[assembly: AvaloniaTestApplication(typeof(Andzip.App.UiTests.TestAppBuilder))]

namespace Andzip.App.UiTests
{
    /// <summary>用被测应用自己的 Application 类型：主题、资源、控件样式全都按真实配置走。
    /// 测试专用近似物会让"布局正确"这个结论失去意义。</summary>
    public static class TestAppBuilder
    {
        // global:: 是必要的：在这个命名空间里裸写 App 会先撞上 Andzip.App 这个命名空间本身。
        // UseSkia + UseHeadlessDrawing=false：不然 CaptureRenderedFrame 拿不到画面，
        // 截图测试就退化成"控件存在"而已。
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<global::Andzip.App.App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    /// <summary>headless 下窗口要 Show 一次并量一遍布局，模板才会应用、控件才会真正建立起来；
    /// 只 new 出来的窗口是个空壳。</summary>
    internal static class Presented
    {
        public static T Shown<T>(this T window) where T : Window
        {
            window.Show();

            var width = double.IsFinite(window.Width) ? window.Width : 1200;
            var height = double.IsFinite(window.Height) ? window.Height : 900;
            var size = new Size(width, height);

            window.Measure(size);
            window.Arrange(new Rect(size));

            return window;
        }
    }

    internal static class TestLang
    {
        /// <summary>读的是仓库里真实的中英文包，不是内存里的替身 ——
        /// 键缺失、文案为空、串了语言，都该在这里暴露。</summary>
        public static LangCatalog Chinese() => Load("SimpChinese");

        public static LangCatalog English() => Load("English");

        private static LangCatalog Load(string language)
            => LangLoader.CreateCatalog(typeof(TestLang).Assembly, RepositoryRoot(), language);

        /// <summary>加载器要的是<strong>含有</strong> langs/ 的那一层，不是 langs/ 本身。</summary>
        private static string RepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "langs")))
                {
                    return directory.FullName;
                }
            }

            throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 往上找不到 langs/ 目录。");
        }
    }

    /// <summary>把控件树摊平按类型 / 文案查找。走<strong>逻辑树</strong>：视觉子节点要等模板应用之后
    /// 才存在，而这里要找的都是代码里显式 new 出来的控件（按钮、勾选框、单选钮），
    /// 它们一进集合就是逻辑子节点。</summary>
    internal static class VisualQuery
    {
        public static IReadOnlyList<T> Of<T>(Control root) where T : Control
            => Descendants(root).OfType<T>().ToList();

        /// <summary>按可见文案找控件：文案是用户唯一的定位依据。</summary>
        public static IReadOnlyList<Control> Titled(Control root, string title)
            => Descendants(root).Where(c => ContentText(c) == title).ToList();

        /// <summary>这棵树上会显示给用户的全部文案。用来问"用户到底看得见什么"，
        /// 而不是"某个键在不在"。</summary>
        public static IReadOnlyList<string> VisibleTexts(Control root)
            => [.. Descendants(root).Select(ContentText).Where(text => !string.IsNullOrEmpty(text)).Select(text => text!)];

        private static IEnumerable<Control> Descendants(Control control)
        {
            foreach (var child in control.GetLogicalChildren().OfType<Control>())
            {
                yield return child;

                foreach (var nested in Descendants(child))
                {
                    yield return nested;
                }
            }
        }

        // Avalonia 里 RadioButton / CheckBox 都派生自 Button，顺序反了后两个 arm 永远走不到
        // （编译器直接报 CS8510），所以先问细分类型。
        private static string? ContentText(Control control) => control switch
        {
            MenuItem item => item.Header?.ToString(),
            RadioButton radio => radio.Content?.ToString(),
            CheckBox check => check.Content?.ToString(),
            Button button => button.Content?.ToString(),
            TextBlock text => text.Text,
            _ => null,
        };
    }

    /// <summary>沿 Menu.Items 递归取菜单项。不能走控件树：子菜单要等弹出才有可视节点，
    /// 而"这一项存不存在、能不能点"是数据结构层面的事实。</summary>
    internal static class MenuWalk
    {
        public static IReadOnlyList<MenuItem> All(ItemsControl root)
        {
            var found = new List<MenuItem>();
            Collect(root);
            return found;

            void Collect(ItemsControl control)
            {
                foreach (var item in control.Items.OfType<MenuItem>())
                {
                    found.Add(item);
                    Collect(item);
                }
            }
        }
    }

    /// <summary>
    /// 在 UI 线程上等一个<strong>会回到 UI 线程</strong>的异步方法。
    /// AvaloniaFact 的测试体本身就跑在 Dispatcher 线程上，所以 <c>task.Wait()</c> 会把
    /// await 之后的续体一起堵在队列外面 —— 死锁到超时，而现象长得像"功能压根没生效"。
    /// 这里反过来反复 <c>RunJobs</c>，给续体机会跑完。
    /// </summary>
    internal static class UiWait
    {
        public static void Until(Task task, string what, int timeoutMilliseconds = 10_000)
        {
            var deadline = Environment.TickCount64 + timeoutMilliseconds;

            while (!task.IsCompleted && Environment.TickCount64 < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            if (!task.IsCompleted)
            {
                throw new TimeoutException($"{what}没在 {timeoutMilliseconds} 毫秒内跑完（多半是 UI 线程被自己等死了）。");
            }

            if (task.IsFaulted)
            {
                throw new AggregateException($"{what}失败了。", task.Exception!);
            }
        }

        /// <summary>同上，但要拿回结果 —— 对话框的 ShowDialog&lt;bool&gt; 属于"点了哪个按钮"这类事实。</summary>
        public static T Until<T>(Task<T> task, string what, int timeoutMilliseconds = 10_000)
        {
            Until((Task)task, what, timeoutMilliseconds);
            return task.Result;
        }

        /// <summary>什么都不等，只让 Dispatcher 把队列里的活儿干一会儿。
        /// 窗口是异步呈现的：刚 ShowDialog 之后马上去找按钮，控件树还没建起来。</summary>
        public static void Pump(int milliseconds = 50)
        {
            var deadline = Environment.TickCount64 + milliseconds;

            while (Environment.TickCount64 < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>点击一个按钮。走事件层而不是指针层：这些处理程序本来就挂在 Click 上。
    /// 两点代价要写清楚：一是禁用态也会被点燃，所以这里先断言 IsEnabled；二是<strong>它不会
    /// 改变控件自己的状态</strong> —— Avalonia 的 Button.OnClick 由指针管线调用，RaiseEvent 只触发
    /// 外部处理程序，所以点 RadioButton / CheckBox 得直接设 IsChecked，否则测的是个没反应的空点击。</summary>
    internal static class Clicker
    {
        public static void Invoke(Button button)
        {
            Assert.True(button.IsEnabled, $"「{button.Content}」这时应该是不可点的");
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        }

        /// <summary>
        /// 点菜单项。走 <c>RaiseEvent</c> 而不是 <c>MenuItem.Click</c> 的内部实现：
        /// 我们要触发的是挂在 <c>Click</c> 上的处理器，而菜单项没有"按下弹起"那套状态要维护
        /// （那正是 <see cref="Invoke(Button)"/> 对 RadioButton 不管用的原因，这里没这个问题）。
        /// </summary>
        public static void Invoke(MenuItem item)
        {
            Assert.True(item.IsEnabled, $"「{item.Header}」这时应该是不可点的");
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        }
    }
}
