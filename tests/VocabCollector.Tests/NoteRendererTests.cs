using System.Text.Json;
using VocabCollector.Core;
using VocabCollector.Core.Markdown;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class NoteRendererTests
{
    [Fact] public void Render_FixedFormatEscapesUntrustedContent()
    {
        using var vault = new TempVault(); var target = vault.Note("英语单词/名词/resilience.md");
        var analysis = TestData.Analysis with { Meanings = [new(PartOfSpeech.Adjective, "[[fake]]\n## injected <script> `code`")] };
        var text = new NoteRenderer().Render("resilient", "source\n## text", analysis, [new(RelationKind.SameRoot, target)], new DateTimeOffset(2026,10,21,15,0,0,TimeSpan.FromHours(8)));
        Assert.Contains("[[英语单词/名词/resilience|resilience]]", text);
        Assert.DoesNotContain("[[fake]]", text); Assert.DoesNotContain("\n## injected", text); Assert.DoesNotContain("<script>", text);
        Assert.Contains("US 未提供", text); Assert.Contains("> \\#\\# text", text);
        Assert.DoesNotContain("\r", text);
        var wordScalar = text.Split('\n').Single(x => x.StartsWith("word: "))[6..];
        Assert.Equal("resilient", JsonSerializer.Deserialize<string>(wordScalar));
        Assert.Contains("#### 我的笔记", text);
    }
    [Fact] public void Render_NoHistoricalLinksShowsEmptyMessage()
    {
        var text = new NoteRenderer().Render("resilient", "", TestData.Analysis, [], DateTimeOffset.Now);
        Assert.Contains("##### 近义词", text); Assert.Contains("resilience", text); Assert.DoesNotContain("[[", text);
    }
}
