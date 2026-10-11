using System.Runtime.Versioning;
using Andzip.Core.Archive;
using Andzip.Core.Lang;

namespace Andzip.App.Services;

/// <summary>
/// 右键菜单文字的单一来源：把语言包里的文案组装成注册表要写的动词表。
/// <para>服务层不认识 <see cref="LangCatalog"/>，文案在这里取好再传下去。</para>
/// <para>它产出的两个类型都只服务于 Windows 注册表，所以本身也只支持 Windows。</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellVerbLabels
{
    /// <param name="showExtractVerbs">「设置 → 上下文菜单 → 解压菜单项」。关掉时 ProgID 里只留 open。</param>
    public static FileAssociationService.ExtractionVerbLabels Extraction(LangCatalog lang, bool showExtractVerbs = true) => new(
        lang.Get("TEXT_MENU_OPEN_WITH"),
        lang.Get("TEXT_MENU_EXTRACT_TO_FOLDER"),
        lang.Get("TEXT_MENU_EXTRACT_HERE"))
    {
        ShowExtractVerbs = showExtractVerbs,
    };

    /// <summary>两个压缩动词。键名与 <see cref="ShellTask"/> 的名字保持一致，方便对照注册表排查。</summary>
    public static IReadOnlyList<ContextMenuService.CompressionVerb> Compression(LangCatalog lang) =>
    [
        new("compress-zip", ShellTasks.Switch(ShellTask.CompressZip), lang.Get("TEXT_MENU_COMPRESS_ZIP")),
        new("compress-7z", ShellTasks.Switch(ShellTask.CompressSevenZip), lang.Get("TEXT_MENU_COMPRESS_7Z")),
    ];
}
