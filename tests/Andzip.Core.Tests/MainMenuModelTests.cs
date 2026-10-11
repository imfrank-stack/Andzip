using Andzip.Core.Menu;

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
}