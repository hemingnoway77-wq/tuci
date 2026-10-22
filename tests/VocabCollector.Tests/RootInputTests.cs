using VocabCollector.Core;
namespace VocabCollector.Tests;

public class RootInputTests
{
    [Theory]
    [InlineData(" DUC ", "duc")]
    [InlineData("-Duct-", "duct")]
    [InlineData("--mit--", "mit")]
    public void Normalize_AcceptsLettersAndOuterHyphens(string raw, string expected) => Assert.Equal(expected, RootInput.Normalize(raw));

    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("-")][InlineData("du-ct")][InlineData("two roots")]
    [InlineData("du2")][InlineData("dúc")][InlineData("_duc")][InlineData("../duc")][InlineData("duc/duct")]
    [InlineData("duc|alias")][InlineData("duc\nother")][InlineData("K")]
    public void Normalize_RejectsInvalidRoot(string raw) => Assert.Throws<ArgumentException>(() => RootInput.Normalize(raw));

    [Fact]
    public void Normalize_EnforcesLengthBoundary()
    {
        Assert.Equal(new string('a', 32), RootInput.Normalize("-" + new string('a', 32) + "-"));
        Assert.Throws<ArgumentException>(() => RootInput.Normalize(new string('a', 33)));
        Assert.Throws<ArgumentException>(() => RootInput.Normalize(null!));
    }
}
