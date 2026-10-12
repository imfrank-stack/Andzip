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
        lang.Get("TEXT_MENU_EXTRACT"),
        lang.Get("TEXT_MENU_EXTRACT_DELETE"))
    {
        ShowExtractVerbs = showExtractVerbs,
    };

    /// <summary>
    /// 两个压缩动词。键名与 <see cref="ShellTask"/> 的名字保持一致，方便对照注册表排查。
    /// <para>「压缩为 ZIP」是"随手压一下"，「压缩到…」把「新建压缩文件」对话框开起来 —— 口令、分卷、
    /// 压缩后删除源文件这些都得有人看着选，静态动词表达不了。</para>
    /// <para>以前还有第三条「压缩为 7Z」，用户的要求是"其他文件压缩只留这两项"，所以撤了：
    /// 7Z 仍然能压，走「压缩到…」在对话框里选格式。</para>
    /// <para><see cref="FileAssociationCatalog.CompressionSuppressionQuery"/> 让这两项在所有
    /// 压缩包扩展名上隐身 —— 右键一个 zip 再给它"压缩为 ZIP"是没有意义的动作。</para>
    /// </summary>
    public static IReadOnlyList<ContextMenuService.CompressionVerb> Compression(LangCatalog lang)
    {
        var suppressed = FileAssociationCatalog.CompressionSuppressionQuery();
        return
        [
            new("compress-zip", ShellTasks.Switch(ShellTask.CompressZip), lang.Get("TEXT_MENU_COMPRESS_ZIP"), suppressed),
            new("compress-dialog", ShellTasks.Switch(ShellTask.CompressDialog), lang.Get("TEXT_MENU_COMPRESS_DIALOG"), suppressed),
        ];
    }
}
