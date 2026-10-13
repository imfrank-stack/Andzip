using Andzip.Core.Settings;

namespace Andzip.Core.Tests;

/// <summary>
/// 主题偏好的存储与折算。三件事各自要钉：存取不失真、认不出来时回落得有道理、
/// "跟随系统"到底翻成深还是浅。
/// </summary>
public class ThemeModeTests
{
    [Theory]
    [InlineData("system", ThemeMode.System)]
    [InlineData("light", ThemeMode.Light)]
    [InlineData("dark", ThemeMode.Dark)]
    [InlineData("DARK", ThemeMode.Dark)]
    [InlineData(" light ", ThemeMode.Light)]
    public void Parse_AcceptsWhatItWrites(string stored, ThemeMode expected)
    {
        Assert.Equal(expected, SettingKeys.ParseThemeMode(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("midnight")]
    [InlineData("42")]
    public void Parse_FallsBackToFollowingTheSystem(string? stored)
    {
        // 认不出来时选 System 而不是 Light：这一项首次出现时大家看到的就该是自己系统的那一套。
        Assert.Equal(ThemeMode.System, SettingKeys.ParseThemeMode(stored));
    }

    [Fact]
    public void RoundTripsThroughTheSettingsFile()
    {
        var settings = new AppSettings();
        var wanted = ViewDefaults.Fallback with { Theme = ThemeMode.Dark };

        wanted.Save(settings);

        Assert.Equal("dark", settings.GetString(SettingKeys.ViewTheme));
        Assert.Equal(wanted, ViewDefaults.Load(settings));
    }

    [Fact]
    public void NothingStoredMeansFollowTheSystem()
    {
        var loaded = ViewDefaults.Load(new AppSettings());

        Assert.Equal(ThemeMode.System, loaded.Theme);
        // 「没设过」与「设成默认」要能区分开：设置页靠这个决定下拉框预选项与「立即应用」是否可用。
        Assert.False(new AppSettings().Has(SettingKeys.ViewTheme));
    }

    [Theory]
    [InlineData(ThemeMode.System, true, true)]
    [InlineData(ThemeMode.System, false, false)]
    [InlineData(ThemeMode.Dark, false, true)]
    [InlineData(ThemeMode.Light, true, false)]
    public void PrefersDarkResolvesThePreference(ThemeMode mode, bool systemIsDark, bool expected)
    {
        // 显式选浅/深时，系统读数必须被忽略 —— 否则"我在深色系统里选了浅色"永远不成立。
        Assert.Equal(expected, mode.PrefersDark(systemIsDark));
    }
}
