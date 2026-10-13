using System.Threading;
using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Andzip.Core.Menu;
using Andzip.Core.Settings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Andzip.App.UiTests;

/// <summary>
/// 界面层的事实核查：这一轮加进去的东西<strong>真的出现在窗口上吗</strong>，还是只活在 Core 的测试里。
/// <para>
/// 全部从 <see cref="MainWindow"/> / <see cref="SettingsWindow"/> 这种真实装配好的窗口出发往下找，
/// 不去直接调用 internal 的工厂 —— 工厂能造出正确控件，不代表装进窗口之后还对。
/// </para>
/// </summary>
[Collection("界面状态")]
public class UiFeatureWiringTests
{
    // ------------------------------------------------ 主窗口：测试按钮与两项菜单

    [AvaloniaFact]
    public void MainWindowShowsAnEnabledTestButton()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings());

        var test = VisualQuery.Titled(window, lang.Get("TEXT_TOOLBAR_TEST"));

        Assert.NotEmpty(test);
        Assert.All(test, control => Assert.True(control.IsEnabled));
    }

    [Fact]
    public void MenuModelEnablesTestAndRepair()
    {
        var items = MainMenuModel.Create().SelectMany(menu => menu.Items).ToList();

        var test = items.Single(i => i.Id == MainMenuModel.CommandTestArchive);
        var repair = items.Single(i => i.Id == MainMenuModel.CommandRepairArchive);

        Assert.True(test.Enabled);
        Assert.True(repair.Enabled);
        Assert.Equal("Ctrl+T", test.Shortcut);
    }

    [AvaloniaFact]
    public void MainWindowMenuBarShowsTestAndRepairAsEnabledItems()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();
        var menu = Assert.Single(VisualQuery.Of<Menu>(window));
        var items = MenuWalk.All(menu).ToList();

        // 菜单显示的是去掉助记符之后的文案：语言包里写「测试压缩文件(&T)」，用户看到的是
        // 「测试压缩文件(T)」，所以按用户看到的形状比。
        var repair = items.SingleOrDefault(i => Equals(i.Header?.ToString(), lang.Get("MENUSTR_TOOLS_REPAIR")));
        var test = items.SingleOrDefault(i => Equals(i.Header?.ToString(),
            lang.Get("MENUSTR_TEST_ARCHIVE").Replace("&", string.Empty, StringComparison.Ordinal)));

        Assert.NotNull(repair);
        Assert.True(repair.IsEnabled, "禁用态的「修复压缩包」就是这一轮之前那个死占位项");

        Assert.NotNull(test);
        Assert.True(test.IsEnabled);

        // 菜单上印出来的快捷键文案是用户唯一能看到的承诺，它必须真的在那里。
        Assert.NotNull(test.InputGesture);
        Assert.Equal(Key.T, test.InputGesture.Key);
        Assert.Equal(KeyModifiers.Control, test.InputGesture.KeyModifiers);
    }

    [AvaloniaFact]
    public void MainWindowBindsCtrlPlusTToSomething()
    {
        var window = new MainWindow(TestLang.Chinese(), new AppSettings()).Shown();

        var bound = false;
        foreach (KeyBinding binding in window.KeyBindings)
        {
            if (binding.Gesture is { Key: Key.T, KeyModifiers: KeyModifiers.Control })
            {
                bound = true;
                break;
            }
        }

        // 菜单上写着 Ctrl+T 而窗口没绑 —— 那是给用户看的假快捷键。
        Assert.True(bound);
    }

    // ------------------------------------------------ 设置页：两个新勾选真的存下来

    [AvaloniaFact]
    public void CompressPageShowsBothNewTogglesOffByDefault()
    {
        // 这两个开关都会改变磁盘行为，默认必须是"和用户以前看到的一样"。
        var lang = TestLang.Chinese();
        var settingsPath = TempSettings();

        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), settingsPath).Shown();
            ShowCompressPage(window, lang);

            Assert.False(FindCheck(window, lang.Get("TEXT_SETTINGS_COMPRESS_PREALLOC")).IsChecked);
            Assert.False(FindCheck(window, lang.Get("TEXT_SETTINGS_COMPRESS_SHARE_SOURCES")).IsChecked);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [AvaloniaFact]
    public void CompressPageTogglesRoundTripThroughTheSettingsFile()
    {
        // 光"界面上有"不够：勾上、按应用、换一个新窗口读回来还是勾着的，才算真接上了存储。
        var lang = TestLang.Chinese();
        var settingsPath = TempSettings();

        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), settingsPath).Shown();
            ShowCompressPage(window, lang);

            FindCheck(window, lang.Get("TEXT_SETTINGS_COMPRESS_PREALLOC")).IsChecked = true;
            FindCheck(window, lang.Get("TEXT_SETTINGS_COMPRESS_SHARE_SOURCES")).IsChecked = true;

            var apply = FindButton(window, lang.Get("TEXT_BTN_APPLY"));
            Assert.True(apply.IsEnabled, "改了值，应用按钮就该可点");
            Clicker.Invoke(apply);

            Assert.True(File.Exists(settingsPath), "按了应用却没落盘");

            var reopened = new SettingsWindow(lang, AppSettings.Load(settingsPath), settingsPath).Shown();
            ShowCompressPage(reopened, lang);

            Assert.True(FindCheck(reopened, lang.Get("TEXT_SETTINGS_COMPRESS_PREALLOC")).IsChecked);
            Assert.True(FindCheck(reopened, lang.Get("TEXT_SETTINGS_COMPRESS_SHARE_SOURCES")).IsChecked);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    // ------------------------------------------------------ 修复对话框：三种模式

    [AvaloniaFact]
    public void RepairWindowOffersThreeDistinctModes()
    {
        var lang = TestLang.Chinese();
        var dialog = new RepairWindow(lang, @"C:\somewhere\broken.zip", @"C:\somewhere\broken.修复.zip");

        var radios = VisualQuery.Of<RadioButton>(dialog).ToList();

        Assert.Equal(3, radios.Count);
        Assert.Equal(
            new[] { "TEXT_REPAIR_MODE_1", "TEXT_REPAIR_MODE_2", "TEXT_REPAIR_MODE_3" }.Select(k => lang.Get(k)),
            radios.Select(r => r.Content?.ToString()));

        Assert.True(radios[0].IsChecked);
        Assert.Equal(ZipRepairMode.RebuildCentralDirectory, dialog.Mode);
    }

    [AvaloniaFact]
    public void RepairWindowHintFollowsTheSelectedMode()
    {
        var lang = TestLang.Chinese();
        var dialog = new RepairWindow(lang, @"C:\somewhere\broken.zip", @"C:\somewhere\broken.修复.zip");

        VisualQuery.Of<RadioButton>(dialog).ToList()[2].IsChecked = true;

        var texts = VisualQuery.Of<TextBlock>(dialog).Select(t => t.Text).ToList();
        Assert.Contains(lang.Get("TEXT_REPAIR_MODE_3_HINT"), texts);
        Assert.DoesNotContain(lang.Get("TEXT_REPAIR_MODE_1_HINT"), texts);
    }

    [AvaloniaFact]
    public void RepairWindowCarriesModeAndOutputBackToTheCaller()
    {
        var lang = TestLang.Chinese();
        const string suggested = @"C:\somewhere\broken.修复.zip";
        var dialog = new RepairWindow(lang, @"C:\somewhere\broken.zip", suggested);

        VisualQuery.Of<RadioButton>(dialog).ToList()[1].IsChecked = true;
        Clicker.Invoke(FindButton(dialog, lang.Get("TEXT_REPAIR_START")));

        Assert.Equal(ZipRepairMode.JoinTruncatedParts, dialog.Mode);
        Assert.Equal(suggested, dialog.OutputPath);
    }

    [AvaloniaFact]
    public void RepairWindowDoesNotReuseTheOriginalAsItsOutput()
    {
        var lang = TestLang.Chinese();
        const string original = @"C:\somewhere\broken.zip";
        var dialog = new RepairWindow(lang, original, ZipRepairer.SuggestOutputPath(original)).Shown();

        Assert.NotEqual(original, dialog.OutputPath);
        Assert.Contains(".修复", dialog.OutputPath, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void RepairWindowExplainsItselfInEnglishToo()
    {
        // 两套语言都得有这三段说明；漏一边就是另一种语言下满屏裸键名。
        var lang = TestLang.English();
        var dialog = new RepairWindow(lang, @"C:\somewhere\broken.zip", @"C:\somewhere\broken.repair.zip");

        var radios = VisualQuery.Of<RadioButton>(dialog).ToList();

        Assert.Equal(3, radios.Count);
        Assert.DoesNotContain(radios, r => r.Content?.ToString()?.StartsWith("TEXT_") == true);
        Assert.DoesNotContain(VisualQuery.Of<TextBlock>(dialog).Select(t => t.Text ?? string.Empty),
            text => text.StartsWith("TEXT_", StringComparison.Ordinal));
    }

    // ------------------------------------------------ 设置 → 解压：双击这一项搬过来了

    /// <summary>
    /// 「双击压缩包时」原本住在常规页，1.6.0 搬到解压页 —— 它选的落点规则就是同一页第一条。
    /// 搬家成不成，判据是两边各一侧：解压页有唯一一个下拉，常规页一个都不剩。
    /// 两页各留一份的话，两边会各自写同一个键，谁后落盘谁说了算。
    /// </summary>
    [AvaloniaFact]
    public void ExtractPageOwnsTheDoubleClickChoice()
    {
        var lang = TestLang.Chinese();
        var settingsPath = TempSettings();

        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), settingsPath).Shown();

            ShowPage(window, lang, "TEXT_SETTINGS_GENERAL");
            Assert.Empty(VisualQuery.Of<ComboBox>(window));

            ShowPage(window, lang, "TEXT_SETTINGS_EXTRACT");
            var combo = Assert.Single(VisualQuery.Of<ComboBox>(window));
            Assert.Equal(
                new[] { lang.Get("TEXT_SETTINGS_DOUBLE_CLICK_OPEN"), lang.Get("TEXT_SETTINGS_DOUBLE_CLICK_EXTRACT") },
                ItemsOf(combo));
            Assert.Equal(0, combo.SelectedIndex);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [AvaloniaFact]
    public void ExtractPageDoubleClickRoundTripsToTheStartupKey()
    {
        // 界面上选了不算数：启动时读的是 double_click_action，写的必须是同一个键。
        var lang = TestLang.Chinese();
        var settingsPath = TempSettings();

        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), settingsPath).Shown();
            ShowPage(window, lang, "TEXT_SETTINGS_EXTRACT");

            Assert.Single(VisualQuery.Of<ComboBox>(window)).SelectedIndex = 1;

            var apply = FindButton(window, lang.Get("TEXT_BTN_APPLY"));
            Assert.True(apply.IsEnabled, "改了双击行为，应用按钮就该可点");
            Clicker.Invoke(apply);

            var stored = AppSettings.Load(settingsPath).GetString(SettingKeys.DoubleClickAction);
            Assert.Equal("extract", stored);
            Assert.True(SettingKeys.ShouldExtractDirectly(stored));

            var reopened = new SettingsWindow(lang, AppSettings.Load(settingsPath), settingsPath).Shown();
            ShowPage(reopened, lang, "TEXT_SETTINGS_EXTRACT");

            Assert.Equal(1, Assert.Single(VisualQuery.Of<ComboBox>(reopened)).SelectedIndex);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [AvaloniaFact]
    public void GeneralPageOkDoesNotEraseWhatTheExtractPageApplied()
    {
        // 两页写同一个文件，落盘时机却不同：解压页点「立即应用」就走，常规页等到「确定」。
        // 后者过去拿窗口打开那一刻的整份快照写盘，正好把前者刚写的双击行为盖掉 ——
        // 和 0.13.0「设置不生效」是同一个成因，只是这次是页面之间互相盖。
        var lang = TestLang.Chinese();
        var settingsPath = TempSettings();

        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), settingsPath).Shown();
            ShowPage(window, lang, "TEXT_SETTINGS_EXTRACT");

            Assert.Single(VisualQuery.Of<ComboBox>(window)).SelectedIndex = 1;
            Clicker.Invoke(FindButton(window, lang.Get("TEXT_BTN_APPLY")));

            ShowPage(window, lang, "TEXT_SETTINGS_GENERAL");
            FindCheck(window, lang.Get("TEXT_SETTINGS_ESC_CANCEL")).IsChecked = false;

            // 「确定」在语言包里写着 确定(&O)，界面上印的是剥掉助记符之后的那一半。
            Clicker.Invoke(FindButton(window, lang.Label("TEXT_BTN_OK")));
            UiWait.Pump(50);

            var stored = AppSettings.Load(settingsPath);
            Assert.Equal("extract", stored.GetString(SettingKeys.DoubleClickAction));
            Assert.False(stored.GetBool(SettingKeys.AllowEscCancelProgress, fallback: true));
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    // ---------------------------------------------------------------- 辅助

    private static void ShowCompressPage(Window window, LangCatalog lang)
        => Clicker.Invoke(FindButton(window, lang.Get("TEXT_SETTINGS_COMPRESS")));

    /// <summary>点左侧导航进某一页。页与页之间控件会换，所以每次找都得在进页之后。</summary>
    private static void ShowPage(Window window, LangCatalog lang, string navKey)
    {
        Clicker.Invoke(FindButton(window, lang.Get(navKey)));
        UiWait.Pump(50);
    }

    // ---------------------------------------- 文案：Win32 的 (&X) 助记符不能露给用户

    /// <summary>
    /// 语言包沿用参考软件的 <c>确定(&amp;O)</c> 写法，而 Avalonia 的 SimpleTheme 不解析 <c>&amp;</c>，
    /// 于是用户真的看到「确定(&O)」。0.9.12 之前只有菜单栏与工具栏调了剥离，
    /// 对话框上的按钮全都漏了 —— 这条断言问的是"有没有哪个窗口还带着它"，
    /// 而不是"某个键有没有被剥"，所以将来新增窗口/按钮自动被覆盖。
    /// </summary>
    [AvaloniaFact]
    public void NoDialogShowsWin32MnemonicMarkers()
    {
        var lang = TestLang.Chinese();
        var archive = @"C:\somewhere\box.zip";

        AssertNoMarkerLeaked(new Window[]
        {
            new MessageWindow(lang, lang.Get("TEXT_TITLE_TEST_PASSED"), "所有成员校验通过。"),
            new NewArchiveWindow(lang),
            new ExtractPathWindow(lang, archive),
            new DuplicateFileWindow(lang, "a.txt"),
            new VolumeSizeWindow(lang, 100),
            new PasswordWindow(lang),
            new RepairWindow(lang, archive, @"C:\somewhere\box.修复.zip"),
            new ProgressWindow(lang, new CancellationTokenSource()),
            new MainWindow(lang, new AppSettings()),
        });
    }

    [AvaloniaFact]
    public void EverySettingsPageShowsWin32FreeLabels()
    {
        // 设置窗口九个页面是各自单独装配的，只看默认页等于没看另外八页。
        var lang = TestLang.Chinese();
        var settingsPath = TempSettings();

        try
        {
            var window = new SettingsWindow(lang, new AppSettings(), settingsPath).Shown();
            var leaked = new List<string>();

            foreach (var navKey in new[]
                     {
                         "TEXT_SETTINGS_GENERAL", "TEXT_SETTINGS_FILE_ASSOCIATION", "TEXT_SETTINGS_CONTEXT_MENU",
                         "TEXT_SETTINGS_COMPRESS", "TEXT_SETTINGS_EXTRACT", "TEXT_SETTINGS_VIEW",
                         "TEXT_SETTINGS_SECURITY", "TEXT_SETTINGS_ADVANCED", "TEXT_SETTINGS_LANGUAGE",
                     })
            {
                Clicker.Invoke(FindButton(window, lang.Get(navKey)));
                leaked.AddRange(VisualQuery.VisibleTexts(window).Where(TextCarriesMarker));
            }

            Assert.Empty(leaked);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    private static void AssertNoMarkerLeaked(IEnumerable<Window> windows)
        => Assert.Empty(windows.SelectMany(window => VisualQuery.VisibleTexts(window.Shown()).Where(TextCarriesMarker)));

    private static bool TextCarriesMarker(string text) => text.Contains("(&", StringComparison.Ordinal);

    // ------------------------------------------------------- 文件 → 最近的文件

    [AvaloniaFact]
    public void RecentFilesMenuIsEnabledAndListsWhatWasOpened()
    {
        var lang = TestLang.Chinese();
        var directory = Directory.CreateTempSubdirectory("andzip-menu-recent-");

        try
        {
            var archive = Path.Combine(directory.FullName, "box.zip");
            File.WriteAllText(archive, "x");

            var settings = new AppSettings();
            RecentFiles.Record(settings, archive);

            var window = new MainWindow(lang, settings).Shown();
            var menu = Assert.Single(VisualQuery.Of<Menu>(window));
            var recent = MenuWalk.All(menu).Single(i =>
                Equals(i.Header?.ToString(), lang.Get("MENUSTR_RECENT")));

            // 以前这一项是永久灰着的占位；现在它得真的能点开，并且里面有东西。
            Assert.True(recent.IsEnabled);
            Assert.Contains(recent.Items.Cast<MenuItem>(), entry =>
                Equals(entry.Header, Path.GetFullPath(archive)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [AvaloniaFact]
    public void RecentFilesMenuSaysSoWhenNothingHasBeenOpenedYet()
    {
        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();
        var menu = Assert.Single(VisualQuery.Of<Menu>(window));
        var recent = MenuWalk.All(menu).Single(i =>
            Equals(i.Header?.ToString(), lang.Get("MENUSTR_RECENT")));

        var placeholder = Assert.Single(recent.Items.Cast<MenuItem>());
        Assert.Equal(lang.Get("MENUSTR_RECENT_EMPTY"), placeholder.Header);
        Assert.False(placeholder.IsEnabled);
    }

    // ------------------------------------ 选择解压路径：浏览只走系统组件

    /// <summary>
    /// 0.11.0 之前右边那块是一棵手写的 <c>TreeView</c>，而且它<strong>根本展不开</strong> ——
    /// <c>ItemTemplate</c> 用的是扁平模板，没把子集合绑成子项来源，所以连展开三角都不出现。
    /// 参考软件那块是系统的 shell 树，嵌不进 Avalonia 窗口，所以浏览统一交给系统文件夹
    /// 选择对话框。这条断言钉的是"别再手写一棵回来"。
    /// </summary>
    [AvaloniaFact]
    public void ExtractPathDialogHasNoHandRolledFolderTree()
    {
        var lang = TestLang.Chinese();
        var dialog = new ExtractPathWindow(lang, @"C:\somewhere\box.zip").Shown();

        Assert.Empty(VisualQuery.Of<TreeView>(dialog));

        var browse = lang.Get("TEXT_BTN_BROWSE").Replace("&", string.Empty, StringComparison.Ordinal);
        Assert.Contains(VisualQuery.Of<Button>(dialog), button =>
            Equals(button.Content?.ToString(), browse) && button.IsEnabled);
    }

    [AvaloniaFact]
    public void ExtractPathDialogStartsAtTheArchiveFolder()
    {
        // "当前位置"默认就是包所在目录 —— 大多数时候用户只想原地解开。
        var dialog = new ExtractPathWindow(TestLang.Chinese(), @"C:\somewhere\box.zip").Shown();
        var path = VisualQuery.Of<ComboBox>(dialog).Single();

        Assert.True(path.IsEditable);
        Assert.Equal(@"C:\somewhere", path.Text);
    }

    // ---------------------------------- 新建压缩文件：默认包名与格式列表

    [AvaloniaFact]
    public void NewArchiveDialogNamesTheOutputAfterTheSingleThingBeingCompressed()
    {
        // 用户选中 photo.jpg 要压，「文件名」就该是 photo.zip，而不是「新建压缩.zip」——
        // 后者是个压完就得改名的名字。
        var (directory, file) = SeedFile("photo.jpg");

        try
        {
            var dialog = new NewArchiveWindow(TestLang.Chinese(), seedPaths: [file]).Shown();

            Assert.Equal(Path.Combine(directory, "photo.zip"), FileNameBox(dialog).Text);
            Assert.Contains("photo.jpg", InputRows(dialog));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void NewArchiveDialogFallsBackToTheGenericNameForSeveralInputs()
    {
        var directory = Directory.CreateTempSubdirectory("andzip-seed-").FullName;
        var first = Path.Combine(directory, "a.txt");
        var second = Path.Combine(directory, "b.txt");
        File.WriteAllText(first, "1");
        File.WriteAllText(second, "2");

        try
        {
            var dialog = new NewArchiveWindow(TestLang.Chinese(), seedPaths: [first, second]).Shown();

            Assert.Equal(Path.Combine(directory, "新建压缩.zip"), FileNameBox(dialog).Text);

            // 两个输入都要出现在列表里（一行有好几个文本单元，所以问"在不在"而不是数行数）。
            Assert.Contains("a.txt", InputRows(dialog));
            Assert.Contains("b.txt", InputRows(dialog));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void NewArchiveDialogListsOnlyFormatsItCanActuallyCreate()
    {
        var lang = TestLang.Chinese();
        var dialog = new NewArchiveWindow(lang, seedPaths: []).Shown();
        var listed = ItemsOf(FormatCombo(dialog));

        // 「该功能暂未实现」不该是一个能选中的选项。
        Assert.DoesNotContain("ZIPX", listed);
        Assert.DoesNotContain("ISO", listed);
        Assert.DoesNotContain("EXE", listed);
        Assert.Contains("ZIP", listed);
        Assert.Equal(
            [.. ArchiveFormatOption.Creatable.Select(format => format.Id)],
            listed);
    }

    private static (string Directory, string File) SeedFile(string name)
    {
        var directory = Directory.CreateTempSubdirectory("andzip-seed-").FullName;
        var file = Path.Combine(directory, name);
        File.WriteAllText(file, "seed");
        return (directory, file);
    }

    private static TextBox FileNameBox(Window window)
        => VisualQuery.Of<TextBox>(window).Single(box => box.Text?.EndsWith(".zip", StringComparison.Ordinal) == true);

    private static IReadOnlyList<string> InputRows(Window window)
        => VisualQuery.Of<TextBlock>(window).Select(t => t.Text ?? string.Empty).ToList();

    private static ComboBox FormatCombo(Window window)
        => VisualQuery.Of<ComboBox>(window).Single(combo => ItemsOf(combo).Contains("ZIP"));

    private static IReadOnlyList<string> ItemsOf(ItemsControl combo)
        => [.. combo.Items.Select(item => item?.ToString() ?? string.Empty)];

    // ------------------------------ 文件 → 另存为 / 删除 / 永久删除 / 打开位置

    /// <summary>
    /// 这四项都需要"正开着某个压缩包"。首页上它们必须<strong>是灰的</strong> ——
    /// 一条能点、点了什么也不发生的「删除压缩文件」比灰着危险得多。
    /// <para>认 CommandId 而不是标题：标题被助记符剥离过、换语言就变，而"这一项能不能点"
    /// 本来就是由命令 id 决定的。菜单里的 <c>Tag</c> 就是那个 id，置灰走的也是它。</para>
    /// </summary>
    [AvaloniaFact]
    public void ArchiveOnlyMenuItemsAreGreyedUntilAnArchiveIsOpen()
    {
        var lang = TestLang.Chinese();
        var directory = Directory.CreateTempSubdirectory("andzip-menu-archive-");

        try
        {
            var archive = CreatePlainArchive(directory.FullName, "box");
            var window = new MainWindow(lang, new AppSettings()).Shown();
            var menu = Assert.Single(VisualQuery.Of<Menu>(window));
            var onlyWhenOpen = MainMenuModel.ArchiveOnlyCommands;

            foreach (var command in onlyWhenOpen)
            {
                Assert.False(Item(menu, command).IsEnabled, $"首页上 {command} 不该可用");
            }

            UiWait.Until(window.OpenArchive(archive), "打开压缩包");

            foreach (var command in onlyWhenOpen)
            {
                Assert.True(Item(menu, command).IsEnabled, $"开着包时 {command} 应当可用");
            }

            // 关掉包之后要回到灰的 —— 置灰的逻辑只在切换视图时跑一次的话，这里就会漏掉。
            // 走菜单项本身而不是直接调方法：那才是用户点的那一下。
            Clicker.Invoke(Item(menu, MainMenuModel.CommandCloseArchive));

            foreach (var command in onlyWhenOpen)
            {
                Assert.False(Item(menu, command).IsEnabled, $"关掉包之后 {command} 又该是灰的");
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string CreatePlainArchive(string root, string stem)
    {
        var source = Path.Combine(root, stem + "-src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "hello");

        var archive = Path.Combine(root, stem + ".zip");
        ArchiveCreator.Create(
            [ArchiveInputItem.FromPath(source)],
            new ArchiveCreationSettings(archive, CompressionLevelOption.Store, Format: ArchiveFormatOption.FromId("ZIP")));
        return archive;
    }

    private static MenuItem Item(Menu menu, string commandId)
    {
        var matches = MenuWalk.All(menu).Where(item => Equals(item.Tag as string, commandId)).ToList();
        Assert.True(matches.Count == 1, $"菜单里 {commandId} 应该恰好一项，实际 {matches.Count} 项");
        return matches[0];
    }

    // ------------------------------------------------ 进度窗口：百分比与预估剩余时间

    [AvaloniaFact]
    public void ProgressWindowShowsPercentAndEstimatedTimeLeft()
    {
        var lang = TestLang.Chinese();
        var timeLeft = lang.Format("TEXT_PROGRESS_TIME_LEFT", ("time", string.Empty)).Trim();
        // 拿"去掉助记符之后"的那一份来比 —— 这才是窗口上真的显示的文字。
        var status = lang.Label("TEXT_STATUS_TESTING");
        using var cts = new CancellationTokenSource();
        var window = new ProgressWindow(lang, cts, "TEXT_STATUS_TESTING").Shown();

        // 按钮模板里也有一个 TextBlock（Avalonia 用的是 AccessText，它是 TextBlock 的子类），
        // 所以按"这一行的开头就是状态文案"来认，而不是"树里只有一个 TextBlock"。
        // 1.7.0 起还要排掉自绘标题栏那条 caption —— 它绑的就是 Window.Title，
        // 而进度窗口的标题正是同一句"正在检测…"，不排掉就有两个匹配。
        string Line() => VisualQuery.Of<TextBlock>(window)
            .Single(t => t.Text?.StartsWith(status, StringComparison.Ordinal) == true && !InsideTitleBar(t)).Text!;

        window.Update(new ArchiveProgress(ArchiveOperation.Test, 10, 1000, "noise.bin", 0, 1));

        // 起步那几帧不报剩余时间：样本太小，速度抖一下估算就能从 2 分钟跳到 40 分钟，
        // 一个乱跳的数字比没有数字更糟。
        Assert.DoesNotContain(timeLeft, Line(), StringComparison.Ordinal);

        window.Update(new ArchiveProgress(ArchiveOperation.Test, 500, 1000, "noise.bin", 0, 1));

        Assert.Contains("50%", Line(), StringComparison.Ordinal);
        Assert.Contains(timeLeft, Line(), StringComparison.Ordinal);

        // 占位符没被替换就是没接上，界面会直接显示 "${time}"。
        Assert.DoesNotContain("${time}", Line(), StringComparison.Ordinal);
    }

    private static Button FindButton(Window window, string text)
        => VisualQuery.Of<Button>(window).SingleOrDefault(b => Equals(b.Content?.ToString(), text))
           ?? throw new InvalidOperationException($"界面上找不到按钮「{text}」。");

    /// <summary>这个控件是不是在自绘标题栏里（那条 caption 与窗口标题同文本，按文字找控件时要排掉）。</summary>
    private static bool InsideTitleBar(Control control)
    {
        for (Control? at = control; at is not null; at = at.Parent as Control)
        {
            if (at is TitleBar)
            {
                return true;
            }
        }

        return false;
    }

    private static SettingToggle FindCheck(Window window, string label)
        => VisualQuery.Of<SettingToggle>(window).SingleOrDefault(c => Equals(c.Label, label))
           ?? throw new InvalidOperationException($"界面上找不到开关行「{label}」。");

    private static string TempSettings()
        => Path.Combine(Path.GetTempPath(), $"andzip-ui-{Guid.NewGuid():N}.ini");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
