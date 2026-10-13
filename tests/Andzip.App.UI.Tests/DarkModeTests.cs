using Andzip.App.Views;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Andzip.App.UiTests;

/// <summary>
/// 夜间模式端到端：切调色板要真的把窗口画暗，设置页那一下要点完就落盘并生效。
/// <para>两条都是"量像素 + 读文件"，不是"属性被赋过值"。
/// 深浅这件事最容易骗自己：调色板换了但控件还是构造时那份颜色，属性看起来全对，屏幕上没变。</para>
/// </summary>
[Collection("界面状态")]
public class DarkModeTests
{
    [AvaloniaFact]
    public void SwitchingToDarkRepaintsTheWindowSurface()
    {
        var lang = TestLang.Chinese();
        Design.Use(dark: false);
        var window = new MainWindow(lang, new AppSettings()).Shown();

        try
        {
            var light = Pixels.At(window, new Point(400, 400));

            Design.Use(dark: true);
            UiWait.Pump(100);

            var dark = Pixels.At(window, new Point(400, 400));

            Assert.False(Pixels.Same(light, dark), "切到深色之后窗口内容区一个像素都没变：外壳没重画。");
            Assert.True(
                Luminance(dark) < Luminance(light),
                $"深色那一份反而更亮（{Pixels.Name(light)} → {Pixels.Name(dark)}）。");
            Assert.Equal(Pixels.Name(Palette.Dark.SurfaceBase), Pixels.Name(dark));
        }
        finally
        {
            Design.Use(dark: false);
        }
    }

    [AvaloniaFact]
    public void TheViewPageWritesTheThemeChoiceAndAppliesIt()
    {
        var lang = TestLang.Chinese();
        var path = Path.Combine(Path.GetTempPath(), $"andzip-theme-{Guid.NewGuid():N}.ini");
        Design.Use(dark: false);

        try
        {
            // 先落一份"深色"的初始值，验证下拉框是照着文件预选的，不是照着自己的默认值。
            var seed = new AppSettings();
            (ViewDefaults.Fallback with { Theme = ThemeMode.Dark }).Save(seed);
            seed.Save(path);

            var window = new SettingsWindow(lang, new AppSettings(), path).Shown();
            Clicker.Invoke(NavButton(window, lang.Label("TEXT_SETTINGS_VIEW")));
            UiWait.Pump(50);

            var combo = VisualQuery.Of<ComboBox>(window).Single();
            Assert.Equal((int)ThemeMode.Dark, combo.SelectedIndex);

            combo.SelectedIndex = (int)ThemeMode.Light;
            Clicker.Invoke(FindButton(window, lang.Label("TEXT_BTN_APPLY")));
            UiWait.Pump(50);

            Assert.Equal("light", AppSettings.Load(path).GetString(SettingKeys.ViewTheme));
            Assert.Equal("light", Design.Active.Name);
        }
        finally
        {
            Design.Use(dark: false);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>「跟随系统」不是第四种颜色：它必须被折算成浅或深，而不是留在界面上等用户自己猜。</summary>
    [AvaloniaFact]
    public void FollowingTheSystemResolvesToOnePalette()
    {
        Design.Apply(ThemeMode.System);
        Assert.Equal(ThemeMode.System, Design.Mode);
        Assert.True(Design.Active.Name is "light" or "dark");

        Design.Apply(ThemeMode.Dark);
        Assert.Equal("dark", Design.Active.Name);

        Design.Apply(ThemeMode.Light);
        Assert.Equal("light", Design.Active.Name);
    }

    private static Button NavButton(Window window, string text)
        => VisualQuery.Of<Button>(window).Single(b => Equals(VisualQuery.TextOf(b), text));

    private static Button FindButton(Window window, string text)
        => VisualQuery.Of<Button>(window).Single(b => Equals(VisualQuery.TextOf(b), text));

    private static double Luminance(Avalonia.Media.Color c)
        => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
}
