using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Andzip.App;

/// <summary>
/// 随程序分发的位图资源：只有一件，就是程序图标本身。
/// <para>为什么从内嵌资源读而不是从磁盘：<c>assets/andzip.png</c> 不在发布目录里
/// （单文件绿色版只有一个 exe），从磁盘读等于"换了台机器就少一个图标"。</para>
/// <para>为什么图标要同时当标题栏那枚小图：标题栏与任务栏/桌面上是两个不同的图标，
/// 一旦它们长得不一样，用户就会怀疑自己点开的不是同一个程序。图源只留一份，
/// 两边不可能再分家（<c>assets/make_icon.py</c> 出的 ICO 也是从同一张 PNG 裁的）。</para>
/// </summary>
internal static class Assets
{
    /// <summary>图标位图（带透明通道）。读不到时为 null，调用方退回代码画的那枚标记。</summary>
    public static Bitmap? AppArt { get; } = Load("andzip.png");

    /// <summary>给 <c>Window.Icon</c> 用的那份 —— 任务栏与 Alt+Tab 拿到的就是它。</summary>
    public static WindowIcon? AppIcon { get; } = LoadIcon("andzip.png");

    private static Bitmap? Load(string name)
    {
        var stream = Open(name);
        return stream is null ? null : new Bitmap(stream);
    }

    private static WindowIcon? LoadIcon(string name)
    {
        var stream = Open(name);
        return stream is null ? null : new WindowIcon(stream);
    }

    /// <summary>按文件名后缀找内嵌资源：逻辑名带根命名空间前缀，写死一个字符串不如查一次。</summary>
    private static Stream? Open(string name)
    {
        var assembly = typeof(Assets).Assembly;
        var key = Array.Find(assembly.GetManifestResourceNames(),
            n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        return key is null ? null : assembly.GetManifestResourceStream(key);
    }
}
