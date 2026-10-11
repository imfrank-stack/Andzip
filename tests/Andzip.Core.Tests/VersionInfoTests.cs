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
}