using Andzip.Core.Lang;

namespace Andzip.Core.Tests;

public class LangLoaderTests
{
    [Theory]
    [InlineData("Andzip.App.langs.SimpChinese.lang", ".lang", "SimpChinese")]
    [InlineData("SimpChinese.lang", ".lang", "SimpChinese")]
    [InlineData("My.Cool.App.langs.English.lang", ".lang", "English")]
    [InlineData("NoSuffix", ".lang", "NoSuffix")]
    public void ExtractLanguageCode_HandlesDottedResourceNames(string name, string suffix, string expected)
        => Assert.Equal(expected, LangLoader.ExtractLanguageCode(name, suffix));

    [Fact]
    public void FromDirectory_LoadsAllLangFiles()
    {
        var dir = Directory.CreateTempSubdirectory("andzip-lang-");
        try
        {
            File.WriteAllText(
                Path.Combine(dir.FullName, "TestLang.lang"),
                "[GENERAL]\nLANG_NAME = Test\n[TEXT]\nTEXT_A = a");

            var items = LangLoader.FromDirectory(dir.FullName);

            var item = Assert.Single(items);
            Assert.Equal("TestLang", item.Code);
            Assert.Equal("Test", item.File.LangName);
            Assert.Equal("a", item.File.Get("TEXT_A"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FromDirectory_MissingDirectory_ReturnsEmpty()
    {
        var missing = Path.Combine(Path.GetTempPath(), "andzip-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(LangLoader.FromDirectory(missing));
    }

    [Fact]
    public void FromEmbedded_LoadsDottedResourceNamesAndExtractsCode()
    {
        // 测试程序集内嵌了与 App 相同机制的 langs（Andzip.Core.Tests.langs.SimpChinese.lang）
        var items = LangLoader.FromEmbedded(typeof(LangLoaderTests).Assembly);

        Assert.Contains(items, i => i.Code == "SimpChinese");
        Assert.Contains(items, i => i.Code == "English");

        var chinese = items.Single(i => i.Code == "SimpChinese").File;
        Assert.Equal("中文 (简体)", chinese.LangName);
        Assert.Equal("文件(&F)", chinese.Get("MENUSTR_FILE"));
    }

    [Fact]
    public void CreateCatalog_PrefersExternalDirectory_ThenEmbedded()
    {
        // 测试输出目录没有 langs/，因此走内嵌分支
        var catalog = LangLoader.CreateCatalog(typeof(LangLoaderTests).Assembly, AppContext.BaseDirectory);

        Assert.True(catalog.HasLanguage("SimpChinese"));
        Assert.Equal("SimpChinese", catalog.CurrentLanguage);
        Assert.Equal("文件(&F)", catalog.Get("MENUSTR_FILE"));

        // 再要一个键证明"不是只有那一条能取到"。这里**不断言文案内容** ——
        // 2.6.0 把工具栏那排标签从「打开」改成长标签「打开压缩包」，这条按字面比文案的断言
        // 就红了一次，而它想守的东西（内嵌那份读得到）一点没变。
        // 文案是语言包的事，归"两侧键集对齐"那类断言管；这一条只管"取到了真文案而不是键名"。
        var open = catalog.Get("TEXT_TOOLBAR_OPEN");
        Assert.False(string.IsNullOrWhiteSpace(open));
        Assert.NotEqual("TEXT_TOOLBAR_OPEN", open);
    }
}