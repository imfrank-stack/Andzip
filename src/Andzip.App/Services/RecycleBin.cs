using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Andzip.App.Services;

/// <summary>
/// 把文件送进回收站。<strong>只有 Windows 有回收站</strong>，所以这个类也只存在于 Windows 路径上 ——
/// 调用方要先判断平台，别指望这里能跨平台。
/// <para>
/// 用 <c>SHFileOperationW</c> 而不是 <c>File.Delete</c>：后者是永久删除，用户点「删除压缩文件」
/// 时期待的是"还能反悔"。也没走 <c>Microsoft.VisualBasic.FileIO</c> 那个现成的壳 ——
/// 它为这一个方法要拖进整个 VisualBasic 程序集，而裁剪发布下多一层不确定。
/// </para>
/// <para>
/// 结构体里的字符串全部手工分配（<see cref="Marshal.StringToHGlobalUni"/>）而不是让 marshaller 处理：
/// <c>pFrom</c> 要求<strong>双零结尾</strong>，而 marshaller 对含嵌入零的字符串怎么处理，
/// 不同版本说法不一 —— 手工分配这件事就没有歧义。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RecycleBin
{
    private const int FoDelete = 0x3;
    private const ushort FofSilent = 0x4;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofNoErrorUi = 0x400;
    private const ushort FofAllowUndo = 0x40;

    /// <summary>用户在系统自己的确认框里点了取消（ERROR_CANCELLED）。</summary>
    private const int ErrorCancelled = 120;

    /// <summary>移到回收站。失败抛 <see cref="IOException"/>，调用方把消息原样转达。</summary>
    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // 双零结尾：SHFileOperationW 的 pFrom 是一个"列表"，列表以空串收尾。
        var from = Marshal.StringToHGlobalUni(path + "\0");
        try
        {
            var operation = new Shfileopstructw
            {
                Hwnd = IntPtr.Zero,
                WFunc = FoDelete,
                PFrom = from,
                PTo = IntPtr.Zero,
                FFlags = (ushort)(FofAllowUndo | FofSilent | FofNoConfirmation | FofNoErrorUi),
                AnyOperationsAbandoned = 0,
                NameMappings = IntPtr.Zero,
                ProgressTitle = IntPtr.Zero,
            };

            var result = SHFileOperationW(ref operation);
            if (result == ErrorCancelled)
            {
                throw new IOException("用户取消了删除。");
            }

            if (result != 0)
            {
                // 结构体里的 fAnyOperationsAbandoned 不足以说明原因， GetLastError 才有细节。
                throw new IOException(
                    $"移到回收站失败（代码 {result}）：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Shfileopstructw
    {
        public IntPtr Hwnd;

        public int WFunc;

        public IntPtr PFrom;

        public IntPtr PTo;

        public ushort FFlags;

        public int AnyOperationsAbandoned;

        public IntPtr NameMappings;

        public IntPtr ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref Shfileopstructw operation);
}
