using VocabCollector.Core.Library;

namespace VocabCollector.Tests;

public class LibraryNoteTests
{
    [Fact]
    public void WordCard_ExtractsBodyWithoutLeakingFrontMatter()
    {
        var note = new LibraryNote("one", "动词/_con.md", "C:/vault/动词/_con.md", "---\r\ncreated: '2024-02-03T10:20:30+08:00'\r\nsecret: do not show\r\n---\r\n# con\r\n**发音**：UK /kɒn/ · US /kɑːn/\r\n\r\n**释义**：\r\n\r\n> v. 欺骗\r\n> n. 骗局\r\n\r\n#### 遇见时的原句\r\n> They conned him.\r\n\r\n#### 我的笔记\r\n", DateTimeOffset.UnixEpoch);

        Assert.Equal("con", note.Title);
        Assert.Equal("动词", note.Folder);
        Assert.True(note.IsWord);
        Assert.Equal("UK /kɒn/ · US /kɑːn/", note.Phonetics);
        Assert.Equal("v. 欺骗\nn. 骗局", note.Meaning);
        Assert.Contains("They conned him.", note.Excerpt);
        Assert.DoesNotContain("secret", note.Excerpt);
    }

    [Theory]
    [InlineData("词根/act.md", false)]
    [InlineData("词根/拉丁/act.md", false)]
    [InlineData("说明/学习方法.md", false)]
    [InlineData("other/hello-world.md", true)]
    [InlineData("don't.md", true)]
    [InlineData("plan 2.md", false)]
    public void RoamingEligibility_MatchesIphoneWordRules(string path, bool expected)
    {
        var note = new LibraryNote("id", path, path, "# readable", DateTimeOffset.UnixEpoch);
        Assert.Equal(expected, note.IsWord);
        Assert.NotEmpty(note.Excerpt);
    }
}
