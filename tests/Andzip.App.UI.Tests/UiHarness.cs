using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using System.Runtime.CompilerServices;

// 块式命名空间而不是 file-scoped：assembly 特性和 file-scoped namespace 放一起，
// 编译器会把这行当成顶级语句（CS8956/CS8937），报的还不是真正的原因。
[assembly: AvaloniaTestApplication(typeof(Andzip.App.UiTests.TestAppBuilder))]

namespace Andzip.App.UiTests
{
    /// <summary>
    /// 整个测试程序集一上来就把"设置文件在哪"换到自己的临时目录。
    /// <para>这不是整理，是止血：<c>MainWindow</c> 打开一个包就往 <c>recent_files</c> 记一笔并落盘到
    /// <see cref="Andzip.Core.Settings.AppSettings.DefaultFilePath"/>，而那是这台机器真的
    /// <c>%APPDATA%\Andzip\settings.ini</c> —— 2026-10-16 跑完整套门禁之后，用户那份文件里的
    /// "最近的文件"变成了一个测试临时目录里的 <c>box.zip</c>。逐条用例传自己的路径要改二十处，
    /// 漏一处就回到原样；换掉这一个口子，测试进程里没有任何一条路写得到用户的文件。</para>
    /// <para>用模块初始化器而不是 fixture：fixture 要每条用例都挂上去，而"漏挂"正是这里的失败模式。
    /// 换到固定名字（不是随机）的目录，为的是崩掉的上一轮留下的文件会被下一轮接着用，
    /// 而不是每次跑完都在 %TEMP% 里多一个没人收的目录。</para>
    /// </summary>
    internal static class TestSettings
    {
        [ModuleInitializer]
        internal static void Redirect() => global::Andzip.Core.Settings.AppSettings.RedirectForTests(
            Path.Combine(Path.GetTempPath(), "andzip-ui-tests"));
    }

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
    /// <summary>
    /// 一个内存里的口令存储，给所有需要"有/没有第二层"的界面用例用。
    /// <para>放在脚手架里而不是各测试文件里各写一份，是为了让它必须<strong>忠实于真实现</strong>：
    /// 键按 <see cref="PasswordVaultKey.Normalize"/> 归一化，而<strong>显示路径保留用户写的那个大小写</strong>
    /// （<c>CredentialVault</c> 的索引就是这么做的）。偷懒拿键当显示路径，界面用例就会对着
    /// "路径被显示成全大写"这种走形照样绿 —— 这一条真的这样红过一次。</para>
    /// </summary>
    internal sealed class FakePasswordStore : Andzip.Core.Archive.IPersistentPasswordStore
    {
        private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _display = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> Stored => _passwords;

        public bool IsSupported { get; set; } = true;

        public string? Rejects(string password) => null;

        public string? Save(string archivePath, string password)
        {
            var key = PasswordVaultKey.Normalize(archivePath);
            _passwords[key] = password;
            _display[key] = Path.GetFullPath(archivePath);
            return null;
        }

        public bool TryGet(string archivePath, out string password)
            => _passwords.TryGetValue(PasswordVaultKey.Normalize(archivePath), out password!);

        public void Remove(string archivePath)
        {
            var key = PasswordVaultKey.Normalize(archivePath);
            _passwords.Remove(key);
            _display.Remove(key);
        }

        public IReadOnlyList<PasswordVaultEntry> List()
            => [.. _display.Select(pair => new PasswordVaultEntry(pair.Value, DateTimeOffset.UnixEpoch))];

        public void Clear()
        {
            _passwords.Clear();
            _display.Clear();
        }
    }

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
            => [.. ShownDescendants(root).Select(ContentText).Where(text => !string.IsNullOrEmpty(text)).Select(text => text!)];

        /// <summary>
        /// 只走<strong>这时真看得见</strong>的那一支。
        /// <para>不能直接用 <see cref="Descendants"/>：Avalonia 的 <c>IsVisible</c> 不继承 ——
        /// 父节点藏着时子节点的 <c>IsVisible</c> 仍然是 true，所以可见性要自己一路带下去。
        /// 安装向导按页切可见性（见 <c>InstallerWindow.GoTo</c>），按逻辑树读就会把
        /// 不在当前这一页上的文案也算成"用户看得见"，于是"卸载完成页上没有那颗按不动的
        /// 完成后运行勾选框"这类断言会假绿。</para>
        /// </summary>
        private static IEnumerable<Control> ShownDescendants(Control control)
        {
            if (!control.IsVisible)
            {
                yield break;
            }

            yield return control;

            foreach (var child in control.GetLogicalChildren().OfType<Control>())
            {
                foreach (var nested in ShownDescendants(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>这个控件对用户写着什么。测试按文案找控件时用它，不要自己 <c>Content.ToString()</c>
        /// —— 有的按钮把视觉画在 Content 里的一颗 Border 上（设置页导航），那样读回来是类型名。</summary>
        public static string? TextOf(Control control) => ContentText(control);

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
            Button button => button.Content switch
            {
                string text => text,
                // 视觉画在 Content 里那颗自绘 Border 上的按钮（设置页导航、开关），
                // 直接 ToString 会读回 "Avalonia.Controls.Border" —— 那不是用户看见的东西。
                Control content => Descendants(content).OfType<TextBlock>()
                    .Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t)),
                _ => button.Content?.ToString(),
            },
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

    /// <summary>
    /// 从窗口画面上取一个点的实际颜色。规范里"这个状态看得见"这类断言只能靠它 ——
    /// 读控件的属性只能证明"代码写了什么"，证明不了"画出来没有"。
    /// </summary>
    internal static class Pixels
    {
        public static Color At(Window window, Point at)
            => Sample(window, at, (buffer, index) =>
                // 实测这个 headless 后端的帧缓冲是 R,G,B,A 顺序（不是文档里默认的 Bgra32）：
                // 深色底 #1F2328 读回来是 [1F,23,28]。按 Bgra 读会得到 #28231F，
                // 那会让"颜色对不对"的断言全部假阴性 —— 所以这里按实测的顺序取。
                Color.FromRgb(buffer[index], buffer[index + 1], buffer[index + 2]));

        /// <summary>
        /// 一个点的 alpha。<strong>透明窗口只有靠它才量得出"阴影被切了没有"</strong>：
        /// 面板之外是透明的，阴影每往外交一寸，那一寸就不再是全透明。
        /// 窗口最外一圈只要有一个像素 alpha&gt;0，就说明阴影没在边界内衰减干净 ——
        /// 屏幕上看到的是一条齐刷刷的切痕，四个角最明显。
        /// </summary>
        public static byte AlphaAt(Window window, Point at)
            => Sample(window, at, (buffer, index) => buffer[index + 3]);

        /// <summary>取一帧并按窗口坐标定位到某个像素的起始字节。</summary>
        private static T Sample<T>(Window window, Point at, Func<byte[], int, T> pick)
        {
            var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("窗口没有画面可取。");

            using (frame)
            {
                var scale = (double)frame.PixelSize.Width / window.Bounds.Width;
                var stride = frame.PixelSize.Width * 4;
                var buffer = new byte[stride * frame.PixelSize.Height];
                using (var locked = frame.Lock())
                {
                    System.Runtime.InteropServices.Marshal.Copy(locked.Address, buffer, 0, buffer.Length);
                }

                var x = Math.Clamp((int)(at.X * scale), 0, frame.PixelSize.Width - 1);
                var y = Math.Clamp((int)(at.Y * scale), 0, frame.PixelSize.Height - 1);
                return pick(buffer, y * stride + x * 4);
            }
        }

        public static Color At(Window window, Control control, double offsetX = 6, double offsetY = 6)
            => At(window, control.TranslatePoint(new Point(offsetX, offsetY), window)!.Value);

        public static string Name(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>WCAG 的相对亮度。判"这块到底暗不暗"要用它，不能直接比 RGB 之和。</summary>
        public static double Luminance(Color c)
        {
            static double Channel(byte value)
            {
                var s = value / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        /// <summary>令牌是画刷，量出来的是颜色，两边都要能报成同一个十六进制串。</summary>
        /// <summary>令牌是画刷，量出来的是颜色；要拿令牌跟像素比就得先取出色来。</summary>
        public static Color Of(IBrush brush) => brush is ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"这个令牌不是纯色画刷（{brush.GetType().Name}）。");

        public static string Name(IBrush brush) => brush is ISolidColorBrush solid
            ? Name(solid.Color)
            : throw new InvalidOperationException($"这个令牌不是纯色画刷（{brush.GetType().Name}）。");

        /// <summary>通道排序后比较：帧缓冲的字节序在不同后端不一致，比"是不是这个颜色"就够了。</summary>
        public static bool Same(Color a, Color b)
        {
            static int[] Sort(Color c) => [.. new[] { (int)c.R, c.G, c.B }.OrderBy(v => v)];
            return Sort(a).SequenceEqual(Sort(b));
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
