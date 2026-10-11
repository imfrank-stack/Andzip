using Andzip.Core.Menu;

namespace Andzip.Core.Tests;

public class ShortcutSpecTests
{
    [Fact]
    public void Parse_CtrlO()
    {
        var spec = ShortcutSpec.Parse("Ctrl+O");

        Assert.NotNull(spec);
        Assert.True(spec!.HasModifier("Ctrl"));
        Assert.False(spec.HasModifier("Shift"));
        Assert.Equal("O", spec.Key);
        Assert.False(spec.IsFunctionKey);
    }

    [Fact]
    public void Parse_ShiftCtrlDel()
    {
        var spec = ShortcutSpec.Parse("Shift+Ctrl+Del");

        Assert.NotNull(spec);
        Assert.True(spec!.HasModifier("Shift"));
        Assert.True(spec.HasModifier("Ctrl"));
        Assert.Equal("Del", spec.Key);
    }

    [Fact]
    public void Parse_CtrlShiftN()
    {
        var spec = ShortcutSpec.Parse("Ctrl+Shift+N");

        Assert.NotNull(spec);
        Assert.True(spec!.HasModifier("Ctrl"));
        Assert.True(spec.HasModifier("Shift"));
        Assert.Equal("N", spec.Key);
    }

    [Fact]
    public void Parse_FunctionKey()
    {
        var spec = ShortcutSpec.Parse("F4");

        Assert.NotNull(spec);
        Assert.Empty(spec!.Modifiers);
        Assert.Equal("F4", spec.Key);
        Assert.True(spec.IsFunctionKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_NullOrEmpty_ReturnsNull(string? text)
    {
        Assert.Null(ShortcutSpec.Parse(text));
    }

    [Fact]
    public void Parse_UnknownModifier_ReturnsNull()
    {
        Assert.Null(ShortcutSpec.Parse("Meta+X"));
    }

    [Fact]
    public void ToString_ReturnsOriginalText()
    {
        var spec = ShortcutSpec.Parse("Ctrl+Alt+N");

        Assert.Equal("Ctrl+Alt+N", spec!.ToString());
    }
}