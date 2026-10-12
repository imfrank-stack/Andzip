using System.Text;
using Andzip.Core.Lang;

namespace Andzip.App;

/// <summary>
/// 助记符文案处理。
/// <para>
/// 语言包沿用参考软件的 Win32 写法（<c>文件(&amp;F)</c>），而 Avalonia 的 SimpleTheme 不解析
/// <c>&amp;</c> 访问键，于是它原样显示成「文件(&amp;F)」。这里在呈现前去掉标记，得到与参考软件
/// 一致的「文件(F)」；语言包本身保持原样，将来接真正的访问键时不用回头改文案。
/// </para>
/// </summary>
internal static class MnemonicText
{
    /// <summary>
    /// 取一条文案<em>用作控件文字</em>：顺手去掉 <c>(&amp;X)</c>。
    /// <para>凡是 <c>Content</c> / <c>Header</c> / <c>Text</c> / <c>Title</c> 都走这里，
    /// 不要直接 <c>Get</c> —— 0.9.12 之前只有菜单栏与工具栏调了 <see cref="Strip"/>，
    /// 对话框上的按钮就把「确定(&amp;O)」原样显示给用户看了。</para>
    /// </summary>
    public static string Label(this LangCatalog lang, string key, string? section = null)
        => Strip(lang.Get(key, section));

    /// <summary>去掉助记符标记：<c>文件(&amp;F)</c> → <c>文件(F)</c>；<c>&amp;&amp;</c> 是字面量 &amp;。</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('&', StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                builder.Append(text[i]);
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '&')
            {
                builder.Append('&');
                i++;
                continue;
            }

            // 单独的 &：丢掉标记本身，下一个字符照常输出。
        }

        return builder.ToString();
    }
}
