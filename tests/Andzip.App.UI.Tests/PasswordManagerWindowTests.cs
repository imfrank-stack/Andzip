using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Andzip.App;
using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Menu;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Andzip.App.UiTests;

/// <summary>
/// 「选项 → 密码管理器」那一扇。
/// <para>这里问的是两句实话：屏幕上列出来的<strong>只有路径和时间</strong>（一次都没有口令），
/// 以及删一条真的少一条。存储是注入进来的假存储 —— 真凭据那一侧的往返在
/// <see cref="CredentialVaultTests"/>，因为在这一扇上点「全部删除」会真的清空这台机器。</para>
/// </summary>
[Collection("界面状态")]
public sealed class PasswordManagerWindowTests
{
    private const string Secret = "hunter2-绝不该出现在屏幕上";

    private static void Close(Window window)
    {
        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static FakePasswordStore StoreWith(params string[] archives)
    {
        var store = new FakePasswordStore();
        foreach (var archive in archives)
        {
            store.Save(archive, Secret);
        }

        return store;
    }

    [AvaloniaFact]
    public void TheListShowsPathsAndTimesAndNeverAPassword()
    {
        var lang = TestLang.Chinese();
        var first = @"D:\备份\tax-2025.zip";
        var second = @"D:\备份\photos.7z";
        var window = new PasswordManagerWindow(lang, StoreWith(first, second)).Shown();

        try
        {
            var visible = VisualQuery.VisibleTexts(window);

            // 前提：这两条真的在屏幕上看得见。没有这一半，下面那句"看不见口令"就是白过的。
            Assert.Contains(first, visible, StringComparer.Ordinal);
            Assert.Contains(second, visible, StringComparer.Ordinal);
            Assert.Equal(2, window.EntryCount);

            // 正题：屏幕上任何一处文案里都不许出现口令，连"部分匹配"都不许。
            Assert.DoesNotContain(visible, text => text.Contains(Secret, StringComparison.Ordinal));
            Assert.DoesNotContain(lang.Label("TEXT_PASSWORD_MANAGER_EMPTY"), window.SummaryText, StringComparison.Ordinal);
            Assert.Contains("2", window.SummaryText, StringComparison.Ordinal);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void EveryRowStaysInsideTheWindow()
    {
        var lang = TestLang.Chinese();
        var longPath = @"D:\" + new string('长', 60) + @"\archive-with-a-very-long-name.zip";
        var window = new PasswordManagerWindow(lang, StoreWith(longPath, @"C:\b.7z")).Shown();

        try
        {
            var width = window.Bounds.Width;
            foreach (var text in VisualQuery.Of<TextBlock>(window).Where(t => t.Text!.Contains(".zip") || t.Text.Contains(".7z")))
            {
                var right = text.TranslatePoint(new Avalonia.Point(text.Bounds.Width, 0), window)!.Value.X;
                Assert.True(right <= width,
                    $"一行文案的右沿落在 x={right:N0}，窗口只有 {width:N0} 宽 —— 长路径把这一扇撑破了");
            }
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void DeletingOneRowTakesItOutOfTheStoreAndOffTheScreen()
    {
        var lang = TestLang.Chinese();
        var keep = @"D:\keep.zip";
        var gone = @"D:\gone.zip";
        var store = StoreWith(keep, gone);
        var window = new PasswordManagerWindow(lang, store).Shown();

        try
        {
            Assert.False(window.DeleteOneEnabled, "还没选中任何一行，「删除选中」就该是灰的");

            var list = Assert.Single(VisualQuery.Of<ListBox>(window));
            var row = list.Items!.OfType<Control>().Single(c => (string)c.Tag! == Path.GetFullPath(gone));
            list.SelectedItem = row;
            UiWait.Pump(30);

            Assert.True(window.DeleteOneEnabled);
            Clicker.Invoke(VisualQuery.Titled(window, lang.Label("TEXT_PASSWORD_MANAGER_DELETE_ONE"))
                .OfType<Button>().Single());
            UiWait.Pump(30);

            Assert.Equal([keep], store.List().Select(e => e.ArchivePath).ToArray());
            Assert.Single(store.Stored);
            Assert.Equal(1, window.EntryCount);
            Assert.False(window.HasSelection, "删掉之后还留着选中项，下一次点删除会打到错位的那一行");
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void AnEmptyManagerSaysSoAndDisablesBothDeletes()
    {
        var lang = TestLang.Chinese();
        var window = new PasswordManagerWindow(lang, new FakePasswordStore()).Shown();

        try
        {
            Assert.Equal(0, window.EntryCount);
            Assert.Equal(lang.Label("TEXT_PASSWORD_MANAGER_EMPTY"), window.SummaryText);
            Assert.False(window.DeleteOneEnabled);
            Assert.False(window.DeleteAllEnabled, "一条都没有，「全部删除」不该按得动");
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 这台机器没有凭据存储时，这一扇说的是"这里没有"，而不是给一张空清单让人以为
    /// 自己存过又弄丢了。列表控件本身也不该出现在屏幕上（否则它是一个永远空的框）。
    /// </summary>
    [AvaloniaFact]
    public void AMachineWithoutACredentialStoreSaysThatInsteadOfShowingAnEmptyList()
    {
        var lang = TestLang.Chinese();
        var window = new PasswordManagerWindow(lang, null).Shown();

        try
        {
            Assert.Contains(lang.Label("TEXT_PASSWORD_MANAGER_UNSUPPORTED"), VisualQuery.VisibleTexts(window), StringComparer.Ordinal);
            Assert.DoesNotContain(lang.Label("TEXT_PASSWORD_MANAGER_INTRO"), VisualQuery.VisibleTexts(window), StringComparer.Ordinal);
            Assert.False(Assert.Single(VisualQuery.Of<ListBox>(window)).IsVisible);
            Assert.False(window.DeleteAllEnabled);
        }
        finally
        {
            Close(window);
        }
    }

    // ------------------------------------------------------------- 菜单

    /// <summary>
    /// 「选项」那一排现在只剩两条，而且两条都是活的。
    /// <para>「注册 Andzip」「取消注册 Andzip」是<strong>整条不存在</strong>而不是灰着 ——
    /// 它们从来没有接过线，真正的注册与撤销在设置的那两页上。留两条灰占位，
    /// 用户读到的是"这功能还没做"。</para>
    /// </summary>
    [AvaloniaFact]
    public void TheOptionsMenuHasTwoLiveItemsAndNoPlaceholdersLeft()
    {
        var lang = TestLang.Chinese();
        var options = MainMenuModel.Create().Single(m => m.Id == "options");
        var items = options.Items.Where(i => !i.IsSeparator).ToList();

        Assert.Equal(["options.settings", "options.passman"], items.Select(i => i.Id).ToArray());
        Assert.All(items, item => Assert.True(item.Enabled, $"{item.Id} 还是灰的"));
        Assert.Equal(MainMenuModel.CommandPasswordManager, items.Single(i => i.Id == "options.passman").CommandId);

        var window = new Andzip.App.MainWindow(lang, new Andzip.Core.Settings.AppSettings()).Shown();
        try
        {
            var shown = MenuWalk.All(Assert.Single(VisualQuery.Of<Menu>(window))).ToList();
            var manager = Assert.Single(shown, i => Equals(i.Header?.ToString(),
                lang.Get("MENUSTR_PASSMAN").Replace("&", string.Empty, StringComparison.Ordinal)));
            Assert.True(manager.IsEnabled, "模型里已启用，界面上却是灰的");

            // 语言包里那两个键已经删了，所以这里不能拿 lang.Get(...) 比 —— 那样它返回的是键名，
            // 断言会永远成立、永远什么都没说。查的是屏幕上这一行字根本没有出现过。
            Assert.DoesNotContain(shown, i => i.Header?.ToString()?.Contains("注册", StringComparison.Ordinal) == true);
        }
        finally
        {
            Close(window);
        }
    }
}
