using Andzip.Core.Menu;
using Andzip.Core;

namespace Andzip.Core.Tests;

public class MainMenuModelTests
{
    [Fact]
    public void Create_HasSevenTopLevelMenus_InReferenceOrder()
    {
        var menus = MainMenuModel.Create();

        Assert.Equal(7, menus.Count);
        Assert.Equal(
            ["file", "edit", "find", "options", "view", "tools", "help"],
            menus.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void Items_HaveUniqueIdsAndTextKeys()
    {
        var menus = MainMenuModel.Create();
        var ids = new List<string>();

        foreach (var menu in menus)
        {
            foreach (var item in menu.Items)
            {
                if (item.IsSeparator)
                {
                    continue;
                }

                ids.Add(item.Id);
                Assert.False(string.IsNullOrWhiteSpace(item.TextKey), $"{item.Id} 缺少 TextKey");
            }
        }

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Exit_IsEnabledWithCommandAndShortcut()
    {
        var exit = MainMenuModel.Create()
            .SelectMany(m => m.Items)
            .Single(i => i.Id == "file.exit");

        Assert.True(exit.Enabled);
        Assert.Equal(MainMenuModel.CommandExit, exit.CommandId);
        Assert.Equal("Ctrl+W", exit.Shortcut);
        Assert.NotNull(exit.Gesture);
    }

    [Fact]
    public void NewArchive_IsEnabledWithCommandAndShortcut()
    {
        var item = MainMenuModel.Create()
            .SelectMany(m => m.Items)
            .Single(i => i.Id == "file.new");

        Assert.True(item.Enabled);
        Assert.Equal(MainMenuModel.CommandNewArchive, item.CommandId);
        Assert.Equal("Ctrl+N", item.Shortcut);
        Assert.NotNull(item.Gesture);
    }

    [Fact]
    public void AllDeclaredShortcuts_ParseSuccessfully()
    {
        var menus = MainMenuModel.Create();

        foreach (var item in menus.SelectMany(m => m.Items).Where(i => !i.IsSeparator && i.Shortcut is not null))
        {
            Assert.NotNull(item.Gesture);
        }
    }

    [Fact]
    public void MenuStructure_MatchesKeyAppendixItems()
    {
        var menus = MainMenuModel.Create();

        Assert.Contains(menus.Single(m => m.Id == "file").Items, i => i.Shortcut == "Ctrl+N");
        Assert.Contains(menus.Single(m => m.Id == "file").Items, i => i.Shortcut == "Shift+Ctrl+Del");
        Assert.Contains(menus.Single(m => m.Id == "edit").Items, i => i.Shortcut == "Ctrl+Shift+M");
        Assert.Contains(menus.Single(m => m.Id == "view").Items, i => i.Shortcut == "F6");
        Assert.Contains(menus.Single(m => m.Id == "help").Items, i => i.Shortcut == "F1");
    }

    [Fact]
    public void HelpMenuWiresWhatItClaimsAndDropsTheDuplicate()
    {
        var items = MainMenuModel.Create().Single(menu => menu.Id == "help").Items;

        var about = items.Single(i => i.Id == "help.about");
        Assert.True(about.Enabled);
        Assert.Equal(MainMenuModel.CommandAbout, about.CommandId);
        Assert.Equal("F1", about.Shortcut);

        var folder = items.Single(i => i.Id == "help.folder");
        Assert.True(folder.Enabled);
        Assert.Equal(MainMenuModel.CommandOpenInstallFolder, folder.CommandId);

        var homepage = items.Single(i => i.Id == "help.homepage");
        Assert.True(homepage.Enabled);
        Assert.Equal(MainMenuModel.CommandHomepage, homepage.CommandId);

        // 「转到用户论坛」还没有地址，所以它还得是灰的占位。拿官网凑一个是最容易犯、
        // 也最难看出来的假 —— 点了确实有浏览器弹出来，只是去的不是它说的那个地方。
        var forum = items.Single(i => i.Id == "help.forum");
        Assert.False(forum.Enabled);
        Assert.Null(forum.CommandId);

        // 「转到帮助页面」整条删掉：它和「转到官网」是同一个目标换两个名字。
        Assert.DoesNotContain(items, i => i.Id == "help.page");
    }

    /// <summary>
    /// 模型里 <c>Enabled: true</c> 的语义是"这条实装过了"，所以它必须报出一个命令 id。
    /// <para>另一半（报了 id 而窗口真的接了）由 <c>MainWindow</c> 构造时当场核对 ——
    /// 一条黑字、能点、点下去什么都不发生的菜单项，比灰着危险得多。</para>
    /// </summary>
    [Fact]
    public void EveryEnabledItemNamesACommand()
    {
        var nameless = MainMenuModel.Create().SelectMany(menu => menu.Items)
            .Where(item => item.Enabled && string.IsNullOrWhiteSpace(item.CommandId))
            .Select(item => item.Id)
            .ToArray();

        Assert.Empty(nameless);
    }

    [Fact]
    public void HomepageIsRegisteredOnceAndIsWhereTheProjectLives()
    {
        Assert.Equal("https://github.com/imfrank-stack/Andzip", AndzipLinks.Homepage);
    }
}
