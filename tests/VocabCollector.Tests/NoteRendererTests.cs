using System.Text.Json;
using VocabCollector.Core;
using VocabCollector.Core.Markdown;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;
public class NoteRendererTests
{
    [Fact]
    public void CorrectionKeepsAnAuditOfOriginalInputAndUsesTheResolvedTitle()
    {
        var analysis = TestData.Analysis with { Spelling = new("introdce", "introduce", SpellingStatus.Corrected, []) };
        var text = new NoteRenderer().Render("introduce", "The original sentence contains introdce.", analysis, [], DateTimeOffset.Now);
        Assert.Contains("word: \"introduce\"", text);
        Assert.Contains("original_input: \"introdce\"", text);
        Assert.Contains("# introduce\n", text);
        Assert.DoesNotContain("# introdce\n", text);
        Assert.Contains("**拼写修正**：introdce → introduce", text);
        Assert.Contains("> The original sentence contains introdce.", text);
    }
    [Fact]
    public void ValidInflectionDoesNotShowASpellingCorrectionOrRenameToItsLemma()
    {
        var analysis = TestData.Analysis with { Lemma = "introduce", Spelling = new("introduced", "introduced", SpellingStatus.Valid, []) };
        var text = new NoteRenderer().Render("introduced", "", analysis, [], DateTimeOffset.Now);
        Assert.Contains("# introduced\n", text);
        Assert.DoesNotContain("original_input:", text);
        Assert.DoesNotContain("**拼写修正**", text);
    }
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
