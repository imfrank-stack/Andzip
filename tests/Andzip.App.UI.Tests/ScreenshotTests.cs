using Andzip.App.Views;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Andzip.App.UiTests;

/// <summary>
/// 把窗口真的画出来并存成 PNG。<strong>画得出来</strong>这件事本身值得断言：
/// 空白、全黑、尺寸为零的窗口在纯逻辑测试里是看不见的。
/// <para>
/// 图落在临时目录，由人眼再核一遍排版；这里只保证"非空白 + 尺寸正常"。
/// 字体与窗口边框是 headless 渲染，真实外观仍以桌面截图为准。
/// </para>
/// </summary>
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

            // 截图之外再钉一次内容：图能画出来但勾选项不见了，同样是不合格。
            Assert.Contains(VisualQuery.Of<CheckBox>(window),
                c => Equals(c.Content?.ToString(), lang.Get("TEXT_SETTINGS_COMPRESS_PREALLOC")));
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
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
        => VisualQuery.Of<Button>(window).Single(b => Equals(b.Content?.ToString(), text));
}
