using Andzip.Core.Archive;

namespace Andzip.Core.Tests;

/// <summary>候选空间本身：数得准、排得对、数不准时要老实说。</summary>
public class PasswordSpaceTests
{
    private static PasswordSpace Create(string characters, int min, int max)
    {
        var (space, error) = PasswordSpace.TryCreate(characters, min, max);
        Assert.Null(error);
        return space!;
    }

    [Fact]
    public void CountIsTheSumOverEveryLengthAndOrderingIsShortestFirst()
    {
        var digits = Create("0123456789", 1, 2);

        Assert.Equal(110UL, digits.Count);
        Assert.False(digits.IsBeyondReach);

        // 先短后长：10 个一位数，然后才是两位数。人记得住的口令通常短，这个顺序能早点撞上。
        Assert.Equal("0", digits.At(0));
        Assert.Equal("9", digits.At(9));
        Assert.Equal("00", digits.At(10));
        Assert.Equal("01", digits.At(11));
        Assert.Equal("99", digits.At(109));
    }

    [Fact]
    public void RepeatingACharacterDoesNotMakeTheSpaceBigger()
    {
        Assert.Equal(Create("aab", 1, 3).Count, Create("ab", 1, 3).Count);
    }

    [Fact]
    public void EveryIndexOfASmallSpaceYieldsADistinctCandidate()
    {
        var space = Create("ab", 1, 4);
        Assert.Equal(2 + 4 + 8 + 16UL, space.Count);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0UL; index < space.Count; index++)
        {
            var candidate = space.At(index);
            Assert.InRange(candidate.Length, 1, 4);
            Assert.All(candidate, c => Assert.Contains(c, "ab"));
            Assert.True(seen.Add(candidate), $"第 {index} 个候选 {candidate} 之前出现过");
        }
    }

    [Fact]
    public void AnUncountableSpaceSaysSoInsteadOfReportingAWrappedNumber()
    {
        // 95 个可打印字符的 12 位是 5.4e23 —— 装不进 64 位。这时候只能报数量级，
        // 报一个绕回来的小数就是拿一个假数字骗界面上的进度条与 ETA。
        var printable = new string(Enumerable.Range('!', 94).Select(i => (char)i).ToArray());
        var beyond = Create(printable, 12, 12);

        Assert.True(beyond.IsBeyondReach);
        // 只给幂 —— "约"是句式的一部分，归语言包。代码里带上它，中文就成了「约 约 10^23」，
        // 英文就成了「about 约 10^23」，一句里混两种语言。
        Assert.Matches(@"^10\^\d+$", beyond.DescribeCount(System.Globalization.CultureInfo.InvariantCulture));

        // 数不准的空间也不能按下标取 —— 那是拿一个假下标换一个假口令。
        Assert.Throws<InvalidOperationException>(() => beyond.At(0));

        var fits = Create("abcdefghijklmnopqrstuvwxyz", 1, 12);
        Assert.False(fits.IsBeyondReach);
        Assert.Equal("zzzzzzzzzzzz", fits.At(fits.Count - 1));
    }

    [Fact]
    public void NonsenseRangesAreRefusedWithAShowableReason()
    {
        Assert.NotNull(PasswordSpace.TryCreate("", 1, 4).Error);
        Assert.NotNull(PasswordSpace.TryCreate("0123456789", 0, 4).Error);
        Assert.NotNull(PasswordSpace.TryCreate("0123456789", 5, 4).Error);
        Assert.NotNull(PasswordSpace.TryCreate("0123456789", 1, 99).Error);
    }
}

/// <summary>字典候选：空行与重复行不能混进"我试了多少个"里。</summary>
public class PasswordDictionaryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("andzip-dict-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private PasswordDictionary Load(string content)
    {
        var path = Path.Combine(_root, Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, content);
        var (dictionary, error) = PasswordDictionary.TryLoad(path);
        Assert.Null(error);
        return dictionary!;
    }

    [Fact]
    public void BlanksAndDuplicatesAreDroppedBeforeCounting()
    {
        var dictionary = Load("hunter2\r\n\r\n  \nhunter2\npassword\nsecret\n");

        Assert.Equal(3UL, dictionary.Count);
        Assert.Equal("hunter2", dictionary.At(0));
        Assert.Equal("password", dictionary.At(1));
        Assert.Equal("secret", dictionary.At(2));
    }

    [Fact]
    public void AMissingOrEmptyDictionaryIsNamedNotSwallowed()
    {
        Assert.NotNull(PasswordDictionary.TryLoad(Path.Combine(_root, "nope.txt")).Error);

        var empty = Path.Combine(_root, "empty.txt");
        File.WriteAllText(empty, "\n\n");
        Assert.NotNull(PasswordDictionary.TryLoad(empty).Error);
    }
}
