using Andzip.Core.Lang;

namespace Andzip.Core.Tests;

public class LangFileTests
{
    private const string Sample = """
        ;
        ; comment
        [GENERAL]
        LANG_NAME = 中文 (简体)

        [TEXT]
        TEXT_A = hello
        TEXT_B = has = sign
        # hash comment
        TEXT_C = ${str1} 压缩文件

        [MENU]
        MENUSTR_FILE = 文件(&F)
        """;

    [Fact]
    public void Parse_ReadsSectionsAndKeys()
    {
        var file = LangFile.Parse(Sample);

        Assert.Equal("中文 (简体)", file.LangName);
        Assert.Equal("hello", file.Get("TEXT_A"));
        Assert.Equal("文件(&F)", file.Get("MENUSTR_FILE"));
    }

    [Fact]
    public void Parse_IgnoresCommentsAndBlankLines()
    {
        var file = LangFile.Parse(Sample);

        Assert.Equal(5, file.Count); // LANG_NAME + TEXT_A/B/C + MENUSTR_FILE
        Assert.NotNull(file.Get("TEXT_B"));
        Assert.NotNull(file.Get("TEXT_C"));
    }

    [Fact]
    public void Parse_KeepsEqualsInsideValue()
    {
        var file = LangFile.Parse(Sample);

        Assert.Equal("has = sign", file.Get("TEXT_B"));
    }

    [Fact]
    public void Get_RespectsSectionScope()
    {
        var file = LangFile.Parse(Sample);

        Assert.Equal("hello", file.Get("TEXT_A", "TEXT"));
        Assert.Null(file.Get("TEXT_A", "MENU"));
    }

    [Fact]
    public void ExpandBreaks_ConvertsMarker()
    {
        var expanded = LangFile.ExpandBreaks("line1[br]line2");

        Assert.Equal("line1" + Environment.NewLine + "line2", expanded);
    }

    [Fact]
    public void Parse_MissingLangName_ReturnsEmpty()
    {
        var file = LangFile.Parse("[TEXT]\nTEXT_A = 1");

        Assert.Equal(string.Empty, file.LangName);
    }
}