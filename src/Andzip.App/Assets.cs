using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Andzip.App;

/// <summary>
/// 程序图标：<strong>出厂那一张是内嵌资源，用户自己调的那一张是磁盘上的一个 .ico</strong>。
/// <para>为什么出厂的要内嵌：<c>assets/andzip.png</c> 不在发布目录里（单文件绿色版只有一个 exe），
/// 从磁盘读等于"换了台机器就少一个图标"。</para>
/// <para>为什么用户那张要落盘：快捷方式与注册表的 <c>DefaultIcon</c> 只能指向
/// <strong>一个文件路径</strong>，指向 exe 里那张就意味着永远是出厂那一张（exe 的资源在运行时改不了，
/// 那是 PE 里的段，不是数据）。所以自定义图标这件事的形态必然是：
/// 画一张 .ico 放到 <c>%APPDATA%\Andzip\</c>，让各处去指它。</para>
/// <para>标题栏那枚、任务栏与 Alt+Tab 那一张、桌面上那一个 —— 必须是同一张图。
/// 一旦它们长得不一样，用户就会怀疑自己点开的不是同一个程序，所以这里只留一个出口
/// （<see cref="AppArt"/> / <see cref="AppIcon"/>），换图标只换这一处。</para>
/// </summary>
internal static class Assets
{
    private static readonly Bitmap? EmbeddedArt = Load("andzip.png");
    private static readonly WindowIcon? EmbeddedIcon = LoadIcon("andzip.png");

    private static Bitmap? _customArt;
    private static WindowIcon? _customIcon;

    /// <summary>图标位图（带透明通道）。读不到时为 null，调用方退回代码画的那枚标记。</summary>
    public static Bitmap? AppArt => _customArt ?? EmbeddedArt;

    /// <summary>给 <c>Window.Icon</c> 用的那份 —— 任务栏与 Alt+Tab 拿到的就是它。</summary>
    public static WindowIcon? AppIcon => _customIcon ?? EmbeddedIcon;

    /// <summary>换图标了。订阅方是各扇窗口与标题栏那枚图（<c>Chrome.WindowSurface</c> 里统一接）。</summary>
    public static event Action? IconChanged;

    /// <summary>
    /// 用磁盘上那个 .ico 当程序图标。文件读不到时<strong>不动现状并返回 false</strong> ——
    /// 启动时这个文件可能在另一台机器上、可能被杀毒软件刚删掉，那种情况下退回出厂那张比"没有图标"好。
    /// </summary>
    public static bool UseCustomIcon(string icoPath)
    {
        if (!File.Exists(icoPath))
        {
            return false;
        }

        try
        {
            // ICO 的第一张就是最大的那一张（IconArt 按面积从大到小排），所以位图与窗口图标同源。
            // <strong>先读成字节再喂给解码器</strong>：直接交一个文件流过去，Avalonia 会把它留着 ——
            // 于是"换一套配色重新生成同一个文件"与「恢复默认」删它的时候全部失败，
            // 而且是在用户第二次点按钮的时候才失败（第一次看不出任何问题）。
            var bytes = File.ReadAllBytes(icoPath);
            var art = new Bitmap(new MemoryStream(bytes));
            var icon = new WindowIcon(new MemoryStream(bytes));

            (_customArt as IDisposable)?.Dispose();
            (_customIcon as IDisposable)?.Dispose();

            _customArt = art;
            _customIcon = icon;
            IconChanged?.Invoke();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // 文件在，但里面不是一张读得开的图（被人手工改坏、或者写了一半）
            return false;
        }
    }

    /// <summary>退回内嵌那一张。「恢复默认」走的就是这里。</summary>
    public static void ClearCustomIcon()
    {
        (_customArt as IDisposable)?.Dispose();
        (_customIcon as IDisposable)?.Dispose();
        _customArt = null;
        _customIcon = null;
        IconChanged?.Invoke();
    }

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
