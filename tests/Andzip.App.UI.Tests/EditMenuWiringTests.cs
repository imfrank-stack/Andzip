using System.IO.Compression;
using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Menu;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Andzip.App.UiTests;

/// <summary>
/// 「编辑」那一排七项：模型里它们已经写着"实装过了"，而<strong>此刻能不能点</strong>要由窗口
/// 按当前开着什么包、选中了什么现算。
/// <para>
/// 全部从装好的 <see cref="MainWindow"/> 出发、按菜单项的 <c>Tag</c>（就是命令 id）认。
/// 不直接调 <c>UpdateEditMenuStates</c>：那等于把"这一项在屏幕上灰不灰"换成"这个方法算得对不对"，
/// 而这两件事之间正隔着"窗口有没有真的把它写进菜单"。
/// </para>
/// </summary>
[Collection("界面状态")]
public class EditMenuWiringTests
{
    /// <summary>首页上一条都不该亮：一条能点、点了什么也不发生的菜单比灰着危险得多。</summary>
    [AvaloniaFact]
    public void TheWholeEditRowIsGreyOnTheHomeScreen()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
        var menu = Assert.Single(VisualQuery.Of<Menu>(window));

        foreach (var command in MainMenuModel.SelectionCommands)
        {
            Assert.False(Item(menu, command).IsEnabled, $"首页上 {command} 不该可用");
        }
    }

    /// <summary>
    /// 开着 ZIP 但什么都没选：不需要选中的那三项（添加 / 粘贴 / 注释）先亮，
    /// 要选中才成立的那四项（改名 / 删除 / 复制 / 复制路径）仍然灰着。
    /// </summary>
    [AvaloniaFact]
    public void OpeningAZipLightsUpOnlyWhatDoesNotNeedASelection()
    {
        var directory = Directory.CreateTempSubdirectory("andzip-edit-menu-");
        try
        {
            var archive = CreateZip(directory.FullName, withDirectory: false);
            var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
            var menu = Assert.Single(VisualQuery.Of<Menu>(window));

            UiWait.Until(window.OpenArchive(archive), "打开压缩包");

            Assert.True(Item(menu, MainMenuModel.CommandAddEntries).IsEnabled);
            Assert.True(Item(menu, MainMenuModel.CommandPasteEntries).IsEnabled);
            Assert.True(Item(menu, MainMenuModel.CommandEditComment).IsEnabled, "ZIP 有归档注释这一格");

            foreach (var command in new[]
                     {
                         MainMenuModel.CommandRenameEntry, MainMenuModel.CommandDeleteEntries,
                         MainMenuModel.CommandCopyEntries, MainMenuModel.CommandCopyPaths,
                     })
            {
                Assert.False(Item(menu, command).IsEnabled, $"{command} 这时不该可用");
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>选中一个文件：四项全都亮起来。「选了什么」这一条只有真选中才问得出来。</summary>
    [AvaloniaFact]
    public void SelectingAFileLightsUpTheSelectionCommands()
    {
        var directory = Directory.CreateTempSubdirectory("andzip-edit-menu-");
        try
        {
            var archive = CreateZip(directory.FullName, withDirectory: false);
            var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
            var menu = Assert.Single(VisualQuery.Of<Menu>(window));

            UiWait.Until(window.OpenArchive(archive), "打开压缩包");
            Select(window, "a.txt");

            foreach (var command in MainMenuModel.SelectionCommands)
            {
                Assert.True(Item(menu, command).IsEnabled, $"选中一个文件之后 {command} 应当可用");
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// 目录不能改名（要连底下每一个成员的路径一起改），所以选中一个目录时只有改名这一项灰着，
    /// 删除与复制照样亮 —— 把整排一起灰掉是另一回事，用户会以为这个包不让动。
    /// </summary>
    [AvaloniaFact]
    public void RenameIsTheOnlyOneThatGoesGreyForADirectory()
    {
        var directory = Directory.CreateTempSubdirectory("andzip-edit-menu-");
        try
        {
            var archive = CreateZip(directory.FullName, withDirectory: true);
            var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
            var menu = Assert.Single(VisualQuery.Of<Menu>(window));

            UiWait.Until(window.OpenArchive(archive), "打开压缩包");
            Select(window, "dir");

            Assert.False(Item(menu, MainMenuModel.CommandRenameEntry).IsEnabled);
            Assert.True(Item(menu, MainMenuModel.CommandDeleteEntries).IsEnabled);
            Assert.True(Item(menu, MainMenuModel.CommandCopyEntries).IsEnabled);
            Assert.True(Item(menu, MainMenuModel.CommandCopyPaths).IsEnabled);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// 改不动的格式（GZ 能创建却没有可改写的目录结构）：增删改名与粘贴全灰、注释也灰，
    /// 但<strong>复制类照样能用</strong> —— 把整个「编辑」菜单一起灰掉是另一回事，
    /// 那等于告诉用户这个包什么都不让做。
    /// </summary>
    [AvaloniaFact]
    public void AFormatWeCannotRewriteLeavesOnlyTheCopyingCommandsUsable()
    {
        var directory = Directory.CreateTempSubdirectory("andzip-edit-menu-");
        try
        {
            var source = Path.Combine(directory.FullName, "note.txt");
            File.WriteAllText(source, "hello");

            var archive = Path.Combine(directory.FullName, "only.gz");
            ArchiveCreator.Create(
                [ArchiveInputItem.FromPath(source)],
                new ArchiveCreationSettings(
                    archive,
                    CompressionLevelOption.Store,
                    Format: ArchiveFormatOption.FromId("GZ"),
                    IncludeRootFolder: false));

            var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();
            var menu = Assert.Single(VisualQuery.Of<Menu>(window));

            UiWait.Until(window.OpenArchive(archive), "打开压缩包");

            foreach (var command in new[]
                     {
                         MainMenuModel.CommandRenameEntry, MainMenuModel.CommandAddEntries,
                         MainMenuModel.CommandDeleteEntries, MainMenuModel.CommandEditComment,
                         MainMenuModel.CommandPasteEntries,
                     })
            {
                Assert.False(Item(menu, command).IsEnabled, $"{command} 对改不动的格式不该可用");
            }

            Select(window, "note.txt");

            Assert.True(Item(menu, MainMenuModel.CommandCopyEntries).IsEnabled);
            Assert.True(Item(menu, MainMenuModel.CommandCopyPaths).IsEnabled);

            // 同一时刻改名仍然是灰的 —— 上面那两条复制不是"整排都被点亮了"。
            Assert.False(Item(menu, MainMenuModel.CommandRenameEntry).IsEnabled);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ------------------------------------------------------------------ 注释框的回车

    /// <summary>
    /// 注释那一格：回车是<strong>换行</strong>，不许顺手把窗口关掉 ——
    /// 一句注释写成两行是很正常的写法，而"回车即确定"在那里等于把后半句吃掉。
    /// <para>参数照着真调用处抄（<c>allowEmpty: true</c> + 字节上限）：留空也允许确定，
    /// 否则这一格里的回车本来就"确定"不了，这条断言等于没量到东西 —— 试过了，把
    /// <c>!multiline</c> 那道闸删掉它照过。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheCommentBoxKeepsEnterForNewlines()
    {
        var dialog = new TextPromptWindow(
            TestLang.Chinese(),
            "MENUSTR_EDIT_COMMENT",
            "TEXT_LABEL_ARCHIVE_COMMENT",
            multiline: true,
            allowEmpty: true,
            maxBytes: ZipComment.MaxBytes).Shown();

        var box = Assert.Single(VisualQuery.Of<TextBox>(dialog));
        dialog.Activate();
        UiWait.Pump(20);
        box.Focus();
        UiWait.Pump(20);
        Assert.True(box.IsFocused, "输入框没拿到键盘焦点");

        dialog.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiWait.Pump(40);

        Assert.True(dialog.IsVisible, "多行那一处回车是换行，不该把窗口关掉");
    }

    // ------------------------------------------------------------------ 辅助

    /// <summary>
    /// 造一个真 ZIP。<paramref name="withDirectory"/> 打开时里面还有一个真目录项 ——
    /// "选中目录不能改名"那条要的就是包里真的有一个目录，而不是一个名叫 dir 的文件。
    /// </summary>
    private static string CreateZip(string root, bool withDirectory)
    {
        var archive = Path.Combine(root, "box.zip");
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        Write(zip, "a.txt", "A");
        Write(zip, "b.txt", "B");

        if (withDirectory)
        {
            zip.CreateEntry("dir/");
            Write(zip, "dir/inner.txt", "I");
        }

        return archive;
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        using var stream = zip.CreateEntry(path).Open();
        stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// 在主列表里选中某一行，并且<strong>确认真的选上了</strong> —— 菜单还灰着的话，
    /// 断言量到的到底是"这一项不该亮"还是"其实没选中"，必须分得开。
    /// </summary>
    private static void Select(MainWindow window, string entryName)
    {
        var view = window.Browser ?? throw new InvalidOperationException("窗口里没有压缩包视图。");
        var entry = view.Listing.Entries.Single(e => e.Name == entryName);
        Assert.True(view.RevealEntry(entry), $"定位不到「{entryName}」那一行");
        Assert.Equal([entry.Path], view.SelectedRows.Select(e => e.Path).ToArray());
    }

    private static MenuItem Item(Menu menu, string commandId)
    {
        var matches = MenuWalk.All(menu).Where(item => Equals(item.Tag as string, commandId)).ToList();
        Assert.True(matches.Count == 1, $"菜单里 {commandId} 应该恰好一项，实际 {matches.Count} 项");
        return matches[0];
    }
}
