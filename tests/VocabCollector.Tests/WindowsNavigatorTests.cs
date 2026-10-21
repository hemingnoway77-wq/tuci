using VocabCollector.Core;
using VocabCollector.Desktop;
namespace VocabCollector.Tests;
public class WindowsNavigatorTests
{
    [Theory][InlineData("C:\\英语 #库\\word & name.md")][InlineData("C:\\vault\\100% [word].md")]
    public void Uri_EncodesExactPathAsOneParameter(string path)
    {
        var uri=WindowsNavigator.BuildOpenUri(new NoteLocation("word",path,"word.md"));
        Assert.StartsWith("obsidian://open?path=",uri.OriginalString);
        Assert.Equal(path,Uri.UnescapeDataString(uri.Query[6..]));
        Assert.DoesNotContain("&",uri.Query);
    }
}
