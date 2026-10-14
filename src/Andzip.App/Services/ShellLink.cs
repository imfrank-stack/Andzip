using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Andzip.App.Services;

/// <summary>
/// 桌面与开始菜单里的 <c>.lnk</c> 快捷方式。
/// <para>为什么不用 <c>WScript.Shell</c> 那个 COM 对象（网上最常见的写法）：它要经过
/// IDispatch 晚绑定，参数是字符串拼的，路径里一个引号就能把命令行拆坏；
/// <c>IShellLinkW</c> 是强类型早绑定，窄字符接口，路径原样传。</para>
/// <para><strong>图标这一项是这次改版的正题</strong>：<c>SetIconLocation(exe, 0)</c> 让 shell
/// 去读 exe 里那张 ICO，而那张 ICO 是从带透明通道的 <c>assets/andzip.png</c> 裁出来的 ——
/// 桌面上的图标因此是圆角带透明的，不是带一圈白底的方块。
/// 这条由 <c>InstallerTests</c> 读回快捷方式的图标字段核对。</para>
/// <para><strong>三个 COM 类型整个保住，一个方法都不许删</strong>：COM 早绑定调用是按
/// 【方法在接口里的序号】算 vtable 槽位的，而我们只用到 18 个方法里的 6 个。裁剪器把没被
/// 调用的那些当死代码删掉之后，序号整体前移，<c>SetPath</c> 就打到隔壁槽上，
/// 得到 <c>E_INVALIDARG</c>（"Value does not fall within the expected range"）。
/// 未裁剪的测试进程里一切正常，所以这个坑只能在产物上量到 ——
/// 见 build.ps1 的 <c>/selftest</c> 门禁。
/// 保留声明挂在 <see cref="Create"/> 上而不是 XML 描述符里：描述符那套嵌套类型要写
/// <c>Outer/Inner</c>，按 C# 习惯写成 <c>Outer+Inner</c> 匹配不上，而匹配不上也只是静默无效。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellLink
{
    /// <param name="iconPath">图标来源；null 表示跟目标同一个文件。</param>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ShellLinkNative))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IShellLinkW))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IPersistFile))]
    public static void Create(string linkPath, string targetPath, string? arguments, string? description, string? iconPath = null)
    {
        var link = new ShellLinkNative();
        try
        {
            var cast = (IShellLinkW)link;
            cast.SetPath(targetPath);

            if (arguments is not null)
            {
                cast.SetArguments(arguments);
            }

            if (description is not null)
            {
                cast.SetDescription(description);
            }

            cast.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? string.Empty);
            cast.SetIconLocation(iconPath ?? targetPath, 0);

            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            ((IPersistFile)link).Save(linkPath, createNew: true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// 只换快捷方式的图标，其余一格都不动。
    /// <para>为什么不干脆 <see cref="Create"/> 重建一个：快捷方式里还有工作目录、说明、热键、
    /// 相对路径这些我们没读回来的字段，重建等于把它们抹平 —— 用户只是换了个图标颜色，
    /// 结果"以管理员身份运行"那个勾没了之类的怪事就会冒出来。读进来、改一格、原地存回去才是最小动作。</para>
    /// <para><paramref name="iconPath"/> 传 null 就是"图标跟目标文件走"（也就是出厂那张）。</para>
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ShellLinkNative))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IShellLinkW))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IPersistFile))]
    public static void SetIcon(string linkPath, string? iconPath)
    {
        var link = new ShellLinkNative();
        try
        {
            var cast = (IShellLinkW)link;
            ((IPersistFile)link).Load(linkPath, 0);
            // 空串才是"没有单独的图标，跟目标文件走"。传快捷方式自己的路径是另一种意思
            // （那会写下一个显式的图标字段，指向 .lnk），恢复默认时不该留下它。
            cast.SetIconLocation(iconPath ?? string.Empty, 0);

            // createNew = false：覆盖同一个文件。传 true 时 IPersistFile 会拒绝第二次保存。
            ((IPersistFile)link).Save(linkPath, createNew: false);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// 读回快捷方式指向的图标（测试用：证明"带透明通道的那张图"真的挂上去了）。
    /// </summary>
    public static (string Path, int Index) ReadIcon(string linkPath)
    {
        var link = new ShellLinkNative();
        try
        {
            var cast = (IShellLinkW)link;
            // STGM_READ 是 0 —— 传 4 会得到 E_INVALIDARG（实测）
            ((IPersistFile)link).Load(linkPath, 0);

            var builder = new StringBuilder(260);
            cast.GetIconLocation(builder, builder.Capacity, out var index);
            return (builder.ToString(), index);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>
    /// 读回快捷方式指向哪个程序、带什么参数。
    /// <para>开始菜单里那一项「卸载 Andzip」的全部作用就是这两格 —— 目标指错或参数丢了，
    /// 它照样显示得好好的，点了却开出主窗口。所以这一处要能读回来测。</para>
    /// </summary>
    public static (string Path, string Arguments) ReadTarget(string linkPath)
    {
        var link = new ShellLinkNative();
        try
        {
            var cast = (IShellLinkW)link;
            ((IPersistFile)link).Load(linkPath, 0);

            var target = new StringBuilder(260);
            cast.GetPath(target, target.Capacity, IntPtr.Zero, 0);

            var arguments = new StringBuilder(260);
            cast.GetArguments(arguments, arguments.Capacity);

            return (target.ToString(), arguments.ToString());
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    public static void Delete(string linkPath)
    {
        if (File.Exists(linkPath))
        {
            File.Delete(linkPath);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkNative
    {
    }

    /// <summary>
    /// 方法顺序就是 vtable 顺序，错一个位置就会调到隔壁那个函数上 —— 编译器不会报。
    /// <para>每个字符串参数都写死 <c>LPWStr</c>：这套接口要的是 <c>PCWSTR</c>（裸宽字符指针），
    /// 而 C# 里 <c>string</c> 走 COM 的默认封送是 <c>BSTR</c>。BSTR 的数据指针也正好指向字符开头，
    /// 所以未裁剪时"碰巧"能用 —— 但那是运气，不是契约，别依赖。</para>
    /// <para>裁剪产物里那个 <c>E_INVALIDARG</c> 反而跟封送无关：是接口方法被删导致序号前移，
    /// 见类型上那段说明。<strong>这条只能由 <c>/selftest</c> 门禁在产物上量到</strong> ——
    /// 当时全部单元测试都是绿的。</para>
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMax, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr pidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMax);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMax);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMax);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchMax, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        void IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool createNew);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
