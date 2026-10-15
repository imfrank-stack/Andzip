using System.Diagnostics;
using System.Globalization;
using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 「工具 → 密码恢复」：忘了自己设的口令时，把可能的口令<strong>一个一个真试一遍</strong>。
/// <para>这个对话框的全部设计压力都在一件事上：<strong>不许把"没试完"说成"没有"</strong>。
/// 穷举有三种结束 —— 找到、试完、被停止 —— 界面上就是三句话，共用一句"没找到"是在骗人。
/// 开工前还要过一次体检（<see cref="PasswordRecovery.Inspect"/>）：一个根本不是压缩包的文件
/// 每次尝试都会因为同一个原因失败，跑完一亿个之后说"没有"，那句话是假的。</para>
/// <para><strong>速度是现场量的</strong>：ZIP 与只加密数据的 7z 大约五千个一秒，
/// 头加密的 7z 只有两百个一秒，探针那一格大时更低。所以先跑一小段标定，再拿实测速度算 ETA，
/// 超过半小时就停下来问一句 —— 而不是让用户守一根不知道要走多久的进度条。</para>
/// <para>口令<strong>只在内存里</strong>：结果直接显示在这一屏上供复制，
/// 不写 <c>settings.ini</c>、不写注册表、不进日志。要不要顺手记进本次运行的口令表，
/// 是这一屏上一个单独的勾选，且默认不勾。</para>
/// </summary>
public sealed class PasswordRecoveryWindow : Window
{
    private readonly LangCatalog _lang;
    private readonly Func<Task<string?>> _pickArchive;
    private readonly Func<Task<string?>> _pickDictionary;
    private readonly Action<string, string> _remember;

    private readonly TextBox _target;
    private readonly RadioButton _bruteOption;
    private readonly RadioButton _dictionaryOption;
    private readonly ComboBox _charset;
    private readonly TextBox _customCharacters;
    private readonly ComboBox _minLength;
    private readonly ComboBox _maxLength;
    private readonly TextBox _dictionaryPath;
    private readonly TextBlock _summary;
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private readonly TextBox _found;
    private readonly CheckBox _remembered;
    private readonly Button _start;
    private readonly Button _stop;

    private CancellationTokenSource? _running;

    /// <summary>四种预设 + 自定义。字符表本身交给 <see cref="PasswordSpace"/>，这里只管选。</summary>
    private static readonly (string Key, string Characters)[] Presets =
    [
        ("TEXT_RECOVERY_SET_DIGITS", "0123456789"),
        ("TEXT_RECOVERY_SET_LOWER", "abcdefghijklmnopqrstuvwxyz"),
        ("TEXT_RECOVERY_SET_UPPER", "ABCDEFGHIJKLMNOPQRSTUVWXYZ"),
        ("TEXT_RECOVERY_SET_ALNUM", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"),
        ("TEXT_RECOVERY_SET_CUSTOM", string.Empty),
    ];

    public PasswordRecoveryWindow(
        LangCatalog lang,
        string? startArchive,
        Func<Task<string?>> pickArchive,
        Func<Task<string?>> pickDictionary,
        Action<string, string> remember)
    {
        _lang = lang;
        _pickArchive = pickArchive;
        _pickDictionary = pickDictionary;
        _remember = remember;

        Title = lang.Label("TEXT_TITLE_RECOVERY");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _target = new TextBox { IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch }.Field();
        _target.Text = startArchive ?? string.Empty;

        _bruteOption = new RadioButton { Content = lang.Label("TEXT_RECOVERY_MODE_BRUTE"), IsChecked = true, GroupName = "recovery" };
        _dictionaryOption = new RadioButton { Content = lang.Label("TEXT_RECOVERY_MODE_DICT"), GroupName = "recovery" };

        _charset = new ComboBox
        {
            MinWidth = 180,
            ItemsSource = Presets.Select(p => lang.Label(p.Key)).ToList(),
            SelectedIndex = 0,
        }.Picker();
        _customCharacters = new TextBox { Watermark = lang.Label("TEXT_RECOVERY_CUSTOM_HINT"), IsEnabled = false }.Field();
        _minLength = LengthPicker(4);
        _maxLength = LengthPicker(6);

        _dictionaryPath = new TextBox { IsReadOnly = true, IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch }.Field();

        _summary = new TextBlock
        {
            FontSize = Design.Type.Caption,
            Foreground = Design.Ink.Secondary,
            TextWrapping = TextWrapping.Wrap,
        };

        _progress = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 6, IsVisible = false };
        _status = new TextBlock
        {
            FontSize = Design.Type.Caption,
            Foreground = Design.Ink.Secondary,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        _found = new TextBox
        {
            IsReadOnly = true,
            IsVisible = false,
            FontFamily = new FontFamily("Consolas, monospace"),
        }.Field();

        _remembered = new CheckBox { Content = lang.Label("TEXT_RECOVERY_REMEMBER"), IsVisible = false, IsChecked = false };

        _start = new Button { Content = lang.Label("TEXT_RECOVERY_START"), MinWidth = 96 }.Accent();
        _stop = new Button { Content = lang.Label("TEXT_RECOVERY_STOP"), MinWidth = 96, IsEnabled = false }.Ghost();
        var close = new Button { Content = lang.Label("TEXT_BTN_CLOSE"), MinWidth = 96 }.Ghost();

        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += (_, _) => _running?.Cancel();
        _remembered.IsCheckedChanged += (_, _) => RememberIfChecked();
        close.Click += (_, _) =>
        {
            RememberIfChecked();
            Close();
        };
        _charset.SelectionChanged += (_, _) => UpdateMode();
        _bruteOption.IsCheckedChanged += (_, _) => UpdateMode();
        _dictionaryOption.IsCheckedChanged += (_, _) => UpdateMode();
        _minLength.SelectionChanged += (_, _) => UpdateMode();
        _maxLength.SelectionChanged += (_, _) => UpdateMode();
        _customCharacters.TextChanged += (_, _) => UpdateMode();
        _dictionaryPath.TextChanged += (_, _) => UpdateMode();

        var body = new StackPanel { Margin = new Thickness(Design.Space.X4), Spacing = Design.Space.X3 };

        body.Children.Add(new TextBlock { Text = lang.Label("TEXT_RECOVERY_INTRO"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Labeled(lang.Label("TEXT_RECOVERY_TARGET"), _target, BrowseAsync(_pickArchive, _target)));
        body.Children.Add(_bruteOption);
        // 四件控件挤一行会超出 620 DIP：真机截图上「最短」的下拉与整个「最长」都跑到窗口外面去了
        // （headless 只查控件在不在，查不出看不看得见 —— 与 2.5.0 那次同一族）。
        body.Children.Add(Indent(Row(_charset, _customCharacters)));
        body.Children.Add(Indent(Row(
            LabeledInline(lang.Label("TEXT_RECOVERY_MIN"), _minLength),
            LabeledInline(lang.Label("TEXT_RECOVERY_MAX"), _maxLength))));
        body.Children.Add(_dictionaryOption);
        body.Children.Add(Indent(Row(_dictionaryPath, BrowseAsync(_pickDictionary, _dictionaryPath))));

        body.Children.Add(_summary);
        body.Children.Add(_progress);
        body.Children.Add(_status);
        body.Children.Add(_found);
        body.Children.Add(_remembered);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X2,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(close);
        buttons.Children.Add(_stop);
        buttons.Children.Add(_start);
        body.Children.Add(buttons);

        this.Chromeless(lang, new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        UpdateMode();
    }

    /// <summary>
    /// 当前选法能开跑吗。<strong>存在的理由是让测试能断到"按钮为什么灰着"</strong>。
    /// <para>数不出精确总数的那一种<strong>算灰的</strong>：这一屏的承诺是"开跑之前把要跑多久说实话"，
    /// 而一个连总数都数不出来的范围没法说实话 —— 与其开一根永远走不完的进度条，不如不开。</para>
    /// </summary>
    internal bool CanStart => _running is null
        && _target.Text?.Length > 0
        && (DictionaryMode ? _dictionaryPath.Text?.Length > 0 : BuildSpace() is { IsBeyondReach: false });

    /// <summary>
    /// 这一小片只读口存在的理由和 <see cref="ArchiveView.CurrentFolderPath"/> 一样：
    /// 界面上"到底说的是哪一句"从窗口外面摸不稳（进度条那一块会随布局换控件），
    /// 而这几句实话正是这个对话框的全部承诺。
    /// </summary>
    internal bool StartEnabled => _start.IsEnabled;

    internal bool StopEnabled => _stop.IsEnabled;

    internal string StatusText => _status.Text ?? string.Empty;

    internal string? FoundPassword => _found.IsVisible ? _found.Text : null;

    internal bool OffersRemember => _remembered.IsVisible;

    internal bool RememberChecked => _remembered.IsChecked == true;

    private bool DictionaryMode => _dictionaryOption.IsChecked == true;

    private PasswordSpace? BuildSpace()
    {
        var preset = Presets[Math.Max(0, _charset.SelectedIndex)];
        var characters = preset.Key == "TEXT_RECOVERY_SET_CUSTOM" ? _customCharacters.Text ?? string.Empty : preset.Characters;
        var (space, _) = PasswordSpace.TryCreate(characters, MinLength, MaxLength);
        return space;
    }

    internal int MinLength => LengthAt(_minLength);

    internal int MaxLength => LengthAt(_maxLength);

    /// <summary>把当前选法写成的那句实话（候选总数；数不准时给数量级）。</summary>
    internal string SummaryText()
    {
        if (_running is not null)
        {
            return _summary.Text ?? string.Empty;
        }

        if (DictionaryMode)
        {
            return _dictionaryPath.Text?.Length > 0
                ? _lang.Label("TEXT_RECOVERY_DICT_READY")
                : _lang.Label("TEXT_RECOVERY_NEED_DICT");
        }

        var space = BuildSpace();
        if (space is null)
        {
            return _lang.Label("TEXT_RECOVERY_NO_SPACE");
        }

        return space.IsBeyondReach
            ? _lang.Format("TEXT_RECOVERY_SPACE_HUGE", ("size", space.DescribeCount(CultureInfo.CurrentCulture)))
            : _lang.Format("TEXT_RECOVERY_SPACE", ("count", space.DescribeCount(CultureInfo.CurrentCulture)));
    }

    private void UpdateMode()
    {
        var brute = !DictionaryMode;
        _charset.IsEnabled = brute;
        _minLength.IsEnabled = brute;
        _maxLength.IsEnabled = brute;
        _customCharacters.IsEnabled = brute && Presets[Math.Max(0, _charset.SelectedIndex)].Key == "TEXT_RECOVERY_SET_CUSTOM";
        _dictionaryPath.IsEnabled = !brute;

        // 最长不能短于最短：不自动纠正的话，用户会以为"0 个候选"是程序坏了。
        if (MinLength > MaxLength)
        {
            _maxLength.SelectedIndex = Math.Min(_maxLength.Items?.Count ?? 1, _minLength.SelectedIndex);
        }

        _summary.Text = SummaryText();
        _start.IsEnabled = CanStart;
    }

    private async Task StartAsync()
    {
        var archive = _target.Text ?? string.Empty;
        if (archive.Length == 0)
        {
            return;
        }

        _summary.Text = _lang.Label("TEXT_RECOVERY_CHECKING");
        var plan = await Task.Run(() => PasswordRecovery.Inspect(archive));
        if (!plan.CanSearch)
        {
            _summary.Text = plan.Target == PasswordRecoveryTarget.NotEncrypted
                ? _lang.Label("TEXT_RECOVERY_NOT_ENCRYPTED")
                : _lang.Label("TEXT_RECOVERY_UNOPENABLE");
            return;
        }

        var candidates = await BuildCandidatesAsync();
        if (candidates is null)
        {
            _summary.Text = _lang.Label("TEXT_RECOVERY_NO_SPACE");
            return;
        }

        var rate = await CalibrateAsync(archive, candidates, plan);
        if (CandidatesBeyondPatience(candidates.Count, rate))
        {
            var ask = _lang.Format("TEXT_RECOVERY_ASK_CONTINUE",
                ("count", candidates.Count.ToString("N0", CultureInfo.CurrentCulture)),
                ("rate", rate.ToString("N0", CultureInfo.CurrentCulture)),
                ("time", Humanize(TimeSpan.FromSeconds(candidates.Count / rate))));
            if (!await MessageWindow.ConfirmAsync(this, _lang, _lang.Label("TEXT_TITLE_RECOVERY"), ask, "TEXT_RECOVERY_CONTINUE"))
            {
                _summary.Text = _lang.Label("TEXT_RECOVERY_NOT_STARTED");
                return;
            }
        }

        await SearchAsync(archive, candidates, plan);
    }

    /// <summary>
    /// 先试一小段，量这台机器上的真实速度。
    /// <para>不这么做就只能拿一个写死的常数去算 ETA，而实测速度差着二十多倍
    /// （ZIP 约 5000/秒，头加密的 7z 约 200/秒，探针那一格大时更低）。</para>
    /// </summary>
    private async Task<double> CalibrateAsync(string archive, IPasswordCandidates candidates, PasswordRecoveryPlan plan)
    {
        var sample = (ulong)Math.Min(64, candidates.Count);
        var started = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            for (var index = 0UL; index < sample; index++)
            {
                PasswordRecovery.Accepts(archive, candidates.At(index), plan);
            }
        });
        started.Stop();
        return started.Elapsed.TotalSeconds > 0 ? sample / started.Elapsed.TotalSeconds : double.PositiveInfinity;
    }

    private async Task SearchAsync(string archive, IPasswordCandidates candidates, PasswordRecoveryPlan plan)
    {
        var cts = new CancellationTokenSource();
        _running = cts;
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        _progress.IsVisible = true;
        _status.IsVisible = true;
        _found.IsVisible = false;
        _remembered.IsVisible = false;

        // 交给后台线程的那一份必须是 UI 自己造的 sink：裸 Progress<T> 捕获的同步上下文
        // 可能是默认的，回调会落到线程池上（0.9.12 那次闪退的原因）。
        var sink = new Progress<PasswordRecoveryProgress>(OnProgress);

        try
        {
            var result = await Task.Run(
                () => PasswordRecovery.Run(archive, candidates, plan, sink, cts.Token),
                cts.Token);
            Show(result);
        }
        catch (OperationCanceledException)
        {
            Show(new PasswordRecoveryResult(PasswordRecoveryOutcome.Cancelled, null, 0, TimeSpan.Zero));
        }
        finally
        {
            _running = null;
            _stop.IsEnabled = false;
            _start.IsEnabled = CanStart;
        }
    }

    private void OnProgress(PasswordRecoveryProgress progress)
    {
        _progress.IsIndeterminate = false;
        _progress.Value = progress.Fraction;

        _status.Text = _lang.Format("TEXT_RECOVERY_TRIED", ("n", progress.Attempts.ToString("N0", CultureInfo.CurrentCulture)))
            + " · " + _lang.Format("TEXT_RECOVERY_RATE", ("rate", progress.AttemptsPerSecond.ToString("N0", CultureInfo.CurrentCulture)))
            + (progress.EstimatedRemaining is { } left
                ? " · " + _lang.Format("TEXT_RECOVERY_ETA", ("time", Humanize(left)))
                : string.Empty);
    }

    /// <summary>internal 而不是 private：三种结束在界面上必须是三句话，这一条得能在测试里各喂一次看到。</summary>
    internal void Show(PasswordRecoveryResult result)
    {
        _progress.IsVisible = false;
        _status.IsVisible = true;
        _status.Text = result.Outcome switch
        {
            PasswordRecoveryOutcome.Found => _lang.Label("TEXT_RECOVERY_FOUND"),
            PasswordRecoveryOutcome.Exhausted => _lang.Label("TEXT_RECOVERY_EXHAUSTED"),
            _ => _lang.Label("TEXT_RECOVERY_CANCELLED"),
        };

        if (result.Outcome != PasswordRecoveryOutcome.Found || result.Password is not { Length: > 0 } password)
        {
            return;
        }

        _found.Text = password;
        _found.IsVisible = true;
        _remembered.IsChecked = false;
        _remembered.IsVisible = true;
    }

    /// <summary>
    /// 口令只在这一屏上待着。<strong>勾了才进本次运行的口令表</strong>，而那张表本身也只活到进程结束 ——
    /// 恢复出来的口令写进 <c>settings.ini</c> 或注册表，等于把"忘了口令"变成"谁都能打开"。
    /// </summary>
    private void RememberIfChecked()
    {
        if (_remembered.IsChecked == true && _found.Text is { Length: > 0 } password && _target.Text is { Length: > 0 } archive)
        {
            _remember(archive, password);
        }
    }

    private static bool CandidatesBeyondPatience(ulong count, double rate)
        => count / Math.Max(1, rate) > TimeSpan.FromMinutes(30).TotalSeconds;

    private async Task<IPasswordCandidates?> BuildCandidatesAsync()
    {
        if (DictionaryMode)
        {
            var (dictionary, _) = PasswordDictionary.TryLoad(_dictionaryPath.Text ?? string.Empty);
            return dictionary;
        }

        return BuildSpace();
    }

    private string Humanize(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
        {
            return _lang.Format("TEXT_RECOVERY_SECONDS", ("n", Math.Ceiling(span.TotalSeconds).ToString("0", CultureInfo.CurrentCulture)));
        }

        if (span.TotalHours < 24)
        {
            return _lang.Format("TEXT_RECOVERY_MINUTES", ("n", Math.Ceiling(span.TotalMinutes).ToString("0", CultureInfo.CurrentCulture)));
        }

        return _lang.Format("TEXT_RECOVERY_DAYS",
            ("d", Math.Floor(span.TotalDays).ToString("0", CultureInfo.CurrentCulture)),
            ("h", Math.Floor(span.TotalHours % 24).ToString("0", CultureInfo.CurrentCulture)));
    }

    private ComboBox LengthPicker(int preferred)
    {
        var picker = new ComboBox
        {
            MinWidth = 72,
            ItemsSource = Enumerable.Range(1, PasswordSpace.MaxSupportedLength).ToList(),
        }.Picker();
        picker.SelectedIndex = Math.Clamp(preferred, 1, PasswordSpace.MaxSupportedLength) - 1;
        return picker;
    }

    private static int LengthAt(ComboBox picker) => picker.SelectedItem is int length ? length : 1;

    private Button BrowseAsync(Func<Task<string?>> pick, TextBox into)
    {
        var button = new Button { Content = _lang.Label("TEXT_RECOVERY_BROWSE"), MinWidth = 0 }.Ghost();
        button.Click += async (_, _) =>
        {
            if (await pick() is { } chosen)
            {
                into.Text = chosen;
            }
        };
        return button;
    }

    private static Control Labeled(string label, Control field, Control trailing)
    {
        var row = new StackPanel { Spacing = Design.Space.X1 };
        row.Children.Add(new TextBlock { Text = label, FontSize = Design.Type.Caption, Foreground = Design.Ink.Secondary });
        row.Children.Add(Row(field, trailing));
        return row;
    }

    private static Control LabeledInline(string label, Control field)
    {
        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pair.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Design.Ink.Secondary });
        pair.Children.Add(field);
        return pair;
    }

    private static Control Row(params Control[] children)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Design.Space.X2,
        };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private static Control Indent(Control child)
    {
        var pad = new StackPanel { Margin = new Thickness(Design.Space.X4, 0, 0, 0) };
        pad.Children.Add(child);
        return pad;
    }
}
