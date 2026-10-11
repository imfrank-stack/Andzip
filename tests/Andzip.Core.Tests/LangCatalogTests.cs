using Andzip.Core.Lang;

namespace Andzip.Core.Tests;

public class LangCatalogTests
{
    private static LangCatalog CreateCatalog()
    {
        var catalog = new LangCatalog();
        catalog.Add("SimpChinese", LangFile.Parse("[GENERAL]\nLANG_NAME = 中文 (简体)\n[TEXT]\nTEXT_ONLY_ZH = 仅有中文\nTEXT_SHARED = 中文值\n"));
        catalog.Add("English", LangFile.Parse("[GENERAL]\nLANG_NAME = English\n[TEXT]\nTEXT_SHARED = english value\nTEXT_TEMPLATE = Archive: ${str1}\n"));
        return catalog;
    }

    [Fact]
    public void DefaultLanguage_IsEnglish()
    {
        var catalog = CreateCatalog();

        Assert.Equal(LangCatalog.DefaultLanguage, catalog.CurrentLanguage);
        Assert.Equal("english value", catalog.Get("TEXT_SHARED"));
    }

    [Fact]
    public void SetLanguage_SwitchesCurrent()
    {
        var catalog = CreateCatalog();

        Assert.True(catalog.SetLanguage("SimpChinese"));
        Assert.Equal("中文值", catalog.Get("TEXT_SHARED"));
    }

    [Fact]
    public void SetLanguage_UnknownCode_ReturnsFalse()
    {
        var catalog = CreateCatalog();

        Assert.False(catalog.SetLanguage("Klingon"));
        Assert.Equal(LangCatalog.DefaultLanguage, catalog.CurrentLanguage);
    }

    [Fact]
    public void Get_FallsBackToDefaultLanguage()
    {
        var catalog = CreateCatalog();
        catalog.SetLanguage("SimpChinese");

        // 中文包里没有的键 → 回退到 English
        Assert.Equal("Archive: ${str1}", catalog.Get("TEXT_TEMPLATE"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsKeyName()
    {
        var catalog = CreateCatalog();

        Assert.Equal("TEXT_NOT_EXIST", catalog.Get("TEXT_NOT_EXIST"));
    }

    [Fact]
    public void Format_ReplacesPlaceholders()
    {
        var catalog = CreateCatalog();

        Assert.Equal("Archive: demo.zip", catalog.Format("TEXT_TEMPLATE", ("str1", "demo.zip")));
    }

    [Fact]
    public void GetLanguageDisplayName_UsesLangName()
    {
        var catalog = CreateCatalog();

        Assert.Equal("中文 (简体)", catalog.GetLanguageDisplayName("SimpChinese"));
        Assert.Equal("Unknown", catalog.GetLanguageDisplayName("Unknown"));
    }

    [Fact]
    public void GetDisplay_ExpandsBreaks()
    {
        var catalog = new LangCatalog();
        catalog.Add("English", LangFile.Parse("TEXT_MULTI = a[br]b"));

        Assert.Equal("a" + Environment.NewLine + "b", catalog.GetDisplay("TEXT_MULTI"));
    }
}