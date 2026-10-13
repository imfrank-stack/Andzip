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
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellLink
{
    /// <param name="iconPath">图标来源；null 表示跟目标同一个文件。</param>
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

    /// <summary>读回快捷方式指向的图标（测试用：证明"带透明通道的那张图"真的挂上去了）。</summary>
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

    /// <summary>方法顺序就是 vtable 顺序，错一个位置就会调到隔壁那个函数上 —— 编译器不会报。</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(StringBuilder pszFile, int cchMax, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr pidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(StringBuilder pszName, int cchMax);

        void SetDescription(string pszName);

        void GetWorkingDirectory(StringBuilder pszDir, int cchMax);

        void SetWorkingDirectory(string pszDir);

        void GetArguments(StringBuilder pszArgs, int cchMax);

        void SetArguments(string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(StringBuilder pszIconPath, int cchMax, out int piIcon);

        void SetIconLocation(string pszIconPath, int iIcon);

        void SetRelativePath(string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath(string pszFile);
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
