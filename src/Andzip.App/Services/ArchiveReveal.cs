using System.Diagnostics;
using System.Runtime.Versioning;

namespace Andzip.App.Services;

/// <summary>
/// 在系统文件管理器里打开某个文件所在的位置（并尽量选中它）。
/// 视图栏的定位按钮与「文件 → 打开压缩文件位置」共用这一份。
/// </summary>
internal static class ArchiveReveal
{
    /// <summary>
    /// 打开所在目录。返回 false 表示这台机器上没有可用的文件管理器
    /// （精简系统、无桌面会话）—— 调用方可以什么都不做，但别谎报成功。
    /// </summary>
    public static bool Reveal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return RevealOnWindows(full);
            }

            var (file, argument) = OperatingSystem.IsMacOS()
                ? ("open", new[] { "-R", full })
                : ("xdg-open", new[] { Path.GetDirectoryName(full) ?? full });

            Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true, ArgumentList = { argument[0] } });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>explorer.exe /select,"路径"</c>。
    /// <para>
    /// 这里刻意用 <see cref="ProcessStartInfo.Arguments"/> 拼出那一条带引号的参数，
    /// 而不是 <c>ArgumentList.Add("/select," + path)</c>：后者会让 .NET 把整个串当成一个参数、
    /// 只在含空格时从外面加引号，于是 explorer 收到的是 <c>"/select,C:\a b\x.zip"</c> ——
    /// 它认不出来，结果是"打开了目录但没选中那个文件"，看起来像功能没生效。
    /// </para>
    /// <para>
    /// 拼字符串在这里是安全的：双引号是 Windows 文件名里<strong>禁止</strong>出现的字符，
    /// 所以路径本身不可能把那个引号闭合掉，也就没有参数注入的余地。
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool RevealOnWindows(string full)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "/select,\"" + full + "\"",
            UseShellExecute = true,
        });

        return true;
    }
}
