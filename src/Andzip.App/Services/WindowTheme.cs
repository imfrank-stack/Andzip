using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Andzip.App.Services;

/// <summary>
/// 让窗口的<strong>标题栏</strong>跟着界面主题一起翻深。
/// <para>Avalonia 11.3 的 <c>Window</c> 没有暴露标题栏配色（<c>SystemDecorations</c> 只管有没有边框），
/// 而 Windows 10/11 的深色标题栏是 DWM 的一个窗口属性 —— 只能自己调。
/// 不调的后果很显眼：整窗是深的，顶上那条标题栏是白的，像贴错了一张纸。</para>
/// <para><c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> 在 18985 之前是 19、之后是 20，
/// 所以两个值都试一次：设不进就返回错误码，不会伤到窗口。
/// 非 Windows 上 <see cref="Apply"/> 直接返回 false，调用方什么都不用做。</para>
/// </summary>
internal static class WindowTheme
{
    private const int UseImmersiveDarkModeOld = 19;
    private const int UseImmersiveDarkMode = 20;

    /// <summary>把标题栏设成深色/浅色。返回 false 表示这台机器上没有可用的 DWM 通道。</summary>
    public static bool Apply(Window window, bool dark)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var handle = window.TryGetPlatformHandle();
        if (handle is null)
        {
            return false;
        }

        return ApplyOnWindows(handle.Handle, dark);
    }

    [SupportedOSPlatform("windows")]
    private static bool ApplyOnWindows(nint hwnd, bool dark)
    {
        var value = dark ? 1 : 0;

        // 先试当前编号，再试旧编号：Windows 10 早期版本只认 19。
        return DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref value, sizeof(int)) == 0
            || DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeOld, ref value, sizeof(int)) == 0;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
