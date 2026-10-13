using Andzip.Core.Versioning;

namespace Andzip.Core.Tests;

public class VersionInfoTests
{
    [Fact]
    public void Parse_ValidIni()
    {
        var info = VersionInfo.Parse("""
            [Version]

            Version = 0.1.0
            BuildNo = 7
            update  = 0
            """);

        Assert.Equal("0.1.0", info.Version);
        Assert.Equal(7, info.BuildNo);
        Assert.False(info.Update);
        Assert.Equal("0.1.0.0", info.FileVersion);
        Assert.Equal("0.1.0+7", info.InformationalVersion);
    }

    [Fact]
    public void Parse_UpdateFlag()
    {
        var info = VersionInfo.Parse("Version = 1.2.3\nBuildNo = 1\nupdate = 1");

        Assert.True(info.Update);
    }

    [Fact]
    public void Parse_MissingVersion_Throws()
    {
        Assert.Throws<FormatException>(() => VersionInfo.Parse("BuildNo = 3"));
    }

    [Fact]
    public void Parse_InvalidVersion_Throws()
    {
        Assert.Throws<FormatException>(() => VersionInfo.Parse("Version = abc\nBuildNo = 3"));
    }

    [Fact]
    public void Parse_MissingBuildNo_Throws()
    {
        Assert.Throws<FormatException>(() => VersionInfo.Parse("Version = 0.1.0"));
    }

    [Fact]
    public void Parse_IgnoresComments()
    {
        var info = VersionInfo.Parse("; Version = 9.9.9\n[Version]\nVersion = 0.2.0\nBuildNo = 2");

        Assert.Equal("0.2.0", info.Version);
    }

    /// <summary>
    /// 编号规则是"每一位一个十进制位，满 9 向左边进一"，不是语义化版本。
    /// 这两条断言钉的是真实犯过的错：<c>0.13.0</c>（2026-10-13，应为 <c>1.3.0</c>）
    /// 与 <c>0.9.10</c>（更早，规则生效之前 shipped，不回改历史，但不能再写进 ini）。
    /// </summary>
    [Theory]
    [InlineData("0.13.0")]
    [InlineData("0.9.10")]
    [InlineData("1.0.11")]
    [InlineData("0.10.0")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0-beta")]
    public void Parse_RejectsAnyPartWiderThanOneDigit(string version)
    {
        var ex = Assert.Throws<FormatException>(() => VersionInfo.Parse($"Version = {version}\nBuildNo = 1"));

        // 光拒不够，还得告诉下次怎么进位 —— 否则被拒的人只会再猜一种写法。
        Assert.Contains("进一", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.9.9")]
    [InlineData("1.0.0")]
    [InlineData("1.3.0")]
    [InlineData("9.9.9")]
    public void Parse_AcceptsEverySingleDigitPart(string version)
    {
        Assert.Equal(version, VersionInfo.Parse($"Version = {version}\nBuildNo = 1").Version);
    }
}