using Microsoft.Win32;

namespace Andzip.App.Services;

/// <summary>
/// 只在与现值不同时才动注册表，并回报"到底动过没有"。
/// <para><strong>返回值不是好看的计数，它决定要不要发 <c>SHChangeNotify</c>。</strong>
/// 一次 <c>SHCNE_ASSOCCHANGED</c> 是<strong>全局</strong>广播：shell 会丢掉关联与图标缓存重读，
/// 屏幕上的图标整片闪一下。于是"要不要广播"必须由"有没有真的变"来决定 ——
/// 3.2.0 报的"每次解压/压缩图标都闪一次"就是这条链断在了这里（见下面
/// <c>RepairCommands</c> 的说明）。</para>
/// <para>顺带也少写几笔：写一个相同的值对注册表来说是"改了"，对资源管理器来说也是"关联变了"，
/// 而这两件事我们都不需要。</para>
/// </summary>
internal static class RegistryWrite
{
    /// <summary>值不同才写。返回是否真的写过。</summary>
    public static bool SetIfDifferent(RegistryKey key, string name, string value)
    {
        if (key.GetValue(name) as string == value)
        {
            return false;
        }

        key.SetValue(name, value);
        return true;
    }

    /// <summary>值在才删。返回是否真的删过 —— "删掉一个本来就没有的东西"不算改动。</summary>
    public static bool DeleteIfPresent(RegistryKey key, string name)
    {
        if (key.GetValue(name) is null)
        {
            return false;
        }

        key.DeleteValue(name, throwOnMissingValue: false);
        return true;
    }

    /// <summary>子树在才删。返回是否真的删过。</summary>
    public static bool DeleteTreeIfPresent(RegistryKey key, string name)
    {
        using (var existing = key.OpenSubKey(name))
        {
            if (existing is null)
            {
                return false;
            }
        }

        key.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        return true;
    }
}
