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

        if (string.IsNullOrWhiteSpace(version) || !System.Version.TryParse(version, out _))
        {
            throw new FormatException("VersionNo.ini 缺少合法的 Version（语义化版本，如 0.1.0）。");
        }

        if (buildNo is null)
        {
            throw new FormatException("VersionNo.ini 缺少合法的 BuildNo。");
        }

        return new VersionInfo(version, buildNo.Value, update);
    }

    public static VersionInfo LoadFile(string path) => Parse(File.ReadAllText(path));
}