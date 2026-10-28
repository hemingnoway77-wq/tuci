using VocabCollector.Core;
using VocabCollector.Core.Ai;
using VocabCollector.Core.Storage;
using VocabCollector.Tests.Fakes;
namespace VocabCollector.Tests;

public class SpellingCaptureIntegrationTests
{
    [Fact]
    public async Task ClearCorrectionUsesOneResolvedWordForTitleFileClassificationAndMessage()
    {
        using var vault = new TempVault();
        var analyzer = new FakeWordAnalyzer { Analyze = _ => Task.FromResult(Corrected()) };
        var store = new CountingStore();
        var result = await Capture(vault, analyzer, store, "  Introdce  ");

        Assert.Equal(CaptureStatus.Saved, result.Status);
        Assert.Equal("introduce", result.Word);
        Assert.Equal("introdce", result.CorrectedFrom);
        Assert.StartsWith("拼写已修正：introdce → introduce。", result.Message);
        var note = Assert.Single(result.Notes);
        Assert.Equal("introduce", note.Word);
        Assert.EndsWith("英语单词/动词/introduce.md", note.AbsolutePath.Replace('\\', '/'));
        var text = await File.ReadAllTextAsync(note.AbsolutePath);
        Assert.Equal(result.Markdown, text);
        Assert.Contains("word: \"introduce\"", text);
        Assert.Contains("# introduce\n", text);
        Assert.Contains("**introduce**", text);
        Assert.DoesNotContain("# introdce", text);
        Assert.Single(Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories));
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(1, store.Scans);
    }

    [Fact]
    public async Task CorrectionKeepsExistingTargetWithoutOverwritingOrCreatingTypo()
    {
        using var vault = new TempVault();
        var target = vault.Note("英语单词/动词/introduce.md", "my target notes");
        var analyzer = new FakeWordAnalyzer { Analyze = _ => Task.FromResult(Corrected()) };
        var store = new CountingStore();
        var result = await Capture(vault, analyzer, store);

        AssertCorrectedDuplicate(result);
        Assert.Equal(target.AbsolutePath, Assert.Single(result.Notes).AbsolutePath);
        Assert.Equal("my target notes", await File.ReadAllTextAsync(target.AbsolutePath));
        Assert.Single(Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories));
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(1, store.Scans);
    }

    [Fact]
    public async Task CorrectedTargetAppearingAtSaveStageWinsTheLockedDuplicateCheck()
    {
        using var vault = new TempVault();
        var analyzer = new FakeWordAnalyzer { Analyze = _ => Task.FromResult(Corrected()) };
        var progress = new InlineProgress<CaptureStage>(stage =>
        {
            if (stage == CaptureStage.Saving) vault.Note("英语单词/其他/introduce.md", "race winner");
        });
        var result = await Capture(vault, analyzer, new VaultStore(), progress: progress);

        AssertCorrectedDuplicate(result);
        Assert.Equal("race winner", await File.ReadAllTextAsync(Assert.Single(result.Notes).AbsolutePath));
        Assert.Single(Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories));
        Assert.Equal(1, analyzer.Calls);
    }

    [Fact]
    public async Task MultipleExistingCorrectedTargetsRemainAvailableForSelection()
    {
        using var vault = new TempVault();
        vault.Note("英语单词/其他/introduce.md", "one");
        vault.Note("英语单词/动词/introduce.md", "two");
        var result = await Capture(vault, new FixedAnalyzer(Corrected()), new VaultStore());

        AssertCorrectedDuplicate(result);
        Assert.Equal(2, result.Notes.Count);
        Assert.Contains("选择", result.Message);
        Assert.Equal(2, Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task ExistingTypedWordStillSkipsAi()
    {
        using var vault = new TempVault();
        var original = vault.Note("英语单词/其他/introdce.md", "personal spelling note");
        var analyzer = new FakeWordAnalyzer { Analyze = _ => throw new InvalidOperationException("AI must not run") };
        var result = await Capture(vault, analyzer, new VaultStore());

        Assert.Equal(CaptureStatus.AlreadyExists, result.Status);
        Assert.Equal("introdce", result.Word);
        Assert.Empty(result.CorrectedFrom);
        Assert.Equal(0, analyzer.Calls);
        Assert.Equal("personal spelling note", await File.ReadAllTextAsync(original.AbsolutePath));
    }

    [Fact]
    public async Task ValidInflectionKeepsTheSurfaceWordDespiteItsLemma()
    {
        using var vault = new TempVault();
        var analysis = Analysis("introduced") with
        {
            Lemma = "introduce",
            Spelling = new("introduced", "introduced", SpellingStatus.Valid, [])
        };
        var result = await Capture(vault, new FixedAnalyzer(analysis), new VaultStore(), "introduced");

        Assert.Equal(CaptureStatus.Saved, result.Status);
        Assert.Equal("introduced", result.Word);
        Assert.Empty(result.CorrectedFrom);
        Assert.Equal("introduced.md", Path.GetFileName(Assert.Single(result.Notes).AbsolutePath));
        Assert.Contains("# introduced\n", result.Markdown);
        Assert.Contains("lemma: \"introduce\"", result.Markdown);
    }

    [Fact]
    public async Task UncertainSuggestionsAreShownWithoutCreatingAnyCandidateNotes()
    {
        using var vault = new TempVault();
        var analysis = Rejected(new("ca", "", SpellingStatus.Uncertain, ["cat", "car", "can"]));
        var result = await Capture(vault, new FixedAnalyzer(analysis), new VaultStore(), "ca");

        Assert.Equal(CaptureStatus.InvalidInput, result.Status);
        Assert.Equal("ca", result.Word);
        Assert.Empty(result.CorrectedFrom);
        Assert.Contains("cat", result.Message);
        Assert.Contains("car", result.Message);
        Assert.Contains("can", result.Message);
        Assert.DoesNotContain(analysis.Error, result.Message);
        AssertNoSavedFiles(vault, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidAndLegacyRejectedAnalysesShowGenericErrorWithoutSaving(bool includeSpelling)
    {
        using var vault = new TempVault();
        var result = await Capture(vault, new FixedAnalyzer(Rejected(includeSpelling
            ? new("introdce", "", SpellingStatus.Invalid, []) : null)), new VaultStore());

        Assert.Equal(CaptureStatus.InvalidInput, result.Status);
        Assert.Equal("introdce", result.Word);
        Assert.DoesNotContain("untrusted", result.Message);
        Assert.Empty(result.CorrectedFrom);
        AssertNoSavedFiles(vault, result);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("input-mismatch")]
    [InlineData("valid-different-target")]
    [InlineData("unrelated-target")]
    [InlineData("example-mismatch")]
    [InlineData("too-many-suggestions")]
    public async Task UnsafeSpellingMetadataFailsClosedWithoutSaving(string scenario)
    {
        using var vault = new TempVault();
        var analysis = scenario switch
        {
            "missing" => Corrected() with { Spelling = null },
            "input-mismatch" => Corrected() with { Spelling = new("another", "introduce", SpellingStatus.Corrected, []) },
            "valid-different-target" => Corrected() with { Spelling = new("introdce", "introduce", SpellingStatus.Valid, []) },
            "unrelated-target" => Analysis("resilient") with { Spelling = new("introdce", "resilient", SpellingStatus.Corrected, []) },
            "example-mismatch" => Corrected() with { Examples = TestData.Analysis.Examples },
            _ => Rejected(new("introdce", "", SpellingStatus.Uncertain, ["introduce", "introduced", "intro", "inside"]))
        };
        var result = await Capture(vault, new FixedAnalyzer(analysis), new VaultStore());

        Assert.Equal(CaptureStatus.Failed, result.Status);
        Assert.Equal("introdce", result.Word);
        Assert.Empty(result.CorrectedFrom);
        AssertNoSavedFiles(vault, result);
    }

    [Fact]
    public async Task CancellationAfterCorrectionKeepsInputAndDoesNotCreateATypoNote()
    {
        using var vault = new TempVault();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<CaptureStage>(stage =>
        {
            if (stage == CaptureStage.Saving) cancellation.Cancel();
        });
        var result = await Capture(vault, new FixedAnalyzer(Corrected()), new VaultStore(), progress: progress, ct: cancellation.Token);

        Assert.Equal(CaptureStatus.Cancelled, result.Status);
        Assert.Equal("introdce", result.Word);
        Assert.Empty(result.CorrectedFrom);
        Assert.Contains("# introduce\n", result.Markdown);
        Assert.Empty(result.Notes);
        Assert.Empty(Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SaveFailureKeepsCorrectedPreviewButRetainsOriginalInput()
    {
        using var vault = new TempVault();
        Directory.CreateDirectory(Path.Combine(vault.Root, "英语单词", "动词", "introduce.md"));
        var result = await Capture(vault, new FixedAnalyzer(Corrected()), new VaultStore());

        Assert.Equal(CaptureStatus.Failed, result.Status);
        Assert.Equal("introdce", result.Word);
        Assert.Empty(result.CorrectedFrom);
        Assert.Contains("# introduce\n", result.Markdown);
        Assert.Empty(result.Notes);
        Assert.Empty(Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationAfterCorrectedCommitStillReportsCorrectedSavedWord()
    {
        using var vault = new TempVault();
        using var cancellation = new CancellationTokenSource();
        var result = await Capture(vault, new FixedAnalyzer(Corrected()), new CancelAfterCommitStore(cancellation), ct: cancellation.Token);

        Assert.Equal(CaptureStatus.Saved, result.Status);
        Assert.Equal("introduce", result.Word);
        Assert.Equal("introdce", result.CorrectedFrom);
        Assert.StartsWith("拼写已修正：introdce → introduce。", result.Message);
        Assert.True(File.Exists(Assert.Single(result.Notes).AbsolutePath));
    }

    [Fact]
    public async Task CorrectedIdentityOwnsTheCachedExampleLinksForFutureBackfill()
    {
        using var vault = new TempVault();
        var corrected = await Capture(vault, new FixedAnalyzer(Corrected() with
        {
            Examples = [new("We introduce a new policy.", "我们推出新政策。"), new("They introduce another policy.", "他们推出另一项政策。")]
        }), new VaultStore());
        Assert.Equal(CaptureStatus.Saved, corrected.Status);
        var saved = Assert.Single(corrected.Notes);
        var policy = Analysis("policy") with
        {
            PrimaryPartOfSpeech = PartOfSpeech.Noun,
            Meanings = [new(PartOfSpeech.Noun, "政策")],
            Spelling = new("policy", "policy", SpellingStatus.Valid, [])
        };
        var result = await Capture(vault, new FixedAnalyzer(policy), new VaultStore(), "policy");

        Assert.Equal(CaptureStatus.Saved, result.Status);
        var refreshed = await File.ReadAllTextAsync(saved.AbsolutePath);
        Assert.Contains("[[英语单词/名词/policy|policy]]", refreshed);
        Assert.DoesNotContain("|introduce]]", refreshed);
        Assert.Equal("introduce.md", Path.GetFileName(saved.AbsolutePath));
        var ownership = await File.ReadAllTextAsync(Path.Combine(vault.Root, "英语单词", ".vocabcollector", "note-state.json"));
        Assert.Contains("introduce.md", ownership);
        Assert.DoesNotContain("introdce.md", ownership);
    }

    private static WordAnalysis Analysis(string word) => TestData.Analysis with
    {
        PrimaryPartOfSpeech = PartOfSpeech.Verb,
        Meanings = [new(PartOfSpeech.Verb, "介绍；引入")],
        Examples = [new($"We {word} a new idea.", "我们介绍一个新想法。"), new($"They {word} their friends.", "他们介绍朋友。")],
        Collocations = [new($"{word} a friend", "介绍朋友"), new($"{word} a concept", "引入概念")],
        Related = [], Lemma = word, FormNote = "", Roots = [], Grammar = []
    };

    private static WordAnalysis Corrected() => Analysis("introduce") with
    { Spelling = new("introdce", "introduce", SpellingStatus.Corrected, []) };

    private static WordAnalysis Rejected(SpellingAssessment? spelling) => new(false, "untrusted provider error", "", "", PartOfSpeech.Other, [], [], [], [])
    { Spelling = spelling };

    private static Task<CaptureResult> Capture(TempVault vault, IWordAnalyzer analyzer, IVocabularyStore store,
        string input = "introdce", IProgress<CaptureStage>? progress = null, CancellationToken ct = default) =>
        new CaptureService(analyzer, store, TimeProvider.System).CaptureAsync(new(input, ""),
            new(new("https://unit.invalid/v1", "unit-key", "model"), vault.Options), progress, ct);

    private static void AssertCorrectedDuplicate(CaptureResult result)
    {
        Assert.Equal(CaptureStatus.AlreadyExists, result.Status);
        Assert.Equal("introduce", result.Word);
        Assert.Equal("introdce", result.CorrectedFrom);
        Assert.StartsWith("拼写已修正：introdce → introduce。", result.Message);
        Assert.Empty(result.Markdown);
    }

    private static void AssertNoSavedFiles(TempVault vault, CaptureResult result)
    {
        Assert.Empty(result.Markdown);
        Assert.Empty(result.Notes);
        Assert.Empty(Directory.GetFiles(vault.Root, "*.md", SearchOption.AllDirectories));
    }

    private sealed class FixedAnalyzer(WordAnalysis analysis) : IWordAnalyzer
    {
        public Task<WordAnalysis> AnalyzeAsync(string word, string context, ApiOptions options, CancellationToken ct) => Task.FromResult(analysis);
    }

    private sealed class CountingStore : IVocabularyStore
    {
        private readonly VaultStore inner = new();
        public int Scans { get; private set; }
        public Task<VaultIndex> ScanAsync(VaultOptions options, CancellationToken ct) { Scans++; return inner.ScanAsync(options, ct); }
        public Task<SavedNote> SaveNewAsync(string word, PartOfSpeech pos, Func<VaultIndex, string> render, VaultOptions options, CancellationToken ct) => inner.SaveNewAsync(word, pos, render, options, ct);
    }

    private sealed class CancelAfterCommitStore(CancellationTokenSource cancellation) : IVocabularyStore
    {
        private readonly VaultStore inner = new();
        public Task<VaultIndex> ScanAsync(VaultOptions options, CancellationToken ct) => inner.ScanAsync(options, ct);
        public async Task<SavedNote> SaveNewAsync(string word, PartOfSpeech pos, Func<VaultIndex, string> render, VaultOptions options, CancellationToken ct)
        { var result = await inner.SaveNewAsync(word, pos, render, options, ct); cancellation.Cancel(); return result; }
    }
}
