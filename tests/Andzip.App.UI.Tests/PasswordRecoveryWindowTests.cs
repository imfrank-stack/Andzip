using System;
using System.Globalization;
using System.Linq;
using Andzip.App;
using Andzip.App.Views;
using Andzip.Core.Archive;
using Andzip.Core.Menu;
using Andzip.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Andzip.App.UiTests;

/// <summary>
/// 「工具 → 密码恢复」那一扇对话框。
/// <para>这里断言的不是"控件在不在"，而是<strong>它说的每句数与每句话对不对</strong>：
/// 候选个数必须是当前选法真正的数量，"停止"与"试完了"必须是两句话，
/// 而找到口令时默认<strong>不往任何地方写</strong>。</para>
/// </summary>
[Collection("界面状态")]
public class PasswordRecoveryWindowTests
{
    private static void Close(Window window)
    {
        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static PasswordRecoveryWindow Dialog(LangCatalogHolder holder, string? target)
        => new(holder.Value, target, () => Task.FromResult<string?>(null), () => Task.FromResult<string?>(null),
            (archive, password) => holder.Remembered.Add((archive, password)));

    /// <summary>一个既能当语言包用、又能记下"口令被写到哪儿去了"的容器。</summary>
    private sealed class LangCatalogHolder
    {
        public required Andzip.Core.Lang.LangCatalog Value { get; init; }

        public System.Collections.Generic.List<(string Archive, string Password)> Remembered { get; } = [];
    }

    private static LangCatalogHolder Holder() => new() { Value = TestLang.Chinese() };

    [AvaloniaFact]
    public void TheRangeIsNamedBeforeAnythingStarts()
    {
        var holder = Holder();
        var window = Dialog(holder, null).Shown();

        try
        {
            // 没选目标就不该能开始 —— 对着空气跑一亿次比不跑更糟。
            Assert.False(window.StartEnabled);

            // 默认就是数字 + 4~6 位：这一句必须报出这一个范围的<strong>精确</strong>个数。
            // 用相等而不是 Contains —— "10" 是 "1,111,100" 的子串，包含断言会放过一个数错的文案。
            var digits = (10_000 + 100_000 + 1_000_000UL).ToString("N0", CultureInfo.CurrentCulture);
            Assert.Equal(holder.Value.Format("TEXT_RECOVERY_SPACE", ("count", digits)), window.SummaryText());
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ATargetMakesItStartableAndTheCountFollowsThePicks()
    {
        var holder = Holder();
        var window = Dialog(holder, @"C:\somewhere\box.zip").Shown();

        try
        {
            Assert.True(window.StartEnabled, "有目标、范围也合法，「开始」却还灰着");

            var pickers = VisualQuery.Of<ComboBox>(window).ToArray();
            var before = window.SummaryText();

            // 最短拉到 1、最长收到 1：一位数字只有 10 个候选。
            pickers[1].SelectedIndex = 0;
            UiWait.Pump(30);
            pickers[2].SelectedIndex = 0;
            UiWait.Pump(30);

            Assert.Equal(1, window.MinLength);
            Assert.Equal(1, window.MaxLength);
            Assert.Equal(holder.Value.Format("TEXT_RECOVERY_SPACE", ("count", (10UL).ToString("N0", CultureInfo.CurrentCulture))),
                window.SummaryText());
            Assert.NotEqual(before, window.SummaryText());
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void TheLongestLengthNeverTrailsTheShortest()
    {
        var holder = Holder();
        var window = Dialog(holder, @"C:\somewhere\box.zip").Shown();

        try
        {
            var pickers = VisualQuery.Of<ComboBox>(window).ToArray();
            // 顺序：字符集、最短、最长。
            var min = pickers[1];
            var max = pickers[2];

            min.SelectedIndex = 9;   // 10 位
            UiWait.Pump(30);

            Assert.True(window.MaxLength >= window.MinLength,
                $"最长 {window.MaxLength} 掉到了最短 {window.MinLength} 之后 —— 那会组出一个 0 个候选的范围，界面上还写着话");
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 三种结束必须是三句话。<strong>"我停了"与"这个范围里没有"是两件事</strong>，
    /// 混成一句会让人以为自己的口令不在那个范围里，于是放弃一个本来能试出来的包。
    /// </summary>
    [AvaloniaFact]
    public void StoppedExhaustedAndFoundAreThreeDifferentSentences()
    {
        var holder = Holder();
        var window = Dialog(holder, @"C:\somewhere\box.zip").Shown();
        var lang = holder.Value;

        try
        {
            window.Show(new PasswordRecoveryResult(PasswordRecoveryOutcome.Cancelled, null, 42, TimeSpan.FromSeconds(1)));
            var stopped = window.StatusText;
            Assert.Null(window.FoundPassword);
            Assert.False(window.OffersRemember);

            window.Show(new PasswordRecoveryResult(PasswordRecoveryOutcome.Exhausted, null, 110, TimeSpan.FromSeconds(2)));
            var exhausted = window.StatusText;
            Assert.Null(window.FoundPassword);

            window.Show(new PasswordRecoveryResult(PasswordRecoveryOutcome.Found, "hunter2", 53, TimeSpan.FromSeconds(1)));
            var found = window.StatusText;

            Assert.NotEqual(stopped, exhausted);
            Assert.DoesNotContain(exhausted, stopped, StringComparison.Ordinal);
            Assert.Equal("hunter2", window.FoundPassword);
            Assert.True(window.OffersRemember);

            // 每一屏说的必须就是那一句：停止时不许混进"试完了"，找到时不许混进"没找到"。
            Assert.DoesNotContain(lang.Label("TEXT_RECOVERY_EXHAUSTED"), stopped, StringComparison.Ordinal);
            Assert.DoesNotContain(lang.Label("TEXT_RECOVERY_EXHAUSTED"), found, StringComparison.Ordinal);
            Assert.Contains(lang.Label("TEXT_RECOVERY_FOUND"), found, StringComparison.Ordinal);
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 找到的口令只显示在这一屏上。<strong>默认一个字节都不写</strong>；
    /// 只有用户亲手勾了"记到本次运行的口令表"才交给回调（那张表本身也只活到进程结束）。
    /// </summary>
    [AvaloniaFact]
    public void TheRecoveredPasswordIsNotWrittenAnywhereUntilAsked()
    {
        var holder = Holder();
        var window = Dialog(holder, @"C:\somewhere\box.zip").Shown();

        try
        {
            window.Show(new PasswordRecoveryResult(PasswordRecoveryOutcome.Found, "hunter2", 53, TimeSpan.FromSeconds(1)));

            Assert.Empty(holder.Remembered);
            Assert.False(window.RememberChecked, "默认就该是不勾 —— 口令落盘这件事不能是顺手发生的");

            var box = VisualQuery.Of<CheckBox>(window).Single();
            box.IsChecked = true;
            UiWait.Pump(30);

            Assert.Equal([(@"C:\somewhere\box.zip", "hunter2")], holder.Remembered);
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 大到数不出精确总数的那一种范围，<strong>「开始」必须是灰的</strong>，而且那一句要替它把理由说出来。
    /// <para>这一档的选择是"不开"而不是"开了慢慢跑"：一个连总数都数不出来的循环给不出进度、
    /// 也给不出"还要多久"，而这一屏的全部承诺就是<strong>开跑之前把时长说实话</strong>。
    /// 一根永远走不完的进度条不是"信息少一点"，是一个让人一直等下去的假象。</para>
    /// <para>断言写成"把语言包那句模板沿 <c>${size}</c> 切开、中间塞进 <c>10^21</c>"：
    /// 既钉住了"换成了数量级那一句"，也钉住了"理由还在同一行里"，
    /// 而不用把字符表抄第二份进测试。</para>
    /// </summary>
    [AvaloniaFact]
    public void ARangeTooBigToCountRefusesToStartAndSaysWhy()
    {
        var holder = Holder();
        var window = Dialog(holder, @"C:\somewhere\box.zip").Shown();

        try
        {
            Assert.True(window.StartEnabled, "默认的 4~6 位数字是数得清的范围，该能开跑");

            var pickers = VisualQuery.Of<ComboBox>(window).ToArray();
            pickers[0].SelectedIndex = 3;    // 字母 + 数字（62 个字符）
            UiWait.Pump(30);
            pickers[1].SelectedIndex = 11;   // 最短 12 位（最长会跟着追上来）
            UiWait.Pump(30);

            Assert.Equal(12, window.MinLength);
            Assert.Equal(12, window.MaxLength);
            Assert.False(window.StartEnabled,
                "62 个字符的 12 位数不出精确总数（62^12 ≈ 10^21 早就超过 64 位），这一种不该开跑");

            var template = holder.Value.Get("TEXT_RECOVERY_SPACE_HUGE");
            var parts = template.Split("${size}", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, parts.Length);
            Assert.Equal(parts[0] + "10^21" + parts[1], window.SummaryText());
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>
    /// 每一枚下拉框都必须<strong>整个落在窗口里</strong>。
    /// <para>2.9.0 的真机截图上，「最短」那颗下拉与整个「最长」都在窗口右边界之外 ——
    /// 那一行排了四件控件，620 DIP 装不下。当时 197 条界面用例全绿，
    /// 因为它只数得出"有三枚 ComboBox"，数不出"屏幕上看得见几枚"
    /// （与 2.5.0 那次「查看页整节内容在窗口外」同一族：headless 查在不在，真机才看得见看不见）。</para>
    /// </summary>
    [AvaloniaFact]
    public void EveryPickerStaysInsideTheWindow()
    {
        var holder = Holder();
        var window = Dialog(holder, @"C:\somewhere\box.zip").Shown();

        try
        {
            var width = window.Bounds.Width;
            var boxes = VisualQuery.Of<ComboBox>(window).ToArray();
            Assert.Equal(3, boxes.Length);

            foreach (var box in boxes)
            {
                Assert.True(box.Bounds.Width > 0 && box.Bounds.Height > 0, "一枚下拉框量出来是空的");

                var right = box.TranslatePoint(new Point(box.Bounds.Width, 0), window)!.Value.X;
                Assert.True(right <= width,
                    $"一枚下拉框的右沿落在 x={right:N0}，窗口只有 {width:N0} 宽 —— 它在屏幕上根本看不见");
            }
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RecoveryIsALiveItemInTheToolsMenu()
    {
        var recovery = MainMenuModel.Create().Single(m => m.Id == "tools").Items.Single(i => i.Id == "tools.recovery");
        Assert.True(recovery.Enabled, "「密码恢复」还是占位的禁用态");
        Assert.Equal(MainMenuModel.CommandRecoverPassword, recovery.CommandId);

        var lang = TestLang.Chinese();
        var window = new MainWindow(lang, new AppSettings()).Shown();
        try
        {
            var items = MenuWalk.All(Assert.Single(VisualQuery.Of<Menu>(window))).ToList();
            var shown = Assert.Single(items, i => Equals(i.Header?.ToString(),
                lang.Get("MENUSTR_TOOLS_RECOVERY").Replace("&", string.Empty, StringComparison.Ordinal)));
            Assert.True(shown.IsEnabled, "模型里已启用，界面上却是灰的");
        }
        finally
        {
            Close(window);
        }
    }
}
