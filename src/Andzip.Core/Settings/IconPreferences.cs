using Andzip.Core.Iconography;

namespace Andzip.Core.Settings;

/// <summary>
/// 「设置 → 查看 → 图标配色」那一节的状态：一套颜色 + 一种字形 + 是否已经设成程序图标。
/// <para>读的时候<strong>处处宽容</strong>：这个文件用户可以拿记事本改，改坏一个字符就抛异常的话，
/// 症状是"程序打不开"，而那才是真的吓人。所以每一格单独退回默认值，
/// 而不是整份作废（也不是整份照收 —— 一个非法颜色画出来就是一块黑）。</para>
/// </summary>
public sealed record IconPreferences(IconPalette Palette, IconGlyph Glyph, bool Applied)
{
    public static IconPreferences Fallback { get; } =
        new(IconPalette.Default, IconGlyph.Overflow, Applied: false);

    public static IconPreferences Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        static string Kept(AppSettings s, string key, string fallback)
            => IconPalette.Normalize(s.GetString(key)) ?? fallback;

        var palette = new IconPalette(
            Kept(settings, SettingKeys.IconFront0, IconPalette.Default.Front0),
            Kept(settings, SettingKeys.IconFront1, IconPalette.Default.Front1),
            Kept(settings, SettingKeys.IconFront2, IconPalette.Default.Front2),
            Kept(settings, SettingKeys.IconBack0, IconPalette.Default.Back0),
            Kept(settings, SettingKeys.IconBack1, IconPalette.Default.Back1),
            Math.Clamp(settings.GetInt(SettingKeys.IconDirection, 0), 0, IconPalette.Directions.Length - 1));

        return new IconPreferences(
            palette,
            settings.GetString(SettingKeys.IconGlyph) == "punched" ? IconGlyph.Punched : IconGlyph.Overflow,
            settings.GetBool(SettingKeys.IconApplied, fallback: false));
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.SetString(SettingKeys.IconFront0, Palette.Front0);
        settings.SetString(SettingKeys.IconFront1, Palette.Front1);
        settings.SetString(SettingKeys.IconFront2, Palette.Front2);
        settings.SetString(SettingKeys.IconBack0, Palette.Back0);
        settings.SetString(SettingKeys.IconBack1, Palette.Back1);
        settings.SetInt(SettingKeys.IconDirection, Palette.Direction);
        settings.SetString(SettingKeys.IconGlyph, Glyph == IconGlyph.Punched ? "punched" : "overflow");
        settings.SetBool(SettingKeys.IconApplied, Applied);
    }
}
