namespace Andzip.Core.Versioning;

/// <summary>
/// 版本号来源（沿用参考软件做法，见需求文档 §11.2）：根目录 VersionNo.ini
/// <code>
/// [Version]
/// Version = 0.1.0
/// BuildNo = 1
/// update  = 0
/// </code>
/// </summary>
public sealed record VersionInfo(string Version, int BuildNo, bool Update)
{
    /// <summary>写入 exe 文件属性的文件版本（四段式，如 0.1.0.0）。</summary>
    public string FileVersion => Version + ".0";

    /// <summary>信息版本（含构建号，如 0.1.0+1）。</summary>
    public string InformationalVersion => $"{Version}+{BuildNo}";

    public static VersionInfo Parse(string iniText)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        string? version = null;
        int? buildNo = null;
        var update = false;

        foreach (var rawLine in iniText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            switch (key.ToUpperInvariant())
            {
                case "VERSION":
                    version = value;
                    break;
                case "BUILDNO":
                    if (int.TryParse(value, out var n))
                    {
                        buildNo = n;
                    }
                    break;
                case "UPDATE":
                    update = value is "1" or "true" or "True";
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(version) || !IsValidNumbering(version))
        {
            throw new FormatException(
                $"VersionNo.ini 的 Version 必须是 A.B.C 三段、每段一个十进制位（0-9），当前是「{version}」。"
                + "这不是语义化版本：某一位满 9 就向左边进一，所以 0.9.9 之后是 1.0.0，"
                + "既不是 0.10.0 也不是 0.9.10。");
        }

        if (buildNo is null)
        {
            throw new FormatException("VersionNo.ini 缺少合法的 BuildNo。");
        }

        return new VersionInfo(version, buildNo.Value, update);
    }

    public static VersionInfo LoadFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// 编号规则（用户 2026-10-12 定的）：三段 <c>A.B.C</c>，<strong>每一位都是 0-9 的一个十进制位</strong>。
    /// <para>修 bug 动 C，小功能动 B 并把 C 清零，大版本动 A 并把 B、C 清零；任何一位满 9 就向左边进一。
    /// 所以 <c>0.9.9</c> 之后是 <c>1.0.0</c> —— <c>0.10.0</c> 那种写法根本不该出现，
    /// 它把"第二位加一"写成了两位数字。规则以前只活在文档里，2026-10-13 就出过一次
    /// <c>0.13.0</c>（应为 <c>1.3.0</c>），于是挪到这里由 <c>Parse</c> 把关。</para>
    /// </summary>
    private static bool IsValidNumbering(string version)
    {
        var parts = version.Split('.');

        return parts.Length == 3
            && parts.All(part => part.Length == 1 && char.IsAsciiDigit(part[0]));
    }
}