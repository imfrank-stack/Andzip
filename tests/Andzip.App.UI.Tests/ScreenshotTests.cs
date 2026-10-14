using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Andzip.App.UiTests;

/// <summary>
/// 把窗口真的画出来并存成 PNG。<strong>画得出来</strong>这件事本身值得断言：
/// 空白、全黑、尺寸为零的窗口在纯逻辑测试里是看不见的。
/// <para>
/// 图落在临时目录，由人眼再核一遍排版；这里只保证"非空白 + 尺寸正常"。
/// 字体与窗口边框是 headless 渲染，真实外观仍以桌面截图为准。
/// </para>
/// </summary>
[Collection("界面状态")]
public class ScreenshotTests
{
    private static string OutputDirectory =>
        Path.Combine(Path.GetTempPath(), "andzip-ui-shots");

    [AvaloniaFact]
    public void MainWindowPaintsSomething()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();

        Save(window, "main-window.png");
    }

    [AvaloniaFact]
    public void CompressPagePaintsBothToggles()
    {
        var lang = TestLang.Chinese();
        var settingsPath = Path.Combine(Path.GetTempPath(), $"andzip-shot-{Guid.NewGuid():N}.ini");

        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), settingsPath).Shown();
            Clicker.Invoke(FindButton(window, lang.Get("TEXT_SETTINGS_COMPRESS")));

            Save(window, "settings-compress.png");

            // 截图之外再钉一次内容：图能画出来但开关不见了，同样是不合格。
            Assert.Contains(VisualQuery.Of<SettingToggle>(window),
                c => c.Label == lang.Get("TEXT_SETTINGS_COMPRESS_PREALLOC"));
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    /// <summary>
    /// 进度窗口画出来：一行进度说明 + 一根进度条，<strong>下面不许有任何东西被切掉</strong>。
    /// <para>1.9.2 之前那里还有一颗「取消」按钮，而窗口高度写的是 168 —— 装不下，
    /// 按钮被卡片的下边界切掉一半，用户的说法是"这个进度条有遮挡，删掉被遮挡的取消按钮"。
    /// 按钮删了，这条断言留下：它同时管住"不许再有东西被切"和"不许谁把高度改回去"。</para>
    /// <para>量的是<strong>进度条下沿到卡片下沿之间还剩多少</strong> —— 那就是 StackPanel 的下内距，
    /// 一个像素都不该被吃掉。</para>
    /// </summary>
    [AvaloniaFact]
    public void ProgressWindowPaintsWithoutAnythingCutOff()
    {
        var lang = TestLang.Chinese();
        using var cts = new CancellationTokenSource();
        var window = new ProgressWindow(lang, cts, "TEXT_STATUS_EXTRACTING").Shown();
        window.Update(new ArchiveProgress(ArchiveOperation.Extract, 15_600_000, 65_500_000,
            "backend/tests/test_layout.py", 7, 24));
        UiWait.Pump(120);

        Save(window, "progress-extracting.png");

        var card = window.GetVisualDescendants().OfType<Border>()
            .First(b => b.CornerRadius.TopLeft == Design.Radius.Panel.TopLeft);
        var bar = VisualQuery.Of<ProgressBar>(window).Single();

        var barBottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), window)!.Value.Y;
        var cardBottom = card.TranslatePoint(new Point(0, card.Bounds.Height), window)!.Value.Y;

        Assert.True(cardBottom - barBottom >= Design.Space.X6 - 1,
            $"进度条下沿离卡片下沿只剩 {cardBottom - barBottom:F0}（下内距要 {Design.Space.X6}）—— "
            + "下面有东西被切掉了，或者窗口高度又被人改小；那颗「取消」按钮就是这么被吃掉一半的");
        Assert.True(bar.Bounds.Height > 0 && bar.Value > 0,
            $"进度条这时该是 24% 的确定态，量到 高 {bar.Bounds.Height} 值 {bar.Value}");
    }

    [AvaloniaFact]
    public void RepairWindowPaintsThreeModes()
    {
        var lang = TestLang.Chinese();
        var dialog = new RepairWindow(lang, @"C:\broken.zip", @"C:\broken.修复.zip").Shown();

        // 单选钮要直接改 IsChecked：事件层的"点击"不会切换控件自身的状态（见 Clicker 的说明），
        // 第一版用 Clicker 点它，截出来的图仍是第一种模式的说明文字 —— 假截图比没截图更误导。
        VisualQuery.Of<RadioButton>(dialog).ToList()[2].IsChecked = true;

        Save(dialog, "repair-modes.png");

        Assert.Contains(TestLang.Chinese().Get("TEXT_REPAIR_MODE_3_HINT"),
            VisualQuery.Of<TextBlock>(dialog).Select(t => t.Text));
    }

    /// <summary>
    /// 深色下的卡片语言：画布压暗、卡片抬亮。阴影在这套配色里几乎不可见，
    /// 所以这两张图要看的不是"有没有影子"，而是"两块分不分得开"。
    /// <para><strong>顺序不能反</strong>：先 <c>Design.Use(dark: true)</c> 再构造主窗口是没用的 ——
    /// 主窗口构造末尾会按「查看 → 界面主题」（默认跟随系统）重新应用一次主题，
    /// 把刚设的深色冲回浅色。第一版就是这么截出一张"深色截图其实是浅色"的假图。</para>
    /// </summary>
    [AvaloniaFact]
    public void HomePaintsInDarkMode()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();

        Design.Use(dark: true);
        UiWait.Pump(100);

        try
        {
            Save(window, "dark-home.png");
            Assert.True(Pixels.Luminance(Pixels.At(window, new Point(4, 4))) < 0.12,
                "深色下的画布没有真的变暗，这张图是浅色的");
        }
        finally
        {
            Design.Use(dark: false);
        }
    }

    [AvaloniaFact]
    public void SettingsPagePaintsInDarkMode()
    {
        var lang = TestLang.Chinese();
        var settingsPath = Path.Combine(Path.GetTempPath(), $"andzip-shot-{Guid.NewGuid():N}.ini");

        Design.Use(dark: true);
        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), settingsPath).Shown();
            Clicker.Invoke(FindButton(window, lang.Get("TEXT_SETTINGS_COMPRESS")));
            Save(window, "dark-settings.png");

            Assert.Contains(VisualQuery.Of<SettingToggle>(window),
                c => c.Label == lang.Get("TEXT_SETTINGS_COMPRESS_PREALLOC"));
        }
        finally
        {
            Design.Use(dark: false);
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }

    private static void Save(Window window, string fileName)
    {
        Directory.CreateDirectory(OutputDirectory);

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        using (frame!)
        {
            Assert.True(frame.PixelSize.Width > 100 && frame.PixelSize.Height > 100,
                $"画面尺寸不正常：{frame.PixelSize.Width}x{frame.PixelSize.Height}");

            // 只有一种颜色 = 没画任何东西（全空白或全黑）。
            Assert.True(CountDistinctColors(frame) > 2, "画面只有一种颜色，等于没渲染出内容。");

            var path = Path.Combine(OutputDirectory, fileName);
            using var stream = File.Create(path);
            frame.Save(stream);
        }
    }

    private static int CountDistinctColors(WriteableBitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var colors = new HashSet<(int, int, int)>();

        // Lock() 而不是 CopyPixels：后者在这个版本上只接受指针缓冲区，
        // 而测试要的就是"逐像素看一眼"，锁帧缓冲最直接。
        using var locked = bitmap.Lock();
        var stride = locked.RowBytes;
        var buffer = new byte[stride * Math.Max(1, size.Height)];
        System.Runtime.InteropServices.Marshal.Copy(locked.Address, buffer, 0, buffer.Length);

        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x + 2 < stride; x += 4)
            {
                var at = y * stride + x;
                colors.Add((buffer[at], buffer[at + 1], buffer[at + 2]));
                if (colors.Count > 64)
                {
                    return colors.Count;   // 只需要知道"不止一种颜色"，不必数完
                }
            }
        }

        return colors.Count;
    }

    private static Button FindButton(Window window, string text)
        => VisualQuery.Of<Button>(window).Single(b => Equals(VisualQuery.TextOf(b), text));
}
