using System.Reflection;

namespace Andzip.Core.Lang;

/// <summary>
/// 语言包加载器：优先加载程序目录 langs/ 下的外部 .lang（便携覆盖），否则回退到内嵌资源（单文件绿色版）。
/// 语言代码取文件名（不含扩展名），如 SimpChinese.lang → "SimpChinese"。
/// </summary>
public static class LangLoader
{
    public static IReadOnlyList<(string Code, LangFile File)> FromDirectory(string directory)
    {
        var result = new List<(string, LangFile)>();
        if (!Directory.Exists(directory))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.lang").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var code = ExtractLanguageCode(Path.GetFileName(path), ".lang");
            result.Add((code, LangFile.Load(path)));
        }

        return result;
    }

    public static IReadOnlyList<(string Code, LangFile File)> FromEmbedded(Assembly assembly)
        => FromEmbedded(assembly, suffix: ".lang");

    public static IReadOnlyList<(string Code, LangFile File)> FromEmbedded(Assembly assembly, string suffix)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var result = new List<(string, LangFile)>();
        foreach (var name in assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            result.Add((ExtractLanguageCode(name, suffix), LangFile.Parse(reader.ReadToEnd())));
        }

        return result;
    }

    /// <summary>
    /// 从资源名/文件名提取语言代码。
    /// 内嵌资源名带命名空间点号（如 Andzip.App.langs.SimpChinese.lang），
    /// 不能直接用 Path.GetFileNameWithoutExtension（会得到 "Andzip.App.langs.SimpChinese"）。
    /// </summary>
    public static string ExtractLanguageCode(string resourceName, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var stem = resourceName;
        if (!string.IsNullOrEmpty(suffix) && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^suffix.Length];
        }

        var lastDot = stem.LastIndexOf('.');
        return lastDot >= 0 ? stem[(lastDot + 1)..] : stem;
    }

    /// <summary>构建目录：外部 langs/ 目录优先，其次内嵌资源；当前语言优先取 <paramref name="preferred"/>。</summary>
    public static LangCatalog CreateCatalog(Assembly assembly, string baseDirectory, string? preferred = null)
    {
        var catalog = new LangCatalog();
        var items = FromDirectory(Path.Combine(baseDirectory, "langs"));
        if (items.Count == 0)
        {
            items = FromEmbedded(assembly);
        }

        catalog.AddRange(items);

        if (preferred is not null && catalog.SetLanguage(preferred))
        {
            return catalog;
        }

        if (catalog.HasLanguage("SimpChinese"))
        {
            catalog.SetLanguage("SimpChinese");
        }

        return catalog;
    }
}