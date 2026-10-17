using Andzip.Core.Archive;
using Andzip.Core.Lang;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Andzip.App.Views;

/// <summary>
/// 「要解压的文件」重名询问对话框（FR-3.7，语言包 IDD_ASK_DUPEFILE）：
/// 一次问清整批 —— 把目标里已经存在的同名文件列出来，选哪一颗就照那一颗办完这一次解压。
/// <para>5.2.0 之前这一扇是<strong>每个撞名文件弹一次</strong>：四个文件撞名就要点四次，
/// 而那四次问的其实是同一个问题（"这一次撞名的怎么算"）。用户的话是
/// 「应该是一键覆盖，或者一键跳过，不能是一个一个点」。</para>
/// <para>三颗按钮都写着"全部"：按下去管的是整批，不是屏幕上列出来的这一个 ——
/// 名字里不写清楚，用户会以为按「覆盖」只覆盖 `CODE_OF_CONDUCT.md`，剩下的还有得问。</para>
/// </summary>
public sealed class DuplicateFileWindow : Window
{
    /// <summary>屏幕上最多列出这几个名字；再多只显省略号（上面那句已经报了总数）。</summary>
    private const int ListedNames = 4;

    /// <summary>整批的处理决定（默认「全部重命名」）。仅在 ShowDialog 返回 true 后有效。</summary>
    public DuplicateDecision Decision { get; private set; } = DuplicateDecision.Rename;

    /// <param name="fileNames">目标里已经存在的那些同名文件（包内路径，顺序与清单一致）。
    /// 见 <see cref="ArchiveExtractor.FindCollisions"/>。</param>
    public DuplicateFileWindow(LangCatalog lang, IReadOnlyList<string> fileNames)
    {
        Title = lang.Label("TEXT_TITLE_ASK_DUPEFILE");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.WindowSurface();

        var body = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
        };
        body.Children.Add(new TextBlock
        {
            Text = lang.Format("TEXT_MSG_FILES_EXIST", ("count", fileNames.Count.ToString())),
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var name in fileNames.Take(ListedNames))
        {
            body.Children.Add(new TextBlock
            {
                Text = name,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (fileNames.Count > ListedNames)
        {
            body.Children.Add(new TextBlock
            {
                Text = "…",
                Foreground = Design.Ink.Secondary,
            });
        }

        var renameButton = new Button
        {
            Content = lang.Label("TEXT_BTN_RENAME_ALL"),
            MinWidth = 104,
            IsDefault = true,
        }.Accent();
        renameButton.Click += (_, _) => Choose(DuplicateDecision.Rename);
        var overwriteButton = new Button { Content = lang.Label("TEXT_BTN_OVERWRITE_ALL"), MinWidth = 104 }.Ghost();
        overwriteButton.Click += (_, _) => Choose(DuplicateDecision.Overwrite);
        var skipButton = new Button { Content = lang.Label("TEXT_BTN_SKIP_ALL"), MinWidth = 104, IsCancel = true }.Ghost();
        skipButton.Click += (_, _) => Choose(DuplicateDecision.Skip);

        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { renameButton, overwriteButton, skipButton },
        });

        this.Chromeless(lang, body);
    }

    private void Choose(DuplicateDecision decision)
    {
        Decision = decision;
        Close(true);
    }
}
